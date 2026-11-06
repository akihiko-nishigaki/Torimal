using System.Text;
using System.Windows;
using Torimaru.App.Interop;
using Torimaru.App.Services;
using Torimaru.Core;

namespace Torimaru.App;

/// <summary>
/// トレイ常駐のエントリポイント。ウィンドウを持たず、
/// ホットキーとトレイメニューからキャプチャを起動する。
/// </summary>
public partial class App : Application
{
    private const string SingleInstanceName = @"Local\Torimaru.SingleInstance";

    private Mutex? _instanceLock;
    private HotKeyManager? _hotKeys;
    private System.Windows.Forms.NotifyIcon? _tray;
    private CaptureWorkflow? _workflow;
    private AppSettings _settings = new();

    /// <summary>ホットキー名 -> トレイメニュー項目。登録結果が出てから実キーを表示するために使う。</summary>
    private readonly Dictionary<string, System.Windows.Forms.ToolStripMenuItem> _hotKeyMenuItems = [];
    private string _hotKeySummary = "ホットキーはまだ登録されていません。";

    /// <summary>「前回と同じ範囲」の項目。開くたびに覚えている大きさを書き込む。</summary>
    private System.Windows.Forms.ToolStripMenuItem? _lastRegionItem;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        // 自己診断モード: GUI を出さずに描画・保存経路を通して結果を出力する。
        // OCR が非同期 (WinRT) なので、メッセージループを止めずに実行する必要がある。
        if (e.Args.Any(arg => string.Equals(arg, "--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            _ = Dispatcher.InvokeAsync(async () =>
            {
                var exitCode = 1;
                try
                {
                    exitCode = await Diagnostics.SelfCheck.RunAsync();
                }
                catch (Exception ex)
                {
                    AppLog.Error("自己診断が異常終了", ex);
                }

                Shutdown(exitCode);
            });

            return;
        }

        // 二重起動するとホットキーを奪い合うので 1 つに限る
        _instanceLock = new Mutex(initiallyOwned: true, SingleInstanceName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "Torimaru はすでに起動しています（通知領域のアイコンを確認してください）。",
                "Torimaru",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            Shutdown();
            return;
        }

        AppLog.Info("起動しました");

        _settings = AppSettings.Load();
        Services.Ffmpeg.ConfiguredPath = _settings.FfmpegPath;

        // 初回起動時に設定ファイルを作り、編集できる場所を示す
        if (!File.Exists(AppSettings.SettingsPath()))
        {
            TrySaveSettings();
        }

        _workflow = new CaptureWorkflow(_settings)
        {
            Notify = ShowBalloon,
        };

        SetupTray();
        SetupHotKeys();
    }

    /// <summary>
    /// ホットキーを（付け直しも含めて）登録する。
    /// 設定画面で割り当てを変えたあとにも、そのまま呼んで差し替えられる。
    /// </summary>
    private void SetupHotKeys()
    {
        _hotKeys ??= new HotKeyManager();
        _hotKeys.UnregisterAll();

        var region = RegisterAction(HotKeyActions.CaptureRegion, () => _workflow?.CaptureInteractive());
        RegisterAction(HotKeyActions.CaptureLastRegion, () => _workflow?.CaptureLastRegion());
        RegisterAction(HotKeyActions.CaptureAllScreens, () => _workflow?.CaptureAllScreens());
        RegisterAction(HotKeyActions.CaptureScrolling, () => _workflow?.CaptureScrolling());
        RegisterAction(HotKeyActions.CaptureDelayed, () => _workflow?.CaptureDelayed());
        RegisterAction(HotKeyActions.PickColor, () => _workflow?.PickColor());
        RegisterAction(HotKeyActions.StartRecording, () => _workflow?.StartRecording());
        RegisterAction(HotKeyActions.ShowWorkspace, () => _workflow?.ShowWorkspace());

        var summary = new StringBuilder();
        foreach (var registration in _hotKeys.Registrations)
        {
            summary.Append(registration.Name)
                .Append(": ")
                .Append(registration.Succeeded ? registration.DisplayKeys : "登録できませんでした")
                .AppendLine();
        }

        var text = summary.ToString().TrimEnd();
        _hotKeySummary = text;
        AppLog.Info("ホットキー\n" + text);

        // メニューはホットキー登録より先に組み立てているので、ここで実キーを書き込む
        foreach (var registration in _hotKeys.Registrations)
        {
            ApplyShortcutLabel(registration);
        }

        // NotifyIcon.Text は 127 文字までしか受け付けず、超えると例外になる。
        // ツールチップは要点だけにして、全体はバルーンとログへ回す。
        SetTrayTooltip($"Torimaru — キャプチャ: {(region.Succeeded ? region.DisplayKeys : "未登録")}");

        ShowBalloon(text);

        if (!region.Succeeded)
        {
            AppLog.Error("範囲キャプチャのホットキーをどの候補でも登録できませんでした");
        }
    }

