using System.Diagnostics;

namespace Torimaru.App.Services;

/// <summary>エクスプローラーや既定アプリでパスを開く。</summary>
public static class ShellOpener
{
    /// <summary>フォルダを開き、可能ならそのファイルを選択状態にする。</summary>
    public static void OpenFolderAndSelect(string? filePath, string folder)
    {
        try
        {
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"")
                {
                    UseShellExecute = true,
                });
                return;
            }

            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error($"フォルダを開けませんでした: {folder}", ex);
        }
    }

    public static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error($"開けませんでした: {path}", ex);
        }
    }
}
