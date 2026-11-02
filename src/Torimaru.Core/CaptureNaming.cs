using System.Text;

namespace Torimaru.Core;

/// <summary>
/// 保存ファイル名を決める。「ファイル名を気にしなくていい」体験の中核。
/// </summary>
public static class CaptureNaming
{
    /// <summary>注釈前のオリジナルを置くサブフォルダ名。</summary>
    public const string OriginalsFolderName = ".originals";

    /// <summary>
    /// 衝突しないファイルパスを作る。既に存在する場合は -2, -3 ... を付ける。
    /// </summary>
    /// <param name="folder">保存先フォルダ。</param>
    /// <param name="timestamp">ファイル名に使う日時。</param>
    /// <param name="pattern">日時書式。</param>
    /// <param name="extension">拡張子（ドット付き）。</param>
    /// <param name="exists">存在判定。テスト時に差し替える。</param>
    public static string BuildUniquePath(
        string folder,
        DateTime timestamp,
        string pattern = "yyyy-MM-dd_HH-mm-ss",
        string extension = ".png",
        Func<string, bool>? exists = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        exists ??= File.Exists;

        var stem = Sanitize(timestamp.ToString(pattern, System.Globalization.CultureInfo.InvariantCulture));
        if (stem.Length == 0)
        {
            stem = "capture";
        }

        var candidate = Path.Combine(folder, stem + extension);
        var index = 2;
        while (exists(candidate))
        {
            candidate = Path.Combine(folder, $"{stem}-{index}{extension}");
            index++;

            // 理論上到達しないが、無限ループは作らない
            if (index > 100000)
            {
                candidate = Path.Combine(folder, $"{stem}-{Guid.NewGuid():N}{extension}");
                break;
            }
        }

        return candidate;
    }

    /// <summary>対応するオリジナル画像のパスを返す。</summary>
    public static string OriginalPathFor(string flattenedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flattenedPath);
        var dir = Path.GetDirectoryName(flattenedPath) ?? string.Empty;
        return Path.Combine(dir, OriginalsFolderName, Path.GetFileName(flattenedPath));
    }

    /// <summary>PNG に埋め込む相対パス表記（区切りは / で統一）。</summary>
    public static string OriginalRelativePathFor(string flattenedPath)
        => OriginalsFolderName + "/" + Path.GetFileName(flattenedPath);

    /// <summary>埋め込まれた相対パスを実パスへ解決する。</summary>
    public static string? ResolveOriginalPath(string flattenedPath, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var dir = Path.GetDirectoryName(flattenedPath);
        if (string.IsNullOrEmpty(dir))
        {
            return null;
        }

        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(dir, normalized));
    }

    /// <summary>ファイル名に使えない文字を除去する。</summary>
    public static string Sanitize(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '-' : ch);
        }

        return builder.ToString().Trim().TrimEnd('.');
    }
}