    /// <summary>
    /// 1 つの操作にホットキーを付ける。
    ///
    /// この端末で設定した割り当てを最優先で試し、取れなければ既定の候補へ落とす。
    /// PrintScreen は Windows 11 の「Snipping Tool を開く」設定に取られることがあるため、
    /// 候補を順に試して実際に有効になったキーを返す。
    /// </summary>
    private HotKeyRegistration RegisterAction(HotKeyAction action, Action handler)
    {
        var candidates = new List<HotKeySpec>();

        if (_settings.FindHotKey(action.Id) is { } configured)
        {
            candidates.Add(configured);
        }

        foreach (var fallback in action.DefaultSpecs())
        {
            if (!candidates.Contains(fallback))
            {
                candidates.Add(fallback);
            }
        }

        return _hotKeys!.Register(action.DisplayName, handler, candidates);
    }

    /// <summary>ホットキーの割り当てを変える画面を出す。</summary>
    private void ShowHotKeySettings()
    {
        // 画面を開いている間は自分のホットキーを全部外す。
        // 付けたままだと、キーを選ぼうとした瞬間にキャプチャが始まってしまい、
        // 「そのキーは空いているか」の判定も自分自身に邪魔される。
        _hotKeys?.UnregisterAll();

        var window = new Torimaru.App.Settings.HotKeySettingsWindow(
            _settings, HotKeyManager.IsAvailable);

        window.ShowDialog();

        if (window.Changed)
        {
            TrySaveSettings();
        }

        // 変更が無くても必ず付け直す（外したままにしない）
        SetupHotKeys();
    }

    private void SetupTray()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();

        // 第 3 引数は SetupHotKeys 側の登録名。一致する項目の右側に実際のキーを表示する。
        AddMenuItem(menu, "キャプチャ（範囲・ウィンドウ）", HotKeyActions.CaptureRegion.DisplayName, () => _workflow?.CaptureInteractive());
        _lastRegionItem = AddMenuItem(
            menu,
            "前回と同じ範囲でキャプチャ",
            HotKeyActions.CaptureLastRegion.DisplayName,
            () => _workflow?.CaptureLastRegion());

        AddMenuItem(menu, "全画面をキャプチャ", HotKeyActions.CaptureAllScreens.DisplayName, () => _workflow?.CaptureAllScreens());
        AddMenuItem(menu, "スクロールキャプチャ", HotKeyActions.CaptureScrolling.DisplayName, () => _workflow?.CaptureScrolling());
        AddMenuItem(
            menu,
            $"遅延キャプチャ（{_settings.DelaySeconds} 秒）",
            HotKeyActions.CaptureDelayed.DisplayName,
            () => _workflow?.CaptureDelayed());
        AddMenuItem(menu, "画面の色を採取", HotKeyActions.PickColor.DisplayName, () => _workflow?.PickColor());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        AddMenuItem(menu, "画面を録画（MP4）", HotKeyActions.StartRecording.DisplayName, () => _workflow?.StartRecording());
        AddMenuItem(menu, "動画ツール（トリム・結合）", null, () => _workflow?.OpenVideoTools());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        AddMenuItem(menu, "ワークスペースを表示", HotKeyActions.ShowWorkspace.DisplayName, () => _workflow?.ShowWorkspace());
        AddMenuItem(menu, "保存フォルダを開く", null, () => _workflow?.OpenSaveFolder());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        AddMenuItem(menu, "ショートカットキーの設定…", null, ShowHotKeySettings);
        AddMenuItem(menu, "ショートカットキー一覧…", null, ShowHotKeyList);
        AddMenuItem(menu, "設定ファイルを開く", null, OpenSettingsFile);
        AddMenuItem(menu, "ログを開く", null, OpenLogFile);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        AddMenuItem(menu, "終了", null, () => Shutdown());

