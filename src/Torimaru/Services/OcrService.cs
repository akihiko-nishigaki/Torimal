using System.Text;

namespace Torimaru.App.Services;

/// <summary>OCR の実行結果。</summary>
public sealed record OcrOutcome(bool Success, string Text, int LineCount, string Message);

/// <summary>
/// 画像から文字を取り出す。Windows 標準の OCR エンジン（<c>Windows.Media.Ocr</c>）を使うので、
/// 追加のインストールも課金も不要。日本語は OS の言語パックがそのまま使われる。
/// </summary>
public static class OcrService
{
    /// <summary>OCR エンジンが使える環境かどうか。</summary>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                return Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages.Count > 0;
            }
            catch (Exception ex)
            {
                AppLog.Error("OCR エンジンの確認に失敗", ex);
                return false;
            }
        }
    }

    /// <summary>利用できる言語タグの一覧（例: ja, en-US）。</summary>
    public static IReadOnlyList<string> AvailableLanguages
    {
        get
        {
            try
            {
                return Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages
                    .Select(language => language.LanguageTag)
                    .ToList();
            }
            catch (Exception ex)
            {
                AppLog.Error("OCR 言語の列挙に失敗", ex);
                return [];
            }
        }
    }

    /// <summary>
    /// 画像から文字を取り出す。
    /// </summary>
    /// <param name="image">対象画像。</param>
    /// <param name="languageTag">使いたい言語タグ。空なら OS の既定。</param>
    public static async Task<OcrOutcome> RecognizeAsync(
        System.Windows.Media.Imaging.BitmapSource image, string? languageTag)
    {
        try
        {
            var engine = CreateEngine(languageTag);
            if (engine is null)
            {
                var available = AvailableLanguages;
                return new OcrOutcome(
                    false,
                    string.Empty,
                    0,
                    available.Count == 0
                        ? "この PC には OCR の言語パックが入っていません。[設定 → 時刻と言語 → 言語と地域] から言語を追加してください。"
                        : $"言語 '{languageTag}' の OCR が使えません（利用可能: {string.Join(", ", available)}）。");
            }

            var prepared = Prepare(image, engine);
            using var softwareBitmap = await ToSoftwareBitmapAsync(prepared);

            var result = await engine.RecognizeAsync(softwareBitmap);

            var builder = new StringBuilder();
            var lines = 0;
            foreach (var line in result.Lines)
            {
                builder.AppendLine(ComposeLine(line));
                lines++;
            }

            var text = builder.ToString().TrimEnd();
            return lines == 0
                ? new OcrOutcome(false, string.Empty, 0, "文字を検出できませんでした。")
                : new OcrOutcome(true, text, lines, $"{lines} 行を読み取りました");
        }
        catch (Exception ex)
        {
            AppLog.Error("OCR に失敗", ex);
            return new OcrOutcome(false, string.Empty, 0, $"OCR に失敗しました: {ex.Message}");
        }
    }

    /// <summary>
    /// 1 行分の文字列を組み立てる。
    ///
    /// Windows の OCR は単語単位で結果を返し、<c>OcrLine.Text</c> は
    /// それを常に半角空白で繋ぐ。日本語では 1 文字ずつが単語として扱われるため、
    /// そのままでは「画 面 キ ャ プ チ ャ」のようになり貼り付けて使えない。
    /// 両側とも欧文の場合にだけ空白を入れることで、日英どちらも自然に繋がる。
    /// </summary>
    private static string ComposeLine(Windows.Media.Ocr.OcrLine line)
    {
        var builder = new StringBuilder();
        char? previous = null;

        foreach (var word in line.Words)
        {
            var text = word.Text;
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            if (previous is char left && !IsCjk(left) && !IsCjk(text[0]))
            {
                builder.Append(' ');
            }

            builder.Append(text);
            previous = text[^1];
        }

        return builder.ToString();
    }

    /// <summary>日本語・中国語・韓国語など、単語間に空白を置かない文字かどうか。</summary>
    private static bool IsCjk(char value) => value switch
    {
        >= '　' and <= 'ヿ' => true,   // CJK 記号・ひらがな・カタカナ
        >= '㐀' and <= '䶿' => true,   // CJK 統合漢字拡張 A
        >= '一' and <= '鿿' => true,   // CJK 統合漢字
        >= '가' and <= '힯' => true,   // ハングル
        >= '豈' and <= '﫿' => true,   // CJK 互換漢字
        >= '＀' and <= '￯' => true,   // 全角・半角形
        _ => false,
    };

    private static Windows.Media.Ocr.OcrEngine? CreateEngine(string? languageTag)
    {
        if (!string.IsNullOrWhiteSpace(languageTag))
        {
            try
            {
                var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(
                    new Windows.Globalization.Language(languageTag));

                if (engine is not null)
                {
                    return engine;
                }
            }
            catch (ArgumentException)
            {
                // 言語タグが不正な場合は既定へ落とす
            }
        }

        return Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
    }

    /// <summary>
    /// OCR が得意なサイズへ整える。
    /// 小さすぎる切り抜きは拡大したほうが精度が出る一方、
    /// エンジンには最大寸法があるため大きすぎる画像は縮める。
    /// </summary>
    private static System.Windows.Media.Imaging.BitmapSource Prepare(
        System.Windows.Media.Imaging.BitmapSource image, Windows.Media.Ocr.OcrEngine engine)
    {
        var max = (int)Windows.Media.Ocr.OcrEngine.MaxImageDimension;
        var longest = Math.Max(image.PixelWidth, image.PixelHeight);

        double scale = 1;

        if (longest > max)
        {
            scale = (double)max / longest;
        }
        else if (image.PixelHeight < 120)
        {
            // 行が小さいほど認識が落ちるので、最大 4 倍まで引き伸ばす
            scale = Math.Min(4.0, 120.0 / Math.Max(1, image.PixelHeight));
        }

        if (Math.Abs(scale - 1) < 0.01)
        {
            return image;
        }

        var scaled = new System.Windows.Media.Imaging.TransformedBitmap(
            image, new System.Windows.Media.ScaleTransform(scale, scale));

        scaled.Freeze();
        return scaled;
    }

    /// <summary>WPF の画像を WinRT の <c>SoftwareBitmap</c> へ渡せる形に変換する。</summary>
    private static async Task<Windows.Graphics.Imaging.SoftwareBitmap> ToSoftwareBitmapAsync(
        System.Windows.Media.Imaging.BitmapSource image)
    {
        var png = CaptureSaver.EncodePng(image);

        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        using (var writer = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(png);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        stream.Seek(0);

        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        var bitmap = await decoder.GetSoftwareBitmapAsync();

        if (bitmap.BitmapPixelFormat == Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8
            && bitmap.BitmapAlphaMode != Windows.Graphics.Imaging.BitmapAlphaMode.Straight)
        {
            return bitmap;
        }

        var converted = Windows.Graphics.Imaging.SoftwareBitmap.Convert(
            bitmap,
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);

        bitmap.Dispose();
        return converted;
    }
}
