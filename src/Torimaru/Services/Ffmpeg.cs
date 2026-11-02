using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Torimaru.App.Services;

/// <summary>ffmpeg の実行結果。</summary>
public sealed record FfmpegResult(int ExitCode, string Output)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// ffmpeg の場所の解決と実行をまとめる。
/// ffprobe は無い環境もあるため、再生時間の取得も ffmpeg の出力から行う。
/// </summary>
public static partial class Ffmpeg
{
    private static string? _cachedPath;

    /// <summary>設定で明示されたパス（トレイメニューの設定ファイルから指定できる）。</summary>
    public static string? ConfiguredPath { get; set; }

    /// <summary>ffmpeg.exe の場所。見つからなければ null。</summary>
    public static string? ResolvePath()
    {
        if (_cachedPath is not null && File.Exists(_cachedPath))
        {
            return _cachedPath;
        }

        foreach (var candidate in EnumerateCandidates())
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                _cachedPath = candidate;
                return _cachedPath;
            }
        }

        return null;
    }

    public static bool IsAvailable => ResolvePath() is not null;

    /// <summary>見つからない場合にユーザーへ出す案内。</summary>
    public const string MissingMessage =
        "ffmpeg が見つかりませんでした。\n\n"
        + "動画の録画・編集には ffmpeg が必要です。\n"
        + "・winget install Gyan.FFmpeg でインストールする\n"
        + "・または settings.json の FfmpegPath に ffmpeg.exe のフルパスを書く\n"
        + "のいずれかを行ってください。";

    private static IEnumerable<string?> EnumerateCandidates()
    {
        yield return ConfiguredPath;

        // PATH を順に見る
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(directory.Trim('"'), "ffmpeg.exe");
            }
            catch (ArgumentException)
            {
                continue;
            }

            yield return candidate;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "ffmpeg.exe");
        yield return @"C:\ffmpeg\bin\ffmpeg.exe";
        yield return @"C:\Program Files\ffmpeg\bin\ffmpeg.exe";
    }

    /// <summary>ffmpeg を実行して終わるまで待つ。</summary>
    public static async Task<FfmpegResult> RunAsync(string arguments, CancellationToken cancellationToken = default)
    {
        var executable = ResolvePath();
        if (executable is null)
        {
            return new FfmpegResult(-1, MissingMessage);
        }

        using var process = new Process
        {
            StartInfo = CreateStartInfo(executable, arguments),
            EnableRaisingEvents = true,
        };

        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => Append(output, e.Data);
        process.ErrorDataReceived += (_, e) => Append(output, e.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(cancellationToken);

        return new FfmpegResult(process.ExitCode, output.ToString());
    }

    private static void Append(StringBuilder builder, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (builder)
        {
            builder.AppendLine(line);
        }
    }

    public static ProcessStartInfo CreateStartInfo(string executable, string arguments) => new()
    {
        FileName = executable,
        Arguments = arguments,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
    };

    /// <summary>
    /// 動画の再生時間を得る。ffprobe が無い環境でも動くよう、
    /// ffmpeg が標準エラーへ出す "Duration: 00:00:12.34" を読む。
    /// </summary>
    public static async Task<TimeSpan?> TryGetDurationAsync(string path)
    {
        // 出力を指定しないと ffmpeg は終了コード 1 で終わるが、情報は表示される
        var result = await RunAsync($"-hide_banner -i {Quote(path)}");
        var match = DurationPattern().Match(result.Output);

        if (!match.Success)
        {
            return null;
        }

        return TimeSpan.FromHours(double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
               + TimeSpan.FromMinutes(double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture))
               + TimeSpan.FromSeconds(double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture));
    }

    [GeneratedRegex(@"Duration:\s*(\d+):(\d+):(\d+\.?\d*)")]
    private static partial Regex DurationPattern();

    /// <summary>引数に使うためパスを引用符で囲む。</summary>
    public static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    /// <summary>秒数を ffmpeg の時刻表記にする。</summary>
    public static string FormatTime(TimeSpan time)
        => time.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
}
