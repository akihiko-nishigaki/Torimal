using System.Windows;
using System.Windows.Media.Imaging;
using Torimaru.App.Capture;
using Torimaru.App.Editor;
using Torimaru.App.Interop;
using Torimaru.App.Overlay;
using Torimaru.App.Recording;
using Torimaru.App.Video;
using Torimaru.App.Workspace;
using Torimaru.Core;

namespace Torimaru.App.Services;

/// <summary>
/// 「撮る → 保存する → 編集する」の一連の流れをまとめる。
/// トレイメニュー・ホットキー・ワークスペースのどこから呼ばれても同じ経路を通す。
/// </summary>
public sealed class CaptureWorkflow
{
    private readonly AppSettings _settings;
    private readonly List<EditorWindow> _editors = [];
    private WorkspaceWindow? _workspace;
    private VideoToolsWindow? _videoTools;
    private VideoRecorder? _recorder;
    private RecordingBarWindow? _recordingBar;
    private bool _capturing;

    public CaptureWorkflow(AppSettings settings) => _settings = settings;

    /// <summary>キャプチャ完了をユーザーへ知らせる（トレイのバルーン等）。</summary>
    public Action<string>? Notify { get; set; }

    public AppSettings Settings => _settings;

    /// <summary>範囲・ウィンドウ・モニタを選んでキャプチャする。</summary>
    public async void CaptureInteractive()
    {
        if (_capturing)
        {
            return;
        }

        _capturing = true;
        try
        {
            var restoreWorkspace = await HideWorkspaceAsync();

            // オーバーレイ表示前に列挙する。こうすれば自分自身が候補に入らない
            var windows = WindowFinder.EnumerateVisibleWindows();
            var image = ScreenCapturer.CaptureVirtualScreen(_settings.CaptureCursor);

            var selector = new RegionSelectorWindow(
                image, windows, _settings.MagnifierZoom, SelectionPurpose.Region, RecallRect(image));

            var selected = selector.ShowDialog() == true ? selector.Result : null;

            if (restoreWorkspace)
            {
                _workspace?.Show();
            }

            if (selected is null)
            {
                return;
            }

            RememberRegion(ToVirtual(image, selected.Value));
            Finish(image.Crop(selected.Value));
        }
        catch (Exception ex)
        {
            Fail("キャプチャに失敗しました", ex);
        }
        finally
        {
            _capturing = false;
        }
    }

    /// <summary>全モニタをそのままキャプチャする。</summary>
    public async void CaptureAllScreens()
    {
        if (_capturing)
        {
            return;
        }

        _capturing = true;
        try
        {
            var restoreWorkspace = await HideWorkspaceAsync();
            var image = ScreenCapturer.CaptureVirtualScreen(_settings.CaptureCursor);

            if (restoreWorkspace)
            {
                _workspace?.Show();
            }

            RememberRegion(ToVirtual(image, image.FullBounds));
            Finish(image.Crop(image.FullBounds));
        }
        catch (Exception ex)
        {
            Fail("キャプチャに失敗しました", ex);
        }
        finally
        {
            _capturing = false;
        }
    }

    /// <summary>
    /// 前回撮った範囲をそのまま撮り直す。範囲を選ぶ手間が無いので、
    /// 同じ場所を何枚も撮るときに使う。
    /// </summary>
    public async void CaptureLastRegion()
    {
        if (_capturing)
        {
            return;
        }

        var (screenX, screenY, screenWidth, screenHeight) = ScreenCapturer.GetVirtualScreenBounds();

        // 覚えた範囲が今の画面に収まらないなら、黙って違う場所を撮らずに選び直させる
        if (RegionMemory.Resolve(_settings.LastRegion, screenX, screenY, screenWidth, screenHeight)
            is not { } region)
        {
            var reason = RegionMemory.ExplainMismatch(
                _settings.LastRegion, screenX, screenY, screenWidth, screenHeight);

            Notify?.Invoke($"{reason}。範囲を選んでください");
            CaptureInteractive();
            return;
        }

        _capturing = true;
        try
        {
            var restoreWorkspace = await HideWorkspaceAsync();
            var image = ScreenCapturer.CaptureVirtualScreen(_settings.CaptureCursor);

            if (restoreWorkspace)
            {
                _workspace?.Show();
            }

            Finish(
                image.Crop(ToImage(image, region)),
                $"前回と同じ範囲で撮りました（{region.Width:0} x {region.Height:0} px）");
        }
        catch (Exception ex)
        {
            Fail("前回と同じ範囲でキャプチャできませんでした", ex);
        }
        finally
        {
            _capturing = false;
        }
    }

