using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Torimaru.App.Services;
using Torimaru.Core;

namespace Torimaru.App.Workspace;

/// <summary>キャプチャ一覧の 1 行。</summary>
public sealed record CaptureItem(
    string Path, string Name, string Info, BitmapSource? Thumbnail, bool IsVideo);

/// <summary>
/// 直近のキャプチャを並べるパネル。
/// 他アプリへドラッグして貼り付けられるのが要点（メールや課題管理への投入が速い）。
/// </summary>
public partial class WorkspaceWindow : Window
{
    private const int MaxItems = 80;

    private readonly CaptureWorkflow _workflow;
    private readonly DispatcherTimer _autoHideTimer = new();
    private Point _dragOrigin;
    private bool _dragArmed;

    public WorkspaceWindow(CaptureWorkflow workflow)
    {
        _workflow = workflow;
        InitializeComponent();
        UpdateButtons();

        _autoHideTimer.Tick += OnAutoHideTick;

        // クリックやドラッグを始めたら使っている最中なので、もう勝手に消さない
        Activated += (_, _) => CancelAutoHide();

        // マウスを乗せている間は待ち、離れたら数え直す
        MouseLeave += (_, _) =>
        {
            if (IsAutoHiding)
            {
                _autoHideTimer.Stop();
                _autoHideTimer.Start();
            }
        };
    }

    /// <summary>撮影直後に出した状態で、時間が来たら消える途中か。</summary>
    public bool IsAutoHiding { get; private set; }

    /// <summary>手で開く。前面に出して、自動では消さない。</summary>
    public void ShowPinned()
    {
        CancelAutoHide();
        ShowActivated = true;
        Show();
        Activate();
        Refresh();
    }

    /// <summary>
    /// 撮影直後に、手元の作業を邪魔しないよう前面に出すだけ（フォーカスは奪わない）。
    /// 指定時間が経つと消える。手で開いて使っている最中なら一覧の更新だけにとどめる。
    /// </summary>
    public void ShowBriefly(TimeSpan duration)
    {
        if (IsVisible && !IsAutoHiding)
        {
            Refresh(selectNewest: true);
            return;
        }

        ShowActivated = false;
        Show();
        Refresh(selectNewest: true);

        _autoHideTimer.Stop();
        IsAutoHiding = duration > TimeSpan.Zero;

        if (IsAutoHiding)
        {
            _autoHideTimer.Interval = duration;
            _autoHideTimer.Start();
        }
    }

    public void CancelAutoHide()
    {
        IsAutoHiding = false;
        _autoHideTimer.Stop();
    }

    private void OnAutoHideTick(object? sender, EventArgs e)
    {
        // 乗せている間は消さない。離れたら MouseLeave で数え直す
        if (IsMouseOver)
        {
            _autoHideTimer.Stop();
            return;
        }

        CancelAutoHide();
        Hide();
    }

    protected override void OnClosed(EventArgs e)
    {
        CancelAutoHide();
        base.OnClosed(e);
    }

