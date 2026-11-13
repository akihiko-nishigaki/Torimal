using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Torimaru.App.Capture;
using Torimaru.App.Interop;
using Torimaru.Core;

namespace Torimaru.App.Overlay;

/// <summary>オーバーレイを何のために出しているか。</summary>
public enum SelectionPurpose
{
    /// <summary>通常のキャプチャ範囲を選ぶ。</summary>
    Region,

    /// <summary>画面上の色を 1 点採取する。</summary>
    Color,

    /// <summary>スクロールキャプチャの対象範囲を選ぶ。</summary>
    ScrollTarget,

    /// <summary>録画する範囲を選ぶ。</summary>
    RecordTarget,
}

/// <summary>
/// 凍結した画面の上で範囲・ウィンドウ・モニタを選ぶオーバーレイ。
///
/// 座標系はすべて「画像ピクセル（= 物理ピクセル）」で扱い、
/// 画面表示だけを DIP へ割り戻す。こうすると DPI 混在でも 1 ピクセルずれない。
/// </summary>
public partial class RegionSelectorWindow : Window
{
    private const double DragThreshold = 4;

    private readonly CapturedImage _image;
    private readonly IReadOnlyList<WindowInfo> _windows;
    private readonly int _magnifierZoom;
    private readonly int _cropSize = 31;
    private readonly SelectionPurpose _purpose;

    /// <summary>
    /// R キーで呼び出せる「前回と同じ範囲」（画像ピクセル座標）。
    /// 今の画面に収まらない場合は呼び出し側が null を渡す。
    /// </summary>
    private readonly PixelRect? _recall;

    private double _scale = 1;
    private bool _dragging;
    private Point _dragStart;
    private PixelRect _selection;
    private WindowInfo? _hoveredWindow;

    public RegionSelectorWindow(
        CapturedImage image,
        IReadOnlyList<WindowInfo> windows,
        int magnifierZoom,
        SelectionPurpose purpose = SelectionPurpose.Region,
        PixelRect? recall = null)
    {
        _image = image;
        _windows = windows;
        _magnifierZoom = Math.Clamp(magnifierZoom, 2, 20);
        _purpose = purpose;
        _recall = recall;

        InitializeComponent();

        Screenshot.Source = image.Source;

        // 前回の範囲を呼べるときだけ、その案内を足す（押せないキーは出さない）
        var recallHint = _recall is { } last && purpose != SelectionPurpose.Color
            ? $"　R=前回と同じ範囲 ({last.Width:0}x{last.Height:0})"
            : string.Empty;

        // ドラッグ中も効く（ボタンを押したまま矢印で端を合わせられる）
        recallHint += "　矢印=1px 移動（Shift で 10px）";

        HintText.Text = purpose switch
        {
            SelectionPurpose.Color => "クリック／Enter=その色を採取（クリップボードへコピー）　矢印=1px 移動（Shift で 10px）　Esc=中止",
            SelectionPurpose.ScrollTarget =>
                "スクロールさせたい範囲をドラッグ　クリック=ウィンドウ全体" + recallHint + "　Esc=中止",
            SelectionPurpose.RecordTarget =>
                "録画する範囲をドラッグ　クリック=ウィンドウ　Space=このモニタ全体"
                + recallHint + "　Esc=中止",
            _ => "ドラッグ=範囲選択　クリック=ウィンドウ　Space=このモニタ全体　F=全画面"
                 + recallHint + "　Esc=中止",
        };
    }

    /// <summary>決定された範囲（画像ピクセル座標）。中止された場合は null。</summary>
    public PixelRect? Result { get; private set; }

    /// <summary><see cref="SelectionPurpose.Color"/> で採取した色。</summary>
    public Color? PickedColor { get; private set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 仮想画面全体を物理ピクセルで覆う。WPF の Left/Top/Width/Height は
        // プライマリ画面の DPI 基準になるため、ここは Win32 で直接指定する。
        var (vx, vy, vw, vh) = ScreenCapturer.GetVirtualScreenBounds();
        var handle = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowPos(
            handle,
            new IntPtr(-1), // HWND_TOPMOST
            vx, vy, vw, vh,
            NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOACTIVATE);

        ApplyScale();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ApplyScale();
    }