    /// <summary>指定秒だけ待ってからキャプチャする（メニューを開いた状態などを撮るため）。</summary>
    public async void CaptureDelayed()
    {
        try
        {
            Notify?.Invoke($"{_settings.DelaySeconds} 秒後にキャプチャを開始します");
            await Task.Delay(TimeSpan.FromSeconds(_settings.DelaySeconds));
            CaptureInteractive();
        }
        catch (Exception ex)
        {
            Fail("遅延キャプチャに失敗しました", ex);
        }
    }

    /// <summary>
    /// 範囲を選んでからスクロールしながら撮り、1 枚の縦長画像に繋ぐ。
    /// </summary>
    public async void CaptureScrolling()
    {
        if (_capturing)
        {
            return;
        }

        _capturing = true;
        try
        {
            var restoreWorkspace = await HideWorkspaceAsync();

            var windows = WindowFinder.EnumerateVisibleWindows();
            var image = ScreenCapturer.CaptureVirtualScreen(includeCursor: false);

            var selector = new RegionSelectorWindow(
                image, windows, _settings.MagnifierZoom, SelectionPurpose.ScrollTarget, RecallRect(image));

            var selected = selector.ShowDialog() == true ? selector.Result : null;

            if (restoreWorkspace)
            {
                _workspace?.Show();
            }

            if (selected is null)
            {
                return;
            }

            // 画像座標 → 仮想画面座標（この後は実画面を直接撮るため）
            var region = ToVirtual(image, selected.Value);
            RememberRegion(region);

            var proceed = MessageBox.Show(
                "この範囲をスクロールしながら撮影します。\n\n"
                + "・開始するとマウスが範囲の中央へ移動し、自動でスクロールします\n"
                + "・撮りたい先頭の位置まで、あらかじめスクロールしておいてください\n"
                + "・途中で止めるときは Esc キーを押してください\n\n"
                + "開始しますか？",
                "スクロールキャプチャ",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Information);

            if (proceed != MessageBoxResult.OK)
            {
                return;
            }

            // ダイアログが消えて画面が描き直されるのを待つ
            await Task.Delay(250);

            Notify?.Invoke("スクロールキャプチャ中です（Esc で中止）");
            var result = await ScrollCapturer.CaptureAsync(region, _settings);

            if (result.Image is null)
            {
                Notify?.Invoke(result.Message);
                AppLog.Info($"スクロールキャプチャ: {result.Message}");
                return;
            }

            AppLog.Info($"スクロールキャプチャ: {result.Message}");
            Finish(result.Image, result.Message);
        }
        catch (Exception ex)
        {
            Fail("スクロールキャプチャに失敗しました", ex);
        }
        finally
        {
            _capturing = false;
        }
    }

