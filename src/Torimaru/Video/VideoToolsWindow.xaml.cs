using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Torimaru.App.Services;
using Torimaru.Core;

namespace Torimaru.App.Video;

/// <summary>
/// 録画した動画の後処理。プレビュー・フレーム書き出し・トリミング・結合を行う。
///
/// 再生は行わず、ffmpeg で 1 枚ずつフレームを取り出して表示する。
/// コーデックの相性に悩まされず、書き出す絵と同じものが見える利点がある。
/// </summary>
public partial class VideoToolsWindow : Window
{
    private readonly CaptureWorkflow _workflow;
    private readonly DispatcherTimer _previewDebounce;
    private readonly string _previewFolder;

    private string? _videoPath;
    private TimeSpan _duration;
    private bool _loading;
    private int _previewSequence;

    public VideoToolsWindow(CaptureWorkflow workflow)
    {
        _workflow = workflow;

        InitializeComponent();

        _previewFolder = Path.Combine(Path.GetTempPath(), "TorimaruVideoPreview");
        Directory.CreateDirectory(_previewFolder);

        // スライダーを動かすたびに ffmpeg を呼ぶと重いので、止まってから取りに行く
        _previewDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _previewDebounce.Tick += async (_, _) =>
        {
            _previewDebounce.Stop();
            await RefreshPreviewAsync();
        };

        UpdateStatus("動画を開いてください。録画した MP4 のトリミングやフレーム書き出しができます。");
        UpdateCommandState();
    }

    /// <summary>外部（ワークスペースなど）から動画を読み込ませる。</summary>
    public async void LoadVideo(string path)
    {
        try
        {
            await LoadVideoAsync(path);
        }
        catch (Exception ex)
        {
            AppLog.Error($"動画の読み込みに失敗: {path}", ex);
            UpdateStatus($"読み込みに失敗しました: {ex.Message}");
        }
    }

    private async Task LoadVideoAsync(string path)
    {
        if (!File.Exists(path))
        {
            UpdateStatus($"ファイルが見つかりません: {path}");
            return;
        }

        _loading = true;
        UpdateStatus("動画を調べています…");

        try
        {
            var duration = await Ffmpeg.TryGetDurationAsync(path);
            if (duration is null || duration.Value <= TimeSpan.Zero)
            {
                UpdateStatus("再生時間を取得できませんでした。動画として読めないかもしれません。");
                return;
            }

            _videoPath = path;
            _duration = duration.Value;

            FileText.Text = $"{Path.GetFileName(path)}　（{_duration.TotalSeconds:0.##} 秒）";

            PositionSlider.Maximum = _duration.TotalSeconds;
            PositionSlider.Value = 0;
            PositionSlider.IsEnabled = true;

            TrimStartBox.Text = "0";
            TrimEndBox.Text = _duration.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture);

            UpdateStatus($"読み込みました（{_duration.TotalSeconds:0.##} 秒）");
        }
        finally
        {
            _loading = false;
            UpdateCommandState();
        }

