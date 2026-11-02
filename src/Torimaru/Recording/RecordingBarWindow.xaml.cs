using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Torimaru.App.Capture;
using Torimaru.App.Interop;
using Torimaru.Core;

namespace Torimaru.App.Recording;

/// <summary>
/// 録画中に出す小さな操作バー。
///
/// 録画範囲の「外」に置くのが肝心で、範囲の中に重なると録画に写り込んでしまう。
/// 下に置けなければ上、それも無理なら画面の隅へ逃がす。
/// </summary>
public partial class RecordingBarWindow : Window
{
    private readonly VideoRecorder _recorder;
    private readonly PixelRect _region;
    private readonly DispatcherTimer _timer;

    /// <summary>停止して保存が押されたとき。</summary>
    public event Func<Task>? StopRequested;

    /// <summary>破棄が押されたとき。</summary>
    public event Func<Task>? CancelRequested;

    public RecordingBarWindow(VideoRecorder recorder, PixelRect virtualRegion)
    {
        _recorder = recorder;
        _region = virtualRegion;

        InitializeComponent();

        AudioText.Text = DescribeAudio(recorder);

        _timer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };

        _timer.Tick += (_, _) => UpdateElapsed();
        _timer.Start();
    }

    private static string DescribeAudio(VideoRecorder recorder)
    {
        var parts = new List<string>();
        if (recorder.SystemAudioActive)
        {
            parts.Add("システム音声");
        }

        if (recorder.MicrophoneActive)
        {
            parts.Add("マイク");
        }

        return parts.Count == 0 ? "音声なし" : string.Join(" + ", parts);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // レイアウトが確定してから位置を決める
        Dispatcher.BeginInvoke(PlaceOutsideRegion, DispatcherPriority.Loaded);
    }

    private void PlaceOutsideRegion()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var scale = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;

        var widthPx = (int)Math.Ceiling(ActualWidth * scale);
        var heightPx = (int)Math.Ceiling(ActualHeight * scale);

        var (vx, vy, vw, vh) = ScreenCapturer.GetVirtualScreenBounds();

        const int gap = 10;
        var x = (int)_region.X;
        var y = (int)_region.Bottom + gap;

        // 下に入らなければ上、それも駄目なら仮想画面の上端へ
        if (y + heightPx > vy + vh)
        {
            var above = (int)_region.Y - gap - heightPx;
            y = above >= vy ? above : vy + gap;
        }

        x = Math.Clamp(x, vx, Math.Max(vx, vx + vw - widthPx));
        y = Math.Clamp(y, vy, Math.Max(vy, vy + vh - heightPx));

        NativeMethods.SetWindowPos(
            new WindowInteropHelper(this).Handle,
            new IntPtr(-1), // HWND_TOPMOST
            x, y, 0, 0,
            NativeMethods.SWP_NOACTIVATE | 0x0001 /* SWP_NOSIZE */);
    }

    private void UpdateElapsed()
    {
        var elapsed = _recorder.Elapsed;
        ElapsedText.Text = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss")
            : elapsed.ToString(@"mm\:ss");

        // 一時停止中は赤い点を止める
        RecordingDot.Opacity = _recorder.IsPaused
            ? 0.35
            : (DateTime.Now.Millisecond < 500 ? 1.0 : 0.35);
    }

    private async void OnPauseResume(object sender, RoutedEventArgs e)
    {
        PauseButton.IsEnabled = false;

        try
        {
            if (_recorder.IsPaused)
            {
                var resumed = await _recorder.ResumeAsync();
                if (!resumed.Success)
                {
                    MessageBox.Show(this, resumed.Message, "録画", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                PauseButton.Content = "一時停止";
            }
            else
            {
                await _recorder.PauseAsync();
                PauseButton.Content = "再開";
            }

            AudioText.Text = DescribeAudio(_recorder);
        }
        finally
        {
            PauseButton.IsEnabled = true;
        }
    }

    private async void OnStop(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        IsEnabled = false;

        if (StopRequested is not null)
        {
            await StopRequested.Invoke();
        }

        Close();
    }

    private async void OnCancel(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            this,
            "録画を破棄しますか？（保存されません）",
            "録画",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        _timer.Stop();
        IsEnabled = false;

        if (CancelRequested is not null)
        {
            await CancelRequested.Invoke();
        }

        Close();
    }

    private void OnDragBar(object sender, MouseButtonEventArgs e)
    {
        // 位置が気に入らなければ自分で動かせるようにする
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