    /// <summary>範囲を選んで画面録画を始める。停止するまで操作バーを出し続ける。</summary>
    public async void StartRecording()
    {
        if (_capturing || _recorder is { IsRecording: true })
        {
            if (_recordingBar is not null)
            {
                _recordingBar.Activate();
            }

            return;
        }

        if (!Ffmpeg.IsAvailable)
        {
            MessageBox.Show(Ffmpeg.MissingMessage, "Torimaru", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _capturing = true;
        try
        {
            var restoreWorkspace = await HideWorkspaceAsync();

            var windows = WindowFinder.EnumerateVisibleWindows();
            var image = ScreenCapturer.CaptureVirtualScreen(includeCursor: false);

            var selector = new RegionSelectorWindow(
                image, windows, _settings.MagnifierZoom, SelectionPurpose.RecordTarget, RecallRect(image));

            var selected = selector.ShowDialog() == true ? selector.Result : null;

            if (restoreWorkspace)
            {
                _workspace?.Show();
            }

            if (selected is null)
            {
                return;
            }

            var region = ToVirtual(image, selected.Value);
            RememberRegion(region);

            // オーバーレイが消えて画面が描き直されるのを待つ
            await Task.Delay(220);

            _recorder = new VideoRecorder(_settings);
            var started = await _recorder.StartAsync(region);

            if (!started.Success)
            {
                _recorder.Dispose();
                _recorder = null;
                MessageBox.Show(started.Message, "録画", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            AppLog.Info($"録画開始: {region.Width}x{region.Height}");

            _recordingBar = new RecordingBarWindow(_recorder, region);
            _recordingBar.StopRequested += FinishRecordingAsync;
            _recordingBar.CancelRequested += DiscardRecordingAsync;
            _recordingBar.Closed += (_, _) => _recordingBar = null;
            _recordingBar.Show();
        }
        catch (Exception ex)
        {
            Fail("録画を開始できませんでした", ex);
        }
        finally
        {
            _capturing = false;
        }
    }

    private async Task FinishRecordingAsync()
    {
        if (_recorder is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_settings.SaveFolder);

            var path = CaptureNaming.BuildUniquePath(
                _settings.SaveFolder, DateTime.Now, _settings.FileNamePattern, ".mp4");

            var result = await _recorder.StopAsync(path);
            AppLog.Info($"録画終了: {result.Message}");

            Notify?.Invoke(result.Success
                ? result.Message
                : $"録画を保存できませんでした: {result.Message}");

            _workspace?.Refresh();
        }
        catch (Exception ex)
        {
            Fail("録画の保存に失敗しました", ex);
        }
        finally
        {
            _recorder?.Dispose();
            _recorder = null;
        }
    }

    private async Task DiscardRecordingAsync()
    {
        if (_recorder is null)
        {
            return;
        }

        try
        {
            await _recorder.CancelAsync();
            Notify?.Invoke("録画を破棄しました");
        }
        catch (Exception ex)
        {
            AppLog.Error("録画の破棄に失敗", ex);
        }
        finally
        {
            _recorder.Dispose();
            _recorder = null;
        }
    }

    /// <summary>録画した動画をトリム・フレーム書き出しするウィンドウを開く。</summary>
    public void OpenVideoTools(string? path = null)
    {
        if (!Ffmpeg.IsAvailable)
        {
            MessageBox.Show(Ffmpeg.MissingMessage, "Torimaru", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            if (_videoTools is null)
            {
                _videoTools = new VideoToolsWindow(this);
                _videoTools.Closed += (_, _) => _videoTools = null;
                _videoTools.Show();
            }
            else
            {
                _videoTools.Show();
                _videoTools.Activate();
            }

            if (path is not null)
            {
                _videoTools.LoadVideo(path);
            }
        }
        catch (Exception ex)
        {
            Fail("動画ツールを開けませんでした", ex);
        }
    }

    /// <summary>画面上の 1 点の色を採取してクリップボードへ入れる。</summary>
    public async void PickColor()
    {
        if (_capturing)
        {
            return;
        }

        _capturing = true;
        try
        {
            var restoreWorkspace = await HideWorkspaceAsync();
            var image = ScreenCapturer.CaptureVirtualScreen(includeCursor: false);

            var selector = new RegionSelectorWindow(
                image, [], _settings.MagnifierZoom, SelectionPurpose.Color);

            var picked = selector.ShowDialog() == true ? selector.PickedColor : null;

            if (restoreWorkspace)
            {
                _workspace?.Show();
            }

            if (picked is null)
            {
                return;
            }

            var color = picked.Value;
            var hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";

            if (TrySetClipboardText(hex))
            {
                Notify?.Invoke($"{hex} をコピーしました（RGB {color.R}, {color.G}, {color.B}）");
            }
            else
            {
                Notify?.Invoke($"{hex}（クリップボードへのコピーに失敗しました）");
            }
        }
        catch (Exception ex)
        {
            Fail("色の採取に失敗しました", ex);
        }
        finally
        {
            _capturing = false;
        }
    }

    private static bool TrySetClipboardText(string text)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(60);
            }
        }

        return false;
    }

    /// <summary>画像ピクセル座標の範囲を仮想画面座標へ移す。</summary>
    private static PixelRect ToVirtual(CapturedImage image, PixelRect imageRect)
        => new(
            imageRect.X + image.OriginX,
            imageRect.Y + image.OriginY,
            imageRect.Width,
            imageRect.Height);

    /// <summary>仮想画面座標の範囲を画像ピクセル座標へ移す。</summary>
    private static PixelRect ToImage(CapturedImage image, PixelRect virtualRect)
        => new(
            virtualRect.X - image.OriginX,
            virtualRect.Y - image.OriginY,
            virtualRect.Width,
            virtualRect.Height);

    /// <summary>
    /// 撮った範囲を覚えて設定に残す。
    ///
    /// 静止画・スクロール・録画のどの経路からも通すので、
    /// 「前回と同じ範囲」は常に最後に選んだ範囲になる。
    /// </summary>
    private void RememberRegion(PixelRect virtualRect)
    {
        var before = _settings.LastRegion;
        _settings.RememberRegion(virtualRect);

        // 範囲として成立しなかった場合は覚えていない。書き込む必要もない
        if (ReferenceEquals(before, _settings.LastRegion))
        {
            return;
        }

        try
        {
            _settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 覚えられなくてもキャプチャ自体は成立しているので、記録だけして進む
            AppLog.Error("前回の範囲を保存できませんでした", ex);
        }
    }

    /// <summary>
    /// オーバーレイ上で「前回と同じ範囲」を呼べるようにするための範囲（画像座標）。
    /// 今撮った画面に収まらない場合は null（呼べないようにする）。
    /// </summary>
    private PixelRect? RecallRect(CapturedImage image)
        => RegionMemory.Resolve(
            _settings.LastRegion, image.OriginX, image.OriginY, image.Width, image.Height) is { } region
            ? ToImage(image, region)
            : null;

    /// <summary>
    /// ワークスペースは常時手前に出ているため、写り込まないよう一時的に隠す。
    /// </summary>
    private async Task<bool> HideWorkspaceAsync()
    {
        if (_workspace is null || !_workspace.IsVisible)
        {
            return false;
        }

        // 自動で消える途中のものは、撮り終えたら改めて出すので元に戻さない。
        // 戻すと消えないまま居座ってしまう
        var restore = !_workspace.IsAutoHiding;
        _workspace.CancelAutoHide();
        _workspace.Hide();

        // 画面から消えるのを待つ（待たないと自分の窓が写る）
        await Task.Delay(90);
        return restore;
    }

    private void Finish(BitmapSource image, string? note = null)
    {
        var path = CaptureSaver.SaveNewCapture(image, _settings);
        AppLog.Info($"保存: {path} ({image.PixelWidth}x{image.PixelHeight})");

        if (note is not null)
        {
            Notify?.Invoke(note);
        }

        if (_settings.CopyToClipboardOnCapture)
        {
            CaptureSaver.TryCopyToClipboard(image);
        }

        switch (_settings.AfterCapture)
        {
            case AfterCaptureModes.Editor:
                _workspace?.Refresh();
                OpenEditor(path);
                break;

            case AfterCaptureModes.Workspace:
                // コピー済みなのでそのまま貼れる。編集・ドラッグしたいときだけ触ればよい
                EnsureWorkspace().ShowBriefly(TimeSpan.FromSeconds(_settings.WorkspaceAutoHideSeconds));
                break;

            default:
                _workspace?.Refresh();
                Notify?.Invoke($"保存しました: {Path.GetFileName(path)}");
                break;
        }
    }

    /// <summary>既存の PNG を編集画面で開く。既に開いていればそれを前面に出す。</summary>
    public void OpenEditor(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var existing = _editors.FirstOrDefault(
                e => string.Equals(Path.GetFullPath(e.FilePath), full, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                existing.Activate();
                return;
            }

            var loaded = CaptureSaver.Load(path);
            var editor = new EditorWindow(path, loaded, _settings);

            _editors.Add(editor);
            editor.Closed += (_, _) =>
            {
                _editors.Remove(editor);
                _workspace?.Refresh();
            };

            editor.Show();
            editor.Activate();
        }
        catch (Exception ex)
        {
            Fail($"編集画面を開けませんでした: {Path.GetFileName(path)}", ex);
        }
    }

    /// <summary>手で開く。この場合は自動で消さない。</summary>
    public void ShowWorkspace() => EnsureWorkspace().ShowPinned();

    private WorkspaceWindow EnsureWorkspace()
    {
        if (_workspace is null)
        {
            _workspace = new WorkspaceWindow(this);
            _workspace.Closed += (_, _) => _workspace = null;
        }

        return _workspace;
    }

    public void OpenSaveFolder()
    {
        Directory.CreateDirectory(_settings.SaveFolder);
        ShellOpener.OpenPath(_settings.SaveFolder);
    }

    private void Fail(string message, Exception ex)
    {
        AppLog.Error(message, ex);
        MessageBox.Show(
            $"{message}\n\n{ex.Message}\n\nログ: {AppLog.LogPath}",
            "Torimaru",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }
}
