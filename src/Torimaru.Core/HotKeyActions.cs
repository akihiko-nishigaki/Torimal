namespace Torimaru.Core;

/// <summary>
/// ホットキーを割り当てられる操作 1 つ分。
/// </summary>
/// <param name="Id">設定ファイルに書くキー名。表示名を変えても設定が壊れないよう別にしている。</param>
/// <param name="DisplayName">画面に出す名前。</param>
/// <param name="DefaultKeys">
/// 既定のキー候補。先頭から順に試し、他のアプリに取られていたら次へ進む。
/// </param>
public sealed record HotKeyAction(string Id, string DisplayName, string[] DefaultKeys);

/// <summary>
/// ホットキーを割り当てられる操作の一覧。
/// 端末ごとの割り当ては <see cref="AppSettings.HotKeys"/> に Id をキーにして保存する。
/// </summary>
public static class HotKeyActions
{
    public static readonly HotKeyAction CaptureRegion = new(
        "CaptureRegion",
        "範囲キャプチャ",
        ["PrintScreen", "Ctrl+Shift+PrintScreen", "Ctrl+Shift+S"]);

    public static readonly HotKeyAction CaptureAllScreens = new(
        "CaptureAllScreens",
        "全画面キャプチャ",
        ["Ctrl+PrintScreen", "Ctrl+Alt+PrintScreen", "Ctrl+Alt+P"]);

    public static readonly HotKeyAction CaptureScrolling = new(
        "CaptureScrolling",
        "スクロールキャプチャ",
        ["Ctrl+Shift+L"]);

    public static readonly HotKeyAction CaptureDelayed = new(
        "CaptureDelayed",
        "遅延キャプチャ",
        ["Ctrl+Shift+D", "Ctrl+Alt+D", "Ctrl+Shift+F9"]);

    public static readonly HotKeyAction PickColor = new(
        "PickColor",
        "色の採取",
        ["Ctrl+Shift+C", "Ctrl+Alt+C"]);

    public static readonly HotKeyAction StartRecording = new(
        "StartRecording",
        "画面録画",
        ["Ctrl+Shift+R", "Ctrl+Alt+R"]);

    public static readonly HotKeyAction ShowWorkspace = new(
        "ShowWorkspace",
        "ワークスペース",
        ["Ctrl+Shift+W"]);

    /// <summary>設定画面に出す順番。</summary>
    public static readonly HotKeyAction[] All =
    [
        CaptureRegion,
        CaptureAllScreens,
        CaptureScrolling,
        CaptureDelayed,
        PickColor,
        StartRecording,
        ShowWorkspace,
    ];

    public static HotKeyAction? Find(string? id)
        => id is null
            ? null
            : Array.Find(All, action => string.Equals(action.Id, id, StringComparison.Ordinal));

    /// <summary>既定のキー候補を解釈済みの形で返す（書き間違いは無視する）。</summary>
    public static List<HotKeySpec> DefaultSpecs(this HotKeyAction action)
    {
        var specs = new List<HotKeySpec>(action.DefaultKeys.Length);
        foreach (var text in action.DefaultKeys)
        {
            if (HotKeySpec.TryParse(text, out var spec) && !specs.Contains(spec))
            {
                specs.Add(spec);
            }
        }

        return specs;
    }
}