        await RefreshPreviewAsync();
    }

    private async void OnOpenFile(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "動画ファイル|*.mp4;*.mkv;*.mov;*.avi|すべてのファイル|*.*",
            InitialDirectory = _workflow.Settings.SaveFolder,
        };

        if (dialog.ShowDialog(this) == true)
        {
            await LoadVideoAsync(dialog.FileName);
        }
    }

    // ------------------------------------------------------------- プレビュー

    private void OnPositionChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PositionText is null)
        {
            return;
        }

        PositionText.Text = $"{e.NewValue:0.00} / {_duration.TotalSeconds:0.00} 秒";

        if (_videoPath is null || _loading)
        {
            return;
        }

        _previewDebounce.Stop();
        _previewDebounce.Start();
    }

    private async Task RefreshPreviewAsync()
    {
        if (_videoPath is null)
        {
            return;
        }

        var sequence = ++_previewSequence;
        var position = TimeSpan.FromSeconds(PositionSlider.Value);
        var target = Path.Combine(_previewFolder, $"preview{sequence % 4}.png");

        var result = await VideoTools.ExtractFrameAsync(_videoPath, position, target);

        // 連続操作で古い結果が後から返ってきても上書きしない
        if (sequence != _previewSequence)
        {
            return;
        }

        if (!result.Success)
        {
            UpdateStatus(result.Message);
            return;
        }

        try
        {
            Preview.Source = CaptureSaver.DecodeImage(await File.ReadAllBytesAsync(target));
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException)
        {
            AppLog.Error("プレビューの表示に失敗", ex);
        }
    }

    private void OnStepBack(object sender, RoutedEventArgs e)
        => PositionSlider.Value = Math.Max(0, PositionSlider.Value - 1);

    private void OnStepForward(object sender, RoutedEventArgs e)
        => PositionSlider.Value = Math.Min(PositionSlider.Maximum, PositionSlider.Value + 1);

    private async void OnSaveFrame(object sender, RoutedEventArgs e)
    {
        if (_videoPath is null)
        {
            return;
        }

        SaveFrameButton.IsEnabled = false;
        try
        {
            var settings = _workflow.Settings;
            Directory.CreateDirectory(settings.SaveFolder);

            var path = CaptureNaming.BuildUniquePath(
                settings.SaveFolder, DateTime.Now, settings.FileNamePattern);

            var position = TimeSpan.FromSeconds(PositionSlider.Value);
            var result = await VideoTools.ExtractFrameAsync(_videoPath, position, path);

            if (!result.Success)
            {
                UpdateStatus(result.Message);
                return;
            }

            // 書き出した PNG は通常のキャプチャと同じ扱いにする
            var bytes = await File.ReadAllBytesAsync(path);
            var image = CaptureSaver.DecodeImage(bytes);
            var originalPath = CaptureNaming.OriginalPathFor(path);

            Directory.CreateDirectory(Path.GetDirectoryName(originalPath)!);
            if (!File.Exists(originalPath))
            {
                await File.WriteAllBytesAsync(originalPath, bytes);
            }

            UpdateStatus($"{position.TotalSeconds:0.00} 秒のフレームを保存しました: {Path.GetFileName(path)}");
            _workflow.OpenEditor(path);
        }
        catch (Exception ex)
        {
            AppLog.Error("フレームの保存に失敗", ex);
            UpdateStatus($"保存に失敗しました: {ex.Message}");
        }
        finally
        {
            SaveFrameButton.IsEnabled = true;
        }
    }

    // ------------------------------------------------------------- トリミング

    private void OnSetTrimStart(object sender, RoutedEventArgs e)
        => TrimStartBox.Text = PositionSlider.Value.ToString("0.##", CultureInfo.InvariantCulture);

    private void OnSetTrimEnd(object sender, RoutedEventArgs e)
        => TrimEndBox.Text = PositionSlider.Value.ToString("0.##", CultureInfo.InvariantCulture);

    private async void OnTrim(object sender, RoutedEventArgs e)
    {
        if (_videoPath is null)
        {
            return;
        }

        if (!TryParseSeconds(TrimStartBox.Text, out var start)
            || !TryParseSeconds(TrimEndBox.Text, out var end))
        {
            UpdateStatus("開始・終了は秒数（例: 12.5）で入力してください。");
            return;
        }

        TrimButton.IsEnabled = false;
        UpdateStatus("切り出しています…");

        try
        {
            var settings = _workflow.Settings;
            Directory.CreateDirectory(settings.SaveFolder);

            var output = CaptureNaming.BuildUniquePath(
                settings.SaveFolder, DateTime.Now, settings.FileNamePattern, ".mp4");

            var result = await VideoTools.TrimAsync(
                _videoPath, start, end, output, ReencodeCheck.IsChecked == true);

            UpdateStatus(result.Success
                ? $"{result.Message}: {Path.GetFileName(output)}"
                : result.Message);

            if (result.Success)
            {
                await LoadVideoAsync(output);
            }
        }
        finally
        {
            TrimButton.IsEnabled = true;
        }
    }

    private static bool TryParseSeconds(string text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (!double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            || seconds < 0)
        {
            return false;
        }

        value = TimeSpan.FromSeconds(seconds);
        return true;
    }

    // ------------------------------------------------------------- 結合

    private void OnAddConcat(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "動画ファイル|*.mp4;*.mkv;*.mov|すべてのファイル|*.*",
            Multiselect = true,
            InitialDirectory = _workflow.Settings.SaveFolder,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        foreach (var file in dialog.FileNames)
        {
            ConcatList.Items.Add(file);
        }

        UpdateCommandState();
    }

    private void OnRemoveConcat(object sender, RoutedEventArgs e)
    {
        if (ConcatList.SelectedItem is not null)
        {
            ConcatList.Items.Remove(ConcatList.SelectedItem);
            UpdateCommandState();
        }
    }

    private async void OnConcat(object sender, RoutedEventArgs e)
    {
        var inputs = ConcatList.Items.Cast<string>().ToList();
        if (inputs.Count < 2)
        {
            UpdateStatus("結合するには 2 本以上を追加してください。");
            return;
        }

        ConcatButton.IsEnabled = false;
        UpdateStatus("結合しています…");

        try
        {
            var settings = _workflow.Settings;
            Directory.CreateDirectory(settings.SaveFolder);

            var output = CaptureNaming.BuildUniquePath(
                settings.SaveFolder, DateTime.Now, settings.FileNamePattern, ".mp4");

            var result = await VideoTools.ConcatAsync(inputs, output);
            UpdateStatus(result.Success
                ? $"{result.Message}: {Path.GetFileName(output)}"
                : result.Message);

            if (result.Success)
            {
                await LoadVideoAsync(output);
            }
        }
        finally
        {
            ConcatButton.IsEnabled = true;
        }
    }

    // ------------------------------------------------------------- 共通

    private void UpdateCommandState()
    {
        var hasVideo = _videoPath is not null;
        SaveFrameButton.IsEnabled = hasVideo;
        TrimButton.IsEnabled = hasVideo;
        ConcatButton.IsEnabled = ConcatList.Items.Count >= 2;
    }

    private void UpdateStatus(string message) => StatusText.Text = message;

    protected override void OnClosed(EventArgs e)
    {
        _previewDebounce.Stop();

        try
        {
            if (Directory.Exists(_previewFolder))
            {
                Directory.Delete(_previewFolder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("プレビューの一時フォルダを削除できませんでした", ex);
        }

        base.OnClosed(e);
    }
}