    /// <summary>DPI 倍率を取り直し、等倍表示になるようレイアウトを組み直す。</summary>
    private void ApplyScale()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        _scale = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;

        var widthDip = ToDip(_image.Width);
        var heightDip = ToDip(_image.Height);

        Screenshot.Width = widthDip;
        Screenshot.Height = heightDip;
        Layer.Width = widthDip;
        Layer.Height = heightDip;

        CrossH.X1 = 0;
        CrossH.X2 = widthDip;
        CrossV.Y1 = 0;
        CrossV.Y2 = heightDip;

        var viewport = ToDip(_cropSize * _magnifierZoom);
        MagnifierViewport.Width = viewport;
        MagnifierViewport.Height = viewport;
        MagnifierImage.Width = viewport;
        MagnifierImage.Height = viewport;

        var pixelBox = ToDip(_magnifierZoom);
        MagnifierCenter.Width = pixelBox;
        MagnifierCenter.Height = pixelBox;

        UpdateDim();
    }

    private double ToDip(double imagePixels) => imagePixels / _scale;

    private double ToImagePixels(double dip) => dip * _scale;

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        Keyboard.Focus(this);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Activate();
        Keyboard.Focus(this);

        // 起動直後からカーソル下の情報を出しておく
        var point = CurrentImagePoint();
        UpdateHover(point);
        UpdateCrosshair(point);
        UpdateMagnifier(point);
    }

    private Point CurrentImagePoint()
    {
        var dip = Mouse.GetPosition(Layer);
        return new Point(ToImagePixels(dip.X), ToImagePixels(dip.Y));
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var dip = e.GetPosition(Layer);
        var point = new Point(ToImagePixels(dip.X), ToImagePixels(dip.Y));

        if (_dragging)
        {
            _selection = PixelRect
                .FromPoints(_dragStart.X, _dragStart.Y, point.X, point.Y)
                .Clip(_image.Width, _image.Height);

            WindowHighlight.Visibility = Visibility.Collapsed;
            ShowSelection(_selection);
        }
        else
        {
            UpdateHover(point);
        }

        UpdateCrosshair(point);
        UpdateMagnifier(point);
    }

    private void UpdateHover(Point point)
    {
        // 色の採取ではウィンドウ単位の選択は邪魔になるだけなので出さない
        if (_purpose == SelectionPurpose.Color)
        {
            return;
        }

        var virtualX = point.X + _image.OriginX;
        var virtualY = point.Y + _image.OriginY;
        _hoveredWindow = WindowFinder.HitTest(_windows, virtualX, virtualY);

        if (_hoveredWindow is null)
        {
            WindowHighlight.Visibility = Visibility.Collapsed;
            HintTitle.Visibility = Visibility.Collapsed;
            _selection = default;
            UpdateDim();
            return;
        }

        var bounds = ToImageRect(_hoveredWindow.Bounds).Clip(_image.Width, _image.Height);
        _selection = bounds;

        Place(WindowHighlight, bounds);
        WindowHighlight.Visibility = Visibility.Visible;
        SelectionBorder.Visibility = Visibility.Collapsed;

        HintTitle.Text = string.IsNullOrEmpty(_hoveredWindow.Title)
            ? "(名前のないウィンドウ)"
            : _hoveredWindow.Title;
        HintTitle.Visibility = Visibility.Visible;

        UpdateDim();
    }

    /// <summary>仮想画面座標の矩形を画像ピクセル座標へ移す。</summary>
    private PixelRect ToImageRect(PixelRect virtualRect)
        => new(
            virtualRect.X - _image.OriginX,
            virtualRect.Y - _image.OriginY,
            virtualRect.Width,
            virtualRect.Height);

    private void ShowSelection(PixelRect region)
    {
        if (region.IsEmpty)
        {
            SelectionBorder.Visibility = Visibility.Collapsed;
        }
        else
        {
            Place(SelectionBorder, region);
            SelectionBorder.Visibility = Visibility.Visible;
        }

        UpdateDim();
    }

    private void Place(Shape shape, PixelRect region)
    {
        Canvas.SetLeft(shape, ToDip(region.X));
        Canvas.SetTop(shape, ToDip(region.Y));
        shape.Width = Math.Max(0, ToDip(region.Width));
        shape.Height = Math.Max(0, ToDip(region.Height));
    }

    /// <summary>選択範囲だけ明るく見えるように、暗幕に穴を開ける。</summary>
    private void UpdateDim()
    {
        var full = new RectangleGeometry(new Rect(0, 0, ToDip(_image.Width), ToDip(_image.Height)));

        if (_selection.IsEmpty)
        {
            Dim.Data = full;
            return;
        }

        var hole = new RectangleGeometry(new Rect(
            ToDip(_selection.X),
            ToDip(_selection.Y),
            ToDip(_selection.Width),
            ToDip(_selection.Height)));

        Dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, hole);
    }

    private void UpdateCrosshair(Point point)
    {
        var x = ToDip(point.X);
        var y = ToDip(point.Y);

        CrossH.Y1 = y;
        CrossH.Y2 = y;
        CrossV.X1 = x;
        CrossV.X2 = x;
    }

    private void UpdateMagnifier(Point point)
    {
        var centerX = (int)Math.Floor(point.X);
        var centerY = (int)Math.Floor(point.Y);

        var half = _cropSize / 2;
        var left = Math.Clamp(centerX - half, 0, Math.Max(0, _image.Width - _cropSize));
        var top = Math.Clamp(centerY - half, 0, Math.Max(0, _image.Height - _cropSize));
        var width = Math.Min(_cropSize, _image.Width);
        var height = Math.Min(_cropSize, _image.Height);

        if (width <= 0 || height <= 0)
        {
            Magnifier.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            MagnifierImage.Source = new System.Windows.Media.Imaging.CroppedBitmap(
                _image.Source, new Int32Rect(left, top, width, height));
        }
        catch (ArgumentException)
        {
            // 画面構成が変わった直後など、範囲が取れない場合はルーペを隠す
            Magnifier.Visibility = Visibility.Collapsed;
            return;
        }

        var color = _image.ColorAt(centerX, centerY);
        ColorSwatch.Fill = new SolidColorBrush(color);
        ReadoutColor.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";

        ReadoutPosition.Text = _dragging || !_selection.IsEmpty
            ? $"{centerX + _image.OriginX,5},{centerY + _image.OriginY,5}   {_selection.Width,5:F0} x {_selection.Height,-5:F0}"
            : $"{centerX + _image.OriginX,5},{centerY + _image.OriginY,5}";

        Magnifier.Visibility = Visibility.Visible;
        PositionNearCursor(Magnifier, point, 22);
        PositionNearCursor(Hint, point, 22, preferBelow: true, verticalOffset: 150);
    }

    /// <summary>
    /// 要素をカーソルの近くに置く。画面端ではみ出す場合は反対側へ回す。
    /// </summary>
    private void PositionNearCursor(
        FrameworkElement element,
        Point imagePoint,
        double gapPixels,
        bool preferBelow = false,
        double verticalOffset = 0)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = element.DesiredSize;

        var canvasWidth = ToDip(_image.Width);
        var canvasHeight = ToDip(_image.Height);
        var gap = ToDip(gapPixels);

        var x = ToDip(imagePoint.X) + gap;
        if (x + size.Width > canvasWidth)
        {
            x = ToDip(imagePoint.X) - gap - size.Width;
        }

        var y = ToDip(imagePoint.Y) + gap + ToDip(verticalOffset);
        if (!preferBelow || y + size.Height > canvasHeight)
        {
            var above = ToDip(imagePoint.Y) - gap - size.Height - ToDip(verticalOffset);
            y = above >= 0 ? above : Math.Max(0, canvasHeight - size.Height);
        }

        Canvas.SetLeft(element, Math.Max(0, Math.Min(x, Math.Max(0, canvasWidth - size.Width))));
        Canvas.SetTop(element, Math.Max(0, Math.Min(y, Math.Max(0, canvasHeight - size.Height))));
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_purpose == SelectionPurpose.Color)
        {
            return;
        }

        var dip = e.GetPosition(Layer);
        _dragStart = new Point(ToImagePixels(dip.X), ToImagePixels(dip.Y));
        _dragging = true;
        _selection = default;

        WindowHighlight.Visibility = Visibility.Collapsed;
        HintTitle.Visibility = Visibility.Collapsed;
        Layer.CaptureMouse();
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_purpose == SelectionPurpose.Color)
        {
            var picked = e.GetPosition(Layer);
            PickedColor = _image.ColorAt(
                (int)Math.Floor(ToImagePixels(picked.X)),
                (int)Math.Floor(ToImagePixels(picked.Y)));

            DialogResult = true;
            Close();
            return;
        }

        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        Layer.ReleaseMouseCapture();

        var dip = e.GetPosition(Layer);
        var end = new Point(ToImagePixels(dip.X), ToImagePixels(dip.Y));

        var dragged = Math.Abs(end.X - _dragStart.X) >= DragThreshold
                      || Math.Abs(end.Y - _dragStart.Y) >= DragThreshold;

        if (dragged)
        {
            Commit(PixelRect.FromPoints(_dragStart.X, _dragStart.Y, end.X, end.Y));
            return;
        }

        // ドラッグしていない = クリック。カーソル下のウィンドウを採用する
        var virtualX = end.X + _image.OriginX;
        var virtualY = end.Y + _image.OriginY;
        var window = WindowFinder.HitTest(_windows, virtualX, virtualY);

        Commit(window is not null
            ? ToImageRect(window.Bounds)
            : ScreenCapturer.GetMonitorBoundsAt(_image, end.X, end.Y));
    }

    private void OnMouseRightButtonUp(object sender, MouseButtonEventArgs e) => Cancel();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // 矢印キーでカーソルを 1 ピクセルずつ（Shift で 10 ピクセル）動かす。
        // 実際のマウスカーソルを動かすので、ドラッグ中ならそのまま範囲の端が動く
        if (TryNudgeCursor(e.Key))
        {
            e.Handled = true;
            return;
        }

        if (_purpose == SelectionPurpose.Color)
        {
            if (e.Key == Key.Enter)
            {
                // キーボードで位置を合わせたあと、クリックせずに決められるようにする
                var point = CurrentImagePoint();
                PickedColor = _image.ColorAt((int)Math.Floor(point.X), (int)Math.Floor(point.Y));
                DialogResult = true;
                Close();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                Cancel();
                e.Handled = true;
            }

            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                Cancel();
                e.Handled = true;
                break;

            case Key.Space:
            case Key.Enter:
                var point = CurrentImagePoint();
                Commit(ScreenCapturer.GetMonitorBoundsAt(_image, point.X, point.Y));
                e.Handled = true;
                break;

            case Key.F:
                Commit(_image.FullBounds);
                e.Handled = true;
                break;

            case Key.R when _recall is { } recall:
                Commit(recall);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// 矢印キーならカーソルを動かして true を返す。
    /// オーバーレイは仮想画面を等倍で覆っているので、画面の 1 ピクセル＝画像の 1 ピクセル。
    /// 撮った画面の外へは出さない（出ると十字線もルーペも消える）。
    /// </summary>
    private bool TryNudgeCursor(Key key)
        => CursorNudge.TryMove(key, (_image.OriginX, _image.OriginY, _image.Width, _image.Height));

    private void Commit(PixelRect region)
    {
        var clipped = region.Clip(_image.Width, _image.Height);
        if (clipped.Width < 1 || clipped.Height < 1)
        {
            Cancel();
            return;
        }

        Result = clipped;
        DialogResult = true;
        Close();
    }

    private void Cancel()
    {
        Result = null;
        DialogResult = false;
        Close();
    }
}