        // 覚えている範囲は撮るたびに変わるので、メニューを開く直前に書き直す
        menu.Opening += (_, _) => RefreshLastRegionItem();
        RefreshLastRegionItem();

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Visible = true,
            Text = "Torimaru",
            ContextMenuStrip = menu,
        };

        _tray.DoubleClick += (_, _) => _workflow?.ShowWorkspace();
    }

    /// <summary>
    /// トレイメニューに項目を 1 つ足す。
    /// <paramref name="hotKeyName"/> を渡した項目は、ホットキー登録後に実際のキーが右側へ入る。
    /// </summary>
    private System.Windows.Forms.ToolStripMenuItem AddMenuItem(
        System.Windows.Forms.ContextMenuStrip menu, string text, string? hotKeyName, Action action)
    {
        var item = new System.Windows.Forms.ToolStripMenuItem(text);
        item.Click += (_, _) => action();

        if (hotKeyName is not null)
        {
            // 実キーは SetupHotKeys の結果が出てから入れる（この時点では空のまま）
            item.ShowShortcutKeys = true;
            _hotKeyMenuItems[hotKeyName] = item;
        }

        menu.Items.Add(item);
        return item;
    }

    /// <summary>覚えている範囲の大きさをメニューに出す。</summary>
    private void RefreshLastRegionItem()
    {
        if (_lastRegionItem is null)
        {
            return;
        }

        if (_settings.LastRegion is { IsUsable: true } region)
        {
            _lastRegionItem.Text = $"前回と同じ範囲でキャプチャ（{region.Width} x {region.Height}）";
            _lastRegionItem.ToolTipText = region.Describe();
        }
        else
        {
            _lastRegionItem.Text = "前回と同じ範囲でキャプチャ（まだありません）";
            _lastRegionItem.ToolTipText = "一度キャプチャすると、その範囲を覚えます。";
        }
    }

    /// <summary>登録できた実際のキーをメニュー項目の右側に表示する。</summary>
    private void ApplyShortcutLabel(HotKeyRegistration registration)
    {
        if (!_hotKeyMenuItems.TryGetValue(registration.Name, out var item))
        {
            return;
        }

        item.ShortcutKeyDisplayString = registration.Succeeded ? registration.DisplayKeys : "（未登録）";

        if (!registration.Succeeded)
        {
            item.ToolTipText = "ホットキーを登録できませんでした。メニューからは実行できます。";
        }
    }

    /// <summary>実際に有効になっているホットキーの一覧を、コピーできる形で見せる。</summary>
    private void ShowHotKeyList()
    {
        var window = new Editor.TextResultWindow(
            "ショートカットキー一覧",
            "このPCで実際に有効になっているキーです。"
            + "候補キーが他のアプリに取られていた場合は、代替キーへ自動で切り替わります。"
            + "変えたいときは「ショートカットキーの設定…」から割り当て直せます。",
            _hotKeySummary);

        window.Show();
        window.Activate();
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var resource = GetResourceStream(new Uri("assets/app.ico", UriKind.Relative));
            if (resource is not null)
            {
                using var stream = resource.Stream;
                return new System.Drawing.Icon(stream);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("トレイアイコンの読み込みに失敗", ex);
        }

        return System.Drawing.SystemIcons.Application;
    }

    /// <summary>トレイのツールチップを安全に設定する（上限を超えたら切り詰める）。</summary>
    private void SetTrayTooltip(string text)
    {
        if (_tray is null)
        {
            return;
        }

        const int maxLength = 127;
        _tray.Text = text.Length <= maxLength ? text : text[..(maxLength - 1)] + "…";
    }

    private void ShowBalloon(string message)
    {
        try
        {
            _tray?.ShowBalloonTip(3000, "Torimaru", message, System.Windows.Forms.ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            AppLog.Error("通知の表示に失敗", ex);
        }
    }

    private void TrySaveSettings()
    {
        try
        {
            _settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("設定の保存に失敗", ex);
        }
    }

    private void OpenSettingsFile()
    {
        var path = AppSettings.SettingsPath();
        if (!File.Exists(path))
        {
            TrySaveSettings();
        }

        ShellOpener.OpenPath(path);
    }

    private static void OpenLogFile()
    {
        if (!File.Exists(AppLog.LogPath))
        {
            AppLog.Info("ログを開きました");
        }

        ShellOpener.OpenPath(AppLog.LogPath);
    }

    /// <summary>
    /// 常駐アプリなので、未処理例外でプロセスごと落とさない。
    /// </summary>
    private void OnDispatcherUnhandledException(
        object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("未処理の例外", e.Exception);

        MessageBox.Show(
            $"予期しないエラーが発生しました。処理は中断されましたが、アプリは動作を続けます。\n\n"
            + $"{e.Exception.Message}\n\nログ: {AppLog.LogPath}",
            "Torimaru",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        e.Handled = true;
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        AppLog.Info("終了しました");

        _hotKeys?.Dispose();

        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }

        _instanceLock?.Dispose();
    }
}
