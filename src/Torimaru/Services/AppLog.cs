using System.Text;

namespace Torimaru.App;

/// <summary>
/// 常駐アプリなので、落ちた理由を後から追えるようにする最小のログ。
/// ログ自体の失敗でアプリを止めないことを優先している。
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();

    private const long MaxBytes = 512 * 1024;

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Torimaru",
        "app.log");

    public static void Info(string message) => Write("INFO ", message, null);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                var dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                RotateIfNeeded();

                var builder = new StringBuilder();
                builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                    .Append(' ')
                    .Append(level)
                    .Append(' ')
                    .Append(message);

                if (ex is not null)
                {
                    builder.AppendLine()
                        .Append("    ")
                        .Append(ex.GetType().FullName)
                        .Append(": ")
                        .Append(ex.Message)
                        .AppendLine()
                        .Append(ex.StackTrace);
                }

                File.AppendAllText(LogPath, builder.ToString() + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // ログ書き込みの失敗は無視する（ここで投げるとアプリが落ちる）
        }
    }

    private static void RotateIfNeeded()
    {
        var info = new FileInfo(LogPath);
        if (!info.Exists || info.Length < MaxBytes)
        {
            return;
        }

        var backup = LogPath + ".1";
        if (File.Exists(backup))
        {
            File.Delete(backup);
        }

        File.Move(LogPath, backup);
    }
}