    /// <summary>
    /// 一覧を読み直す。サムネイル生成は UI を止めないよう別スレッドで行う。
    /// selectNewest なら先頭（＝今撮ったもの）を選んでおき、すぐ編集・コピーできるようにする。
    /// </summary>
    public void Refresh(bool selectNewest = false)
    {
        var folder = _workflow.Settings.SaveFolder;

        Task.Run(() => LoadItems(folder)).ContinueWith(
            task =>
            {
                if (task.IsFaulted)
                {
                    AppLog.Error("ワークスペースの読み込みに失敗", task.Exception);
                    return;
                }

                var items = task.Result;
                Items.ItemsSource = items;
                EmptyHint.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

                if (selectNewest && items.Count > 0)
                {
                    Items.SelectedIndex = 0;
                    Items.ScrollIntoView(items[0]);
                }

                UpdateButtons();
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static List<CaptureItem> LoadItems(string folder)
    {
        var items = new List<CaptureItem>();
        if (!Directory.Exists(folder))
        {
            return items;
        }

        // 直下のみを見る。注釈前のオリジナルは .originals に隔離してあるので混ざらない
        var files = new DirectoryInfo(folder)
            .EnumerateFiles("*.*", SearchOption.TopDirectoryOnly)
            .Where(f => f.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                        || f.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(MaxItems);

        foreach (var file in files)
        {
            var isVideo = file.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase);

            var info = string.Format(
                CultureInfo.CurrentCulture,
                "{0}{1:yyyy/MM/dd HH:mm}　{2:N0} KB",
                isVideo ? "動画　" : string.Empty,
                file.LastWriteTime,
                Math.Max(1, file.Length / 1024));

            var thumbnail = isVideo
                ? LoadVideoThumbnail(file)
                : LoadThumbnail(file.FullName);

            items.Add(new CaptureItem(file.FullName, file.Name, info, thumbnail, isVideo));
        }

        return items;
    }

    /// <summary>
    /// 動画のサムネイルは ffmpeg で 1 フレーム抜いて作り、キャッシュする。
    /// 一覧を開くたびに作り直すと重いため、更新時刻をキーにして使い回す。
    /// </summary>
    private static BitmapSource? LoadVideoThumbnail(FileInfo file)
    {
        if (!Ffmpeg.IsAvailable)
        {
            return null;
        }

        try
        {
            var cacheFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Torimaru",
                "thumbs");

            Directory.CreateDirectory(cacheFolder);

            var key = $"{file.Name}-{file.LastWriteTimeUtc.Ticks}".GetHashCode().ToString("X8");
            var cached = Path.Combine(cacheFolder, $"{key}.png");

            if (!File.Exists(cached)
                && !VideoTools.TryCreateThumbnailAsync(file.FullName, cached).GetAwaiter().GetResult())
            {
                return null;
            }

            return LoadThumbnail(cached);
        }
        catch (Exception ex)
        {
            AppLog.Error($"動画サムネイルの作成に失敗: {file.FullName}", ex);
            return null;
        }
    }

    private static BitmapSource? LoadThumbnail(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path);
            bitmap.DecodePixelWidth = 150;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
        {
            AppLog.Error($"サムネイルを作れませんでした: {path}", ex);
            return null;
        }
    }

    private CaptureItem? Selected => Items.SelectedItem as CaptureItem;

    private void UpdateButtons()
    {
        var hasSelection = Selected is not null;
        EditButton.IsEnabled = hasSelection;
        CopyButton.IsEnabled = hasSelection;
        DeleteButton.IsEnabled = hasSelection;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void OnCapture(object sender, RoutedEventArgs e) => _workflow.CaptureInteractive();

    private void OnRefresh(object sender, RoutedEventArgs e) => Refresh();

    private void OnOpenFolder(object sender, RoutedEventArgs e) => _workflow.OpenSaveFolder();

    private void OnEdit(object sender, RoutedEventArgs e) => OpenSelected();

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected();

    private void OpenSelected()
    {
        if (Selected is not { } item || !File.Exists(item.Path))
        {
            return;
        }

        if (item.IsVideo)
        {
            _workflow.OpenVideoTools(item.Path);
        }
        else
        {
            _workflow.OpenEditor(item.Path);
        }
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item)
        {
            return;
        }

        try
        {
            if (item.IsVideo)
            {
                // 動画は画像として貼れないので、ファイルとしてコピーする
                var data = new DataObject();
                data.SetFileDropList(new System.Collections.Specialized.StringCollection { item.Path });
                Clipboard.SetDataObject(data, copy: true);
                return;
            }

            var image = CaptureSaver.DecodeImage(File.ReadAllBytes(item.Path));
            CaptureSaver.TryCopyToClipboard(image);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException
                                       or System.Runtime.InteropServices.COMException)
        {
            AppLog.Error($"コピーに失敗: {item.Path}", ex);
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            item.IsVideo
                ? $"{item.Name} を削除しますか？"
                : $"{item.Name} を削除しますか？\n（注釈前のオリジナルも一緒に削除されます）",
            "Torimaru",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            File.Delete(item.Path);

            var original = CaptureNaming.OriginalPathFor(item.Path);
            if (File.Exists(original))
            {
                File.Delete(original);
            }

            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error($"削除に失敗: {item.Path}", ex);
            MessageBox.Show(
                this,
                $"削除できませんでした。\n\n{ex.Message}",
                "Torimaru",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    // ------------------------------------------------- 他アプリへのドラッグ&ドロップ

    private void OnItemsPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragOrigin = e.GetPosition(this);
        _dragArmed = FindItem(e.OriginalSource) is not null;
    }

    private void OnItemsPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var item = FindItem(e.OriginalSource);
        _dragArmed = false;

        if (item is null || !File.Exists(item.Path))
        {
            return;
        }

        var data = new DataObject();
        data.SetFileDropList(new System.Collections.Specialized.StringCollection { item.Path });

        try
        {
            DragDrop.DoDragDrop(Items, data, DragDropEffects.Copy);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            AppLog.Error("ドラッグ操作に失敗", ex);
        }
    }

    /// <summary>クリックされた視覚要素から、対応する一覧項目を探す。</summary>
    private CaptureItem? FindItem(object? source)
    {
        if (source is not DependencyObject element)
        {
            return null;
        }

        // 要素から親方向へ辿って ListBoxItem を見つけてくれる
        return (Items.ContainerFromElement(element) as ListBoxItem)?.DataContext as CaptureItem;
    }
}
