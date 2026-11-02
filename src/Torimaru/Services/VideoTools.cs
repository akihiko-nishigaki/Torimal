using System.Text;

namespace Torimaru.App.Services;

/// <summary>動画の加工結果。</summary>
public sealed record VideoOperationResult(bool Success, string Message, string? OutputPath = null);

/// <summary>
/// 録画した動画に対する後処理。すべて ffmpeg を呼ぶ薄いラッパー。
/// </summary>
public static class VideoTools
{
    /// <summary>映像と音声（0〜2 本）を 1 つの MP4 にまとめる。</summary>
    public static async Task<VideoOperationResult> MuxAsync(
        string videoPath, IReadOnlyList<string> audioPaths, string outputPath)
    {
        var usable = audioPaths.Where(p => File.Exists(p) && new FileInfo(p).Length > 1024).ToList();

        if (usable.Count == 0)
        {
            // 音声が無ければ映像をそのまま採用する
            File.Move(videoPath, outputPath, overwrite: true);
            return new VideoOperationResult(true, "音声なしで保存しました", outputPath);
        }

        var arguments = new StringBuilder("-y -hide_banner -loglevel error ");
        arguments.Append("-i ").Append(Ffmpeg.Quote(videoPath)).Append(' ');

        foreach (var audio in usable)
        {
            arguments.Append("-i ").Append(Ffmpeg.Quote(audio)).Append(' ');
        }

        if (usable.Count == 1)
        {
            arguments.Append("-map 0:v -map 1:a ");
        }
        else
        {
            // システム音声とマイクを 1 本に混ぜる
            arguments.Append("-filter_complex \"[1:a][2:a]amix=inputs=2:duration=longest:normalize=0[a]\" ");
            arguments.Append("-map 0:v -map \"[a]\" ");
        }

        arguments.Append("-c:v copy -c:a aac -b:a 192k -shortest ");
        arguments.Append(Ffmpeg.Quote(outputPath));

        var result = await Ffmpeg.RunAsync(arguments.ToString());

        if (result.Succeeded && File.Exists(outputPath))
        {
            return new VideoOperationResult(true, "音声を結合しました", outputPath);
        }

        // 結合に失敗しても、録れた映像は救う
        AppLog.Error($"音声の結合に失敗しました: {result.Output}");
        File.Move(videoPath, outputPath, overwrite: true);
        return new VideoOperationResult(true, "音声の結合に失敗したため、映像のみ保存しました", outputPath);
    }

    /// <summary>複数の MP4 を順につなぐ（一時停止をはさんだ録画の結合に使う）。</summary>
    public static async Task<VideoOperationResult> ConcatAsync(
        IReadOnlyList<string> inputs, string outputPath)
    {
        if (inputs.Count == 0)
        {
            return new VideoOperationResult(false, "結合する動画がありません");
        }

        if (inputs.Count == 1)
        {
            File.Move(inputs[0], outputPath, overwrite: true);
            return new VideoOperationResult(true, "保存しました", outputPath);
        }

        var listPath = Path.Combine(
            Path.GetDirectoryName(outputPath) ?? Path.GetTempPath(),
            $"concat-{Guid.NewGuid():N}.txt");

        try
        {
            // concat デマルチプレクサ用のリスト。シングルクォートはエスケープが必要。
            // BOM を付けると ffmpeg が 1 行目を "unknown keyword" として弾くため、BOM なしで書く。
            var lines = inputs.Select(p => $"file '{p.Replace("'", @"'\''")}'");
            await File.WriteAllLinesAsync(
                listPath, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var copyResult = await Ffmpeg.RunAsync(
                $"-y -hide_banner -loglevel error -f concat -safe 0 -i {Ffmpeg.Quote(listPath)} "
                + $"-c copy {Ffmpeg.Quote(outputPath)}");

            if (copyResult.Succeeded && File.Exists(outputPath))
            {
                return new VideoOperationResult(true, $"{inputs.Count} 本を結合しました", outputPath);
            }

            // 無劣化結合が通らない組み合わせでは再エンコードで繋ぐ
            AppLog.Info("無劣化での結合に失敗したため再エンコードします");

            var encodeResult = await Ffmpeg.RunAsync(
                $"-y -hide_banner -loglevel error -f concat -safe 0 -i {Ffmpeg.Quote(listPath)} "
                + $"-c:v libx264 -preset veryfast -crf 22 -pix_fmt yuv420p -c:a aac -b:a 192k "
                + Ffmpeg.Quote(outputPath));

            return encodeResult.Succeeded && File.Exists(outputPath)
                ? new VideoOperationResult(true, $"{inputs.Count} 本を再エンコードして結合しました", outputPath)
                : new VideoOperationResult(false, $"結合に失敗しました\n{encodeResult.Output}");
        }
        finally
        {
            TryDelete(listPath);
        }
    }

    /// <summary>指定時刻のフレームを PNG として書き出す。</summary>
    public static async Task<VideoOperationResult> ExtractFrameAsync(
        string videoPath, TimeSpan position, string outputPath)
    {
        // -ss を -i より前に置くと高速（キーフレーム単位）、後ろだと正確。
        // フレーム書き出しは正確さを優先する。
        var result = await Ffmpeg.RunAsync(
            $"-y -hide_banner -loglevel error -i {Ffmpeg.Quote(videoPath)} "
            + $"-ss {Ffmpeg.FormatTime(position)} -frames:v 1 {Ffmpeg.Quote(outputPath)}");

        return result.Succeeded && File.Exists(outputPath)
            ? new VideoOperationResult(true, "フレームを書き出しました", outputPath)
            : new VideoOperationResult(false, $"フレームを書き出せませんでした\n{result.Output}");
    }

    /// <summary>動画の一部を切り出す。</summary>
    /// <param name="reencode">true なら指定時刻どおりに正確に切る（遅い）。false はキーフレーム単位で高速。</param>
    public static async Task<VideoOperationResult> TrimAsync(
        string videoPath, TimeSpan start, TimeSpan end, string outputPath, bool reencode)
    {
        if (end <= start)
        {
            return new VideoOperationResult(false, "終了位置は開始位置より後にしてください");
        }

        var codecs = reencode
            ? "-c:v libx264 -preset veryfast -crf 22 -pix_fmt yuv420p -c:a aac -b:a 192k"
            : "-c copy";

        var result = await Ffmpeg.RunAsync(
            $"-y -hide_banner -loglevel error -ss {Ffmpeg.FormatTime(start)} -to {Ffmpeg.FormatTime(end)} "
            + $"-i {Ffmpeg.Quote(videoPath)} {codecs} {Ffmpeg.Quote(outputPath)}");

        return result.Succeeded && File.Exists(outputPath)
            ? new VideoOperationResult(
                true,
                $"{(end - start).TotalSeconds:0.#} 秒を切り出しました",
                outputPath)
            : new VideoOperationResult(false, $"切り出しに失敗しました\n{result.Output}");
    }

    /// <summary>一覧表示用のサムネイルを作る。</summary>
    public static async Task<bool> TryCreateThumbnailAsync(string videoPath, string outputPath, int width = 160)
    {
        var result = await Ffmpeg.RunAsync(
            $"-y -hide_banner -loglevel error -ss 0.5 -i {Ffmpeg.Quote(videoPath)} "
            + $"-frames:v 1 -vf scale={width}:-1 {Ffmpeg.Quote(outputPath)}");

        return result.Succeeded && File.Exists(outputPath);
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error($"一時ファイルを削除できませんでした: {path}", ex);
        }
    }
}
