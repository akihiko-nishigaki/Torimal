using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Torimaru.App.Editor;
using Torimaru.Core;

namespace Torimaru.App.Services;

/// <summary>PNG から読み戻した編集状態。</summary>
public sealed record LoadedCapture(BitmapSource BaseImage, AnnotationDocument Document);

/// <summary>
/// 保存・読み込み・クリップボードをまとめる。
///
/// 保存の設計:
///   shots\2026-10-31_18-24-39.png            注釈を焼き込んだ配布用 PNG（tEXt に注釈 JSON）
///   shots\.originals\2026-10-31_18-24-39.png  注釈前のオリジナル
/// こうすると、配布物は普通の PNG のまま、後から注釈だけをやり直せる。
/// </summary>
public static class CaptureSaver
{
    /// <summary>
    /// 撮った直後の画像を保存する（ファイル名はユーザーに尋ねない）。
    /// </summary>
    public static string SaveNewCapture(BitmapSource image, AppSettings settings)
    {
        Directory.CreateDirectory(settings.SaveFolder);

        var path = CaptureNaming.BuildUniquePath(
            settings.SaveFolder, DateTime.Now, settings.FileNamePattern);

        var bytes = EncodePng(image);
        File.WriteAllBytes(path, bytes);
        WriteOriginal(path, bytes, replaceExisting: false);

        return path;
    }

    /// <summary>注釈込みで上書き保存する。</summary>
    /// <param name="replaceOriginal">
    /// 控えのオリジナルを今の下地で置き換えるか。
    /// トリミングや中抜きで下地そのものを切ったときは必ず true にする。
    /// false のままだと切る前のオリジナルが残り、次に開いたときに
    /// 「寸法の違う画像に切った後の注釈が乗る」ことになる。
    /// </param>
    public static void SaveEdited(
        string path, BitmapSource baseImage, AnnotationDocument document, bool replaceOriginal = false)
    {
        var originalBytes = EncodePng(baseImage);
        WriteOriginal(path, originalBytes, replaceOriginal);

        document.OriginalRelativePath = CaptureNaming.OriginalRelativePathFor(path);

        var flattened = Flatten(baseImage, document);
        var payload = PngTextChunk.Embed(EncodePng(flattened), document.ToJson());

        File.WriteAllBytes(path, payload);
    }

    /// <summary>
    /// 注釈前のオリジナルを控えとして置く。
    /// 既にあるものは原則そのまま残す（オリジナルが失われていた場合に
    /// 焼き込み済みの絵で上書きしてしまうのを防ぐため）。
    /// </summary>
    private static void WriteOriginal(
        string flattenedPath, byte[] originalPngBytes, bool replaceExisting)
    {
        var originalPath = CaptureNaming.OriginalPathFor(flattenedPath);
        var dir = Path.GetDirectoryName(originalPath);
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);

        // 隠しフォルダにしてキャプチャ一覧をきれいに保つ
        try
        {
            var info = new DirectoryInfo(dir);
            if ((info.Attributes & FileAttributes.Hidden) == 0)
            {
                info.Attributes |= FileAttributes.Hidden;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("オリジナル用フォルダを隠し属性にできませんでした", ex);
        }

        if (replaceExisting || !File.Exists(originalPath))
        {
            File.WriteAllBytes(originalPath, originalPngBytes);
        }
    }

    /// <summary>
    /// 下地と注釈を 1 枚に焼き込む。画面表示と同じ <see cref="ShapeRenderer"/> を
    /// scale=1.0 で通すので、見た目どおりの結果になる。
    /// </summary>
    public static BitmapSource Flatten(BitmapSource baseImage, AnnotationDocument document)
    {
        var width = baseImage.PixelWidth;
        var height = baseImage.PixelHeight;

        var canvas = ShapeRenderer.BuildCanvas(baseImage, document, 1.0);
        canvas.Measure(new Size(width, height));
        canvas.Arrange(new Rect(0, 0, width, height));
        canvas.UpdateLayout();

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(canvas);
        target.Freeze();
        return target;
    }

    public static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// PNG を開く。注釈データが埋まっていればオリジナル画像 + 注釈として復元し、
    /// 無ければ普通の画像として扱う。
    /// </summary>
    public static LoadedCapture Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var document = AnnotationDocument.FromJson(PngTextChunk.ExtractWithLegacy(bytes));

        if (document is not null)
        {
            var originalPath = CaptureNaming.ResolveOriginalPath(path, document.OriginalRelativePath);
            if (originalPath is not null && File.Exists(originalPath))
            {
                return new LoadedCapture(DecodeImage(File.ReadAllBytes(originalPath)), document);
            }

            // オリジナルが失われている場合は、焼き込み済み画像を下地にする。
            // 既存の注釈をもう一度描くと二重になるため、注釈は捨てる。
            AppLog.Info($"オリジナル画像が見つからないため注釈を破棄します: {path}");
        }

        return new LoadedCapture(DecodeImage(bytes), new AnnotationDocument());
    }

    public static BitmapSource DecodeImage(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var decoder = BitmapDecoder.Create(
            stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    /// <summary>
    /// クリップボードへ入れる。貼り付け先の好みが分かれるため
    /// ビットマップと PNG の両方を載せる。
    /// </summary>
    public static bool TryCopyToClipboard(BitmapSource image)
    {
        var data = new DataObject();
        data.SetImage(image);

        try
        {
            data.SetData("PNG", new MemoryStream(EncodePng(image)), autoConvert: false);
        }
        catch (Exception ex)
        {
            AppLog.Error("クリップボードへの PNG 添付に失敗", ex);
        }

        // 他プロセスがクリップボードを握っていると失敗するので数回試す
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                System.Threading.Thread.Sleep(60);
            }
        }

        AppLog.Error("クリップボードへのコピーに失敗しました");
        return false;
    }
}
