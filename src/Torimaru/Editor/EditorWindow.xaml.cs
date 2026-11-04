using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Torimaru.App.Services;
using Torimaru.Core;

namespace Torimaru.App.Editor;

/// <summary>
/// 注釈エディタ。図形はベクタのまま保持し、保存時に初めて焼き込む。
/// オリジナル画像は別途退避してあるので、保存後でも何度でも編集し直せる。
/// </summary>
public partial class EditorWindow : Window
{
    private const string SelectTool = "Select";

    // 画像そのものを切るツール。注釈を置くツールとは別扱い。
    // トリミングはドラッグし終えた時点で切るが、
    // 中抜きは抜く帯を微調整してから確定させる（ドラッグ一発では細かい位置を決められない）。
    private const string CropTool = "Crop";
    private const string CutRowsTool = "CutRows";
    private const string CutColumnsTool = "CutColumns";

    /// <summary>画像を切るツールか（＝図形を作らないツールか）。</summary>
    private static bool IsImageTool(string tool)
        => tool is CropTool or CutRowsTool or CutColumnsTool;

    /// <summary>中抜き（帯を抜くツール）か。確定の手順を踏む。</summary>
    private static bool IsBandTool(string tool)
        => tool is CutRowsTool or CutColumnsTool;

    /// <summary>
    /// 元に戻すための 1 手分。
    ///
    /// 注釈だけでなく下地の画像も持つ。トリミングや中抜きは下地を切り替えるので、
    /// 注釈の JSON だけ戻しても寸法の違う画像に古い座標が乗ってしまう。
    /// 画像は凍結済みで共有できるため、切っていない手では参照が増えるだけで済む。
    /// </summary>
    private readonly record struct EditorSnapshot(BitmapSource Image, string DocumentJson);

    private static readonly string[] Swatches =
    [
        "#FFE53935", // 赤
        "#FFFB8C00", // オレンジ
        "#FFFDD835", // 黄
        "#FF43A047", // 緑
        "#FF1E88E5", // 青
        "#FF8E24AA", // 紫
        "#FF000000", // 黒
        "#FFFFFFFF", // 白
    ];

    private readonly AppSettings _settings;
    private readonly Stack<EditorSnapshot> _undo = new();
    private readonly Stack<EditorSnapshot> _redo = new();

    private BitmapSource _baseImage;
    private AnnotationDocument _document;
    private string _path;

    private string _tool = SelectTool;
    private string _color;
    private double _thickness;
    private string _fontFamily;
    private double _fontSize;
    private double _zoom = 1.0;
    private double _dpiScale = 1.0;

    private AnnotationShape? _selected;
    private AnnotationShape? _draft;
    private AnnotationShape? _editingText;

    private EditorSnapshot? _pendingSnapshot;
    private bool _moving;
    private Point _moveOrigin;
    private double _moveShapeX;
    private double _moveShapeY;
    private double _moveTailX;
    private double _moveTailY;
    private bool _draggingTail;
    private bool _dirty;

    /// <summary>確定済みの図形を書き直している（新規ではない）。確定・取り消しの扱いが変わる。</summary>
    private bool _reediting;

    /// <summary>選択中の吹き出しに合わせてツールバーを更新している間だけ true（反映の往復を防ぐ）。</summary>
    private bool _syncingCalloutLook;

    /// <summary>選択中の図形に合わせて書体・大きさの欄を更新している間だけ true。</summary>
    private bool _syncingFont;

    /// <summary>トリミング・中抜きの範囲をドラッグ中。</summary>
    private bool _regionDragging;
    private Point _regionOrigin;

    /// <summary>
    /// 中抜きの確定待ちの帯。ドラッグし終えてもすぐには切らず、
    /// つまみやキーで微調整してから確定させる。
    /// </summary>
    private BandSelection? _band;

    /// <summary>確定待ちの帯がどちらの向きか（CutRowsTool / CutColumnsTool）。</summary>
    private string _bandTool = CutRowsTool;

    /// <summary>いまつまんでいる辺。</summary>
    private BandGrip _bandGrip = BandGrip.None;

    /// <summary>帯ごと動かすとき、掴んだ位置が帯の始まりからどれだけ内側かを覚えておく。</summary>
    private double _bandGripOffset;

    /// <summary>
    /// この編集中に一度でも下地を切ったか。
    /// 切った場合は保存時に控えのオリジナルも差し替える必要がある
    /// （元に戻したあとでも差し替えは必要なので、一度 true にしたら戻さない）。
    /// </summary>
    private bool _baseImageCut;

    public EditorWindow(string path, LoadedCapture loaded, AppSettings settings)
    {
        _path = path;
        _baseImage = loaded.BaseImage;
        _document = loaded.Document;
        _settings = settings;
        _color = settings.DefaultColor;
        _thickness = settings.DefaultThickness;
        _fontFamily = settings.DefaultFontFamily;
        _fontSize = settings.DefaultFontSize;

        InitializeComponent();

        BuildPalette();
        BuildFontControls();
        ThicknessSlider.Value = _thickness;
        ThicknessLabel.Text = ((int)_thickness).ToString(CultureInfo.InvariantCulture);
        SelectTool_(SelectTool);
        Title = $"キャプチャの編集 — {System.IO.Path.GetFileName(path)}";
    }

    /// <summary>編集中のファイルパス。</summary>
    public string FilePath => _path;

    /// <summary>画像 1 ピクセルあたりの DIP。</summary>
    private double DisplayScale => _zoom / _dpiScale;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var dpi = VisualTreeHelper.GetDpi(this);
        _dpiScale = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;

        Render();
        UpdateStatus($"{_baseImage.PixelWidth} x {_baseImage.PixelHeight} px");
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _dpiScale = newDpi.DpiScaleX <= 0 ? 1 : newDpi.DpiScaleX;
        Render();
    }

    // ---------------------------------------------------------------- 描画

    private void Render()
    {
        // 入力中の図形は入力欄と二重に見えるので、下の絵からは外す
        var canvas = ShapeRenderer.BuildCanvas(_baseImage, _document, DisplayScale, _editingText);
        RenderHost.Content = canvas;

        // オーバーレイの座標をキャンバスに一致させる
        CanvasHost.Width = canvas.Width;
        CanvasHost.Height = canvas.Height;

        ZoomLabel.Content = $"{_zoom * 100:0}%";
        UndoButton.IsEnabled = _undo.Count > 0;
        RedoButton.IsEnabled = _redo.Count > 0;

        UpdateSelectionAdorner();

        // 倍率が変わっても、確定待ちの帯が画像の同じ場所に乗っているようにする
        LayoutBand();
    }

    /// <summary>吹き出しのつまみ（先端）の当たり判定。画面上で約 11px 相当。</summary>
    private bool IsNearTail(AnnotationShape shape, Point point)
    {
        var tolerance = 11 / Math.Max(0.1, DisplayScale);
        var dx = point.X - shape.TailX;
        var dy = point.Y - shape.TailY;
        return ((dx * dx) + (dy * dy)) <= tolerance * tolerance;
    }

    private void UpdateSelectionAdorner()
    {
        if (_selected is null || _editingText is not null)
        {
            SelectionAdorner.Visibility = Visibility.Collapsed;
            TailHandle.Visibility = Visibility.Collapsed;
            return;
        }

        if (_selected.Kind == ShapeKind.Callout)
        {
            Canvas.SetLeft(TailHandle, (_selected.TailX * DisplayScale) - (TailHandle.Width / 2));
            Canvas.SetTop(TailHandle, (_selected.TailY * DisplayScale) - (TailHandle.Height / 2));
            TailHandle.Visibility = Visibility.Visible;
            SyncCalloutLookControls(_selected);
        }
        else
        {
            TailHandle.Visibility = Visibility.Collapsed;
        }

        if (IsTextual(_selected))
        {
            SyncFontControls(_selected);
        }

        var bounds = _selected.HitBounds;
        Canvas.SetLeft(SelectionAdorner, bounds.X * DisplayScale);
        Canvas.SetTop(SelectionAdorner, bounds.Y * DisplayScale);
        SelectionAdorner.Width = Math.Max(2, bounds.Width * DisplayScale);
        SelectionAdorner.Height = Math.Max(2, bounds.Height * DisplayScale);
        SelectionAdorner.Visibility = Visibility.Visible;
    }

    private void BuildPalette()
    {
        foreach (var hex in Swatches)
        {
            var brush = new SolidColorBrush(ShapeRenderer.ParseColor(hex));
            brush.Freeze();

            var button = new Button
            {
                Style = (Style)FindResource("SwatchButton"),
                Background = brush,
                Tag = hex,
                ToolTip = hex,
            };

            button.Click += OnColorClick;
            Palette.Children.Add(button);
        }
    }

    // ---------------------------------------------------------------- ツール

    private void OnToolClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tag })
        {
            SelectTool_(tag);
        }
    }

    private void SelectTool_(string tool)
    {
        CommitInlineText();

        // 別のツールへ移るときは確定待ちを捨てる（帯だけ残しても確定できない）
        ClearBand();

        _tool = tool;

        foreach (var toggle in new[]
                 {
                     ToolSelect, ToolArrow, ToolRectangle, ToolEllipse,
                     ToolText, ToolCallout, ToolStep, ToolHighlight, ToolBlur,
                     ToolCrop, ToolCutRows, ToolCutColumns,
                 })
        {
            toggle.IsChecked = (string?)toggle.Tag == tool;
        }

        CanvasHost.Cursor = tool switch
        {
            SelectTool => Cursors.Arrow,
            CutRowsTool => Cursors.SizeNS,
            CutColumnsTool => Cursors.SizeWE,
            _ => Cursors.Cross,
        };

        if (tool != SelectTool)
        {
            _selected = null;
            UpdateSelectionAdorner();
        }

        // 画像を切るツールは手順が分かりにくいので、選んだ時点でやり方を出す
        switch (tool)
        {
            case CropTool:
                UpdateStatus("トリミング: 残したい範囲をドラッグしてください（Esc で中止）");
                break;
            case CutRowsTool:
                UpdateStatus("縦に中抜き: 消したい中間部分を上下にドラッグ → 微調整 → Enter で確定（Esc で中止）");
                break;
            case CutColumnsTool:
                UpdateStatus("横に中抜き: 消したい中間部分を左右にドラッグ → 微調整 → Enter で確定（Esc で中止）");
                break;
        }
    }

    private void OnColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string hex })
        {
            return;
        }

        _color = hex;

        // 選択中の図形があれば、その色も変える（選び直す手間を省く）
        if (_selected is not null)
        {
            BeginAction();
            _selected.Color = hex;
            CommitAction();
        }

        UpdateStatus($"色: {hex}");
        Render();
    }

    // ------------------------------------------------- 書体と大きさ

    /// <summary>
    /// お気に入りを一度も決めていないときに並べる定番（日本語が読めるもの）。
    /// 入っていない書体は出さない。
    /// </summary>
    private static readonly string[] PreferredFonts =
    [
        "Yu Gothic UI", "Meiryo UI", "Meiryo", "BIZ UDPGothic",
        "MS PGothic", "MS Gothic", "Yu Mincho", "MS PMincho",
        "Segoe UI", "Arial", "Consolas",
    ];

    private static readonly int[] FontSizes =
        [10, 12, 14, 16, 18, 20, 24, 28, 32, 40, 48, 56, 64, 80];

    /// <summary>この PC に入っている書体名（表示順）。</summary>
    private readonly List<string> _installedFonts = [];

    private readonly HashSet<string> _installedFontLookup = new(StringComparer.OrdinalIgnoreCase);

    private void BuildFontControls()
    {
        foreach (var family in System.Windows.Media.Fonts.SystemFontFamilies)
        {
            var name = family.Source;
            if (!string.IsNullOrWhiteSpace(name) && _installedFontLookup.Add(name))
            {
                _installedFonts.Add(name);
            }
        }

        _installedFonts.Sort(StringComparer.CurrentCulture);

        // お気に入りを一度も決めていないときだけ、定番を入れておく。
        // 空リスト（全部外した状態）はそのまま尊重する。
        _settings.FavoriteFonts ??=
            PreferredFonts.Where(_installedFontLookup.Contains).ToList();

        RebuildFontFamilyItems();

        foreach (var size in FontSizes)
        {
            FontSizeBox.Items.Add(NewTaggedItem(size.ToString(CultureInfo.InvariantCulture)));
        }

        EnsureFontSizeItem((int)Math.Round(_fontSize));

        _syncingFont = true;
        try
        {
            SelectByTag(FontFamilyBox, _fontFamily);
            SelectByTag(FontSizeBox, ((int)Math.Round(_fontSize)).ToString(CultureInfo.InvariantCulture));
        }
        finally
        {
            _syncingFont = false;
        }

        UpdateFavoriteButton();
    }

    /// <summary>書体一覧を「お気に入り → 区切り → 全書体」で組み直す。選択は呼び出し側で戻す。</summary>
    private void RebuildFontFamilyItems()
    {
        FontFamilyBox.Items.Clear();

        foreach (var name in Favorites)
        {
            // 外した書体（別 PC で入れたお気に入りなど）は出しても選べないので飛ばす
            if (_installedFontLookup.Contains(name))
            {
                FontFamilyBox.Items.Add(NewFontItem(name));
            }
        }

        if (FontFamilyBox.Items.Count > 0)
        {
            // 区切り線そのものを項目にすると選べてしまうので、無効な項目の中に入れる
            FontFamilyBox.Items.Add(new ComboBoxItem
            {
                Content = new Separator(),
                IsEnabled = false,
                Focusable = false,
            });
        }

        foreach (var name in _installedFonts)
        {
            FontFamilyBox.Items.Add(NewFontItem(name));
        }
    }

    private List<string> Favorites => _settings.FavoriteFonts ??= [];

    private bool IsFavorite(string? name)
        => name is not null
           && Favorites.Any(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase));

    private void UpdateFavoriteButton()
    {
        var name = CurrentFontFamily;
        var favorite = IsFavorite(name);

        FavoriteFontButton.Content = favorite ? "★" : "☆";
        FavoriteFontButton.ToolTip = favorite
            ? $"{name} をお気に入りから外す"
            : $"{name} をお気に入りに入れる（一覧の先頭に出る）";
    }

    /// <summary>いま選んでいる書体をお気に入りに出し入れする。</summary>
    private void OnToggleFavoriteFont(object sender, RoutedEventArgs e)
    {
        var name = CurrentFontFamily;
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var index = Favorites.FindIndex(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            Favorites.RemoveAt(index);
            UpdateStatus($"お気に入りから外しました: {name}");
        }
        else
        {
            Favorites.Add(name);
            UpdateStatus($"お気に入りに入れました: {name}（一覧の先頭に出ます）");
        }

        SaveSettingsQuietly();

        // 並べ直すと選択が外れるので、同じ書体を選び直す
        _syncingFont = true;
        try
        {
            RebuildFontFamilyItems();
            SelectByTag(FontFamilyBox, name);
        }
        finally
        {
            _syncingFont = false;
        }

        UpdateFavoriteButton();
    }

    /// <summary>一覧の項目自体をその書体で表示する（名前だけでは見た目が分からないため）。</summary>
    private static ComboBoxItem NewFontItem(string name) => new()
    {
        Content = name,
        Tag = name,
        FontFamily = ShapeRenderer.ResolveFontFamily(name),
        FontSize = 13,
        ToolTip = name,
    };

    private static ComboBoxItem NewTaggedItem(string text) => new() { Content = text, Tag = text };

    /// <summary>一覧にない大きさ（書体を選べる前に作った注釈など）でも表示がずれないようにする。</summary>
    private void EnsureFontSizeItem(int size)
    {
        var tag = size.ToString(CultureInfo.InvariantCulture);
        foreach (var item in FontSizeBox.Items.OfType<ComboBoxItem>())
        {
            if ((string?)item.Tag == tag)
            {
                return;
            }
        }

        var index = 0;
        while (index < FontSizeBox.Items.Count
               && FontSizeBox.Items[index] is ComboBoxItem { Tag: string existing }
               && int.TryParse(existing, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
               && value < size)
        {
            index++;
        }

        FontSizeBox.Items.Insert(index, NewTaggedItem(tag));
    }

    /// <summary>これから書く文字の書体。</summary>
    private string CurrentFontFamily => SelectedTag(FontFamilyBox) ?? _fontFamily;

    /// <summary>これから書く文字の大きさ。</summary>
    private double CurrentFontSize
        => SelectedTag(FontSizeBox) is { } tag
           && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var size)
            ? Math.Clamp(size, 8, 200)
            : _fontSize;

    private void OnFontChanged(object sender, SelectionChangedEventArgs e)
    {
        // 初期化中の選択と、選択図形からの逆反映では何もしない
        if (!IsLoaded || _syncingFont)
        {
            return;
        }

        _fontFamily = CurrentFontFamily;
        _fontSize = CurrentFontSize;
        RememberFontDefaults();
        UpdateFavoriteButton();

        if (_selected is not null && IsTextual(_selected))
        {
            BeginAction();
            _selected.FontFamily = _fontFamily;
            _selected.FontSize = _fontSize;

            // 文字の大きさが変わると当たり判定も吹き出しの本体も変わる
            if (_selected.Kind == ShapeKind.Callout)
            {
                ShapeRenderer.ApplyCalloutSize(_selected, _dpiScale);
            }
            else
            {
                MeasureText(_selected);
            }

            CommitAction();
            Render();
        }

        UpdateStatus($"文字: {_fontFamily} {_fontSize:0} px");
    }

    /// <summary>選んだ図形の書体・大きさをツールバーに映す。</summary>
    private void SyncFontControls(AnnotationShape shape)
    {
        _syncingFont = true;
        try
        {
            SelectByTag(
                FontFamilyBox,
                string.IsNullOrWhiteSpace(shape.FontFamily) ? _fontFamily : shape.FontFamily);

            var size = (int)Math.Round(shape.FontSize);
            EnsureFontSizeItem(size);
            SelectByTag(FontSizeBox, size.ToString(CultureInfo.InvariantCulture));
        }
        finally
        {
            _syncingFont = false;
        }

        UpdateFavoriteButton();
    }

    /// <summary>次のキャプチャでも同じ書体で書けるよう設定に残す。</summary>
    private void RememberFontDefaults()
    {
        _settings.DefaultFontFamily = _fontFamily;
        _settings.DefaultFontSize = _fontSize;
        SaveSettingsQuietly();
    }

    private void SaveSettingsQuietly()
    {
        try
        {
            _settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 設定が残せなくても編集は続けられるので、記録だけして進む
            AppLog.Error("設定を保存できませんでした", ex);
        }
    }

    // ------------------------------------------------- 吹き出しの形・地色

    /// <summary>これから置く吹き出しの形。</summary>
    private string CurrentCalloutShape
        => SelectedTag(CalloutShapeBox) is { } tag ? CalloutShapes.Normalize(tag) : CalloutShapes.Round;

    /// <summary>これから置く吹き出しの地色。</summary>
    private string CurrentCalloutStyle
        => SelectedTag(CalloutStyleBox) is { } tag ? CalloutStyles.Normalize(tag) : CalloutStyles.Solid;

    private static string? SelectedTag(ComboBox? box)
        => box?.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : null;

    private static string SelectedLabel(ComboBox? box)
        => box?.SelectedItem is ComboBoxItem { Content: string label } ? label : string.Empty;

    private void OnCalloutLookChanged(object sender, SelectionChangedEventArgs e)
    {
        // XAML の初期選択（InitializeComponent 中）と、選択図形からの逆反映では何もしない
        if (!IsLoaded || _syncingCalloutLook)
        {
            return;
        }

        if (_selected is { Kind: ShapeKind.Callout })
        {
            BeginAction();
            _selected.CalloutShape = CurrentCalloutShape;
            _selected.CalloutStyle = CurrentCalloutStyle;

            // 形によって文字を収めるのに必要な大きさが変わる（楕円は広い）。
            // しっぽの先端は動かさないので、ユーザーが合わせた向きは保たれる。
            ShapeRenderer.ApplyCalloutSize(_selected, _dpiScale);
            CommitAction();
            Render();
        }

        UpdateStatus($"吹き出し: {SelectedLabel(CalloutShapeBox)} / {SelectedLabel(CalloutStyleBox)}");
    }

    /// <summary>吹き出しを選んだとき、ツールバーの表示をその吹き出しの設定に合わせる。</summary>
    private void SyncCalloutLookControls(AnnotationShape callout)
    {
        _syncingCalloutLook = true;
        try
        {
            SelectByTag(CalloutShapeBox, CalloutShapes.Normalize(callout.CalloutShape));
            SelectByTag(CalloutStyleBox, CalloutStyles.Normalize(callout.CalloutStyle));
        }
        finally
        {
            _syncingCalloutLook = false;
        }
    }

    private static void SelectByTag(ComboBox box, string tag)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if ((string?)item.Tag == tag)
            {
                box.SelectedItem = item;
                return;
            }
        }
    }

    private void OnThicknessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _thickness = e.NewValue;

        if (ThicknessLabel is not null)
        {
            ThicknessLabel.Text = ((int)_thickness).ToString(CultureInfo.InvariantCulture);
        }

        if (_selected is not null && IsLoaded)
        {
            BeginAction();
            _selected.Thickness = _thickness;
            if (_selected.Kind == ShapeKind.Blur)
            {
                _selected.BlurRadius = BlurRadiusFor(_thickness);
            }

            CommitAction();
            Render();
        }
    }

    private static double BlurRadiusFor(double thickness) => Math.Max(6, thickness * 3);

    // ---------------------------------------------------------------- マウス操作

    private Point ToImagePoint(Point hostPoint)
        => new(hostPoint.X / DisplayScale, hostPoint.Y / DisplayScale);

    private void OnCanvasMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        CommitInlineText();

        var point = ToImagePoint(e.GetPosition(CanvasHost));

        if (IsImageTool(_tool))
        {
            // ScrollViewer にフォーカスを奪われると Esc での中止が効かなくなる
            e.Handled = true;

            // 確定待ちの帯があるなら、まずはその微調整として扱う
            if (_band is { } pending && _bandTool == _tool)
            {
                if (e.ClickCount == 2)
                {
                    ConfirmBand();
                    return;
                }

                var axis = BandAxis(point);
                var grip = pending.HitTest(axis, GripTolerance);

                if (grip != BandGrip.None)
                {
                    _bandGrip = grip;
                    _bandGripOffset = axis - pending.Start;
                    CanvasHost.CaptureMouse();
                    return;
                }

                // 帯の外を掴んだときは引き直し
            }

            ClearBand();
            _regionDragging = true;
            _regionOrigin = point;
            CanvasHost.CaptureMouse();
            UpdateRegionAdorner(point);
            return;
        }

        if (_tool == SelectTool)
        {
            // ダブルクリックで、確定済みの文字・吹き出しを書き直す
            if (e.ClickCount == 2 && HitTest(point) is { } target && IsTextual(target))
            {
                // ScrollViewer は未処理の MouseLeftButtonDown でフォーカスを奪うので、ここで止める
                e.Handled = true;
                _moving = false;
                _draggingTail = false;
                CanvasHost.ReleaseMouseCapture();

                _selected = target;
                BeginReedit(target);
                return;
            }

            // 選択中の吹き出しのつまみが近ければ、本体ではなく先端を動かす
            if (_selected is { Kind: ShapeKind.Callout } && IsNearTail(_selected, point))
            {
                BeginAction();
                _draggingTail = true;
                CanvasHost.CaptureMouse();
                UpdateStatus("吹き出しの先端をドラッグしています");
                return;
            }

            _selected = HitTest(point);
            if (_selected is not null)
            {
                BeginAction();
                _moving = true;
                _moveOrigin = point;
                _moveShapeX = _selected.X;
                _moveShapeY = _selected.Y;
                _moveTailX = _selected.TailX;
                _moveTailY = _selected.TailY;

                if (_selected.Kind == ShapeKind.Callout)
                {
                    UpdateStatus("吹き出しを移動中（○をドラッグすると先端だけ動かせます）");
                }
            }

            UpdateSelectionAdorner();
            CanvasHost.CaptureMouse();
            return;
        }

        if (_tool == ShapeKind.Text || _tool == ShapeKind.Callout)
        {
            // ScrollViewer は未処理の MouseLeftButtonDown を受けると自分にフォーカスを移す。
            // ここで止めないと InlineText が直後に LostKeyboardFocus を受け、
            // 空文字のまま確定扱いになって 1 文字も入力できない。
            e.Handled = true;
            StartTextShape(point, _tool);
            return;
        }

        if (_tool == ShapeKind.Step)
        {
            AddStepShape(point);
            return;
        }

        BeginAction();
        _draft = new AnnotationShape
        {
            Kind = _tool,
            X = point.X,
            Y = point.Y,
            Width = 0,
            Height = 0,
            Color = _color,
            Thickness = _thickness,
            BlurRadius = BlurRadiusFor(_thickness),
        };

        _document.Shapes.Add(_draft);
        CanvasHost.CaptureMouse();
        Render();
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        var point = ToImagePoint(e.GetPosition(CanvasHost));

        PositionText.Text = $"{point.X,5:F0}, {point.Y,-5:F0}";

        if (_bandGrip != BandGrip.None && _band is { } adjusting)
        {
            var axis = BandAxis(point);

            _band = _bandGrip == BandGrip.Whole
                ? adjusting.MoveTo(axis - _bandGripOffset, BandTotal)
                : adjusting.MoveEdge(_bandGrip, axis, BandTotal);

            ShowBand();
            return;
        }

        if (_regionDragging)
        {
            UpdateRegionAdorner(point);
            return;
        }

        if (_draft is not null)
        {
            _draft.Width = point.X - _draft.X;
            _draft.Height = point.Y - _draft.Y;
            Render();
            return;
        }

        if (_draggingTail && _selected is not null)
        {
            _selected.TailX = point.X;
            _selected.TailY = point.Y;
            Render();
            return;
        }

        if (_moving && _selected is not null)
        {
            var dx = point.X - _moveOrigin.X;
            var dy = point.Y - _moveOrigin.Y;

            _selected.X = _moveShapeX + dx;
            _selected.Y = _moveShapeY + dy;

            if (_selected.Kind == ShapeKind.Callout)
            {
                _selected.TailX = _moveTailX + dx;
                _selected.TailY = _moveTailY + dy;
            }

            Render();
        }
    }

    private void OnCanvasMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        CanvasHost.ReleaseMouseCapture();

        if (_bandGrip != BandGrip.None)
        {
            // つまみを離しただけ。確定待ちはそのまま続ける
            _bandGrip = BandGrip.None;
            return;
        }

        if (_regionDragging)
        {
            _regionDragging = false;

            var end = ToImagePoint(e.GetPosition(CanvasHost));

            if (IsBandTool(_tool))
            {
                // 中抜きは離した時点では切らない。微調整してから確定させる
                BeginBand(_regionOrigin, end);
                return;
            }

            RegionAdorner.Visibility = Visibility.Collapsed;
            ApplyImageTool(_regionOrigin, end);
            return;
        }

        if (_draft is not null)
        {
            var bounds = _draft.Bounds.Normalized();
            var isArrow = _draft.Kind == ShapeKind.Arrow;
            var tooSmall = isArrow
                ? Math.Sqrt((_draft.Width * _draft.Width) + (_draft.Height * _draft.Height)) < 6
                : bounds.Width < 4 || bounds.Height < 4;

            if (tooSmall)
            {
                // 誤クリックで極小図形を作らない
                _document.Shapes.Remove(_draft);
                AbortAction();
            }
            else
            {
                _selected = _draft;
                CommitAction();
            }

            _draft = null;
            Render();
            return;
        }

        if (_draggingTail)
        {
            _draggingTail = false;
            CommitAction();
            Render();
            return;
        }

        if (_moving)
        {
            _moving = false;
            CommitAction();
        }
    }

    // ---------------------------------------------------------------- 画像を切る

    private static readonly Brush KeepStroke = FrozenBrush("#FF1F6FEB");
    private static readonly Brush KeepFill = FrozenBrush("#201F6FEB");
    private static readonly Brush CutStroke = FrozenBrush("#FFE53935");
    private static readonly Brush CutFill = FrozenBrush("#40E53935");

    private static Brush FrozenBrush(string hex)
    {
        var brush = new SolidColorBrush(ShapeRenderer.ParseColor(hex));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// ドラッグ中の範囲を見せる。
    /// トリミングは「残す範囲」なので青、中抜きは「消える帯」なので赤く塗る。
    /// </summary>
    private void UpdateRegionAdorner(Point current)
    {
        var width = _baseImage.PixelWidth;
        var height = _baseImage.PixelHeight;

        // 中抜きは片方の軸しか意味を持たないので、もう一方は画像いっぱいに広げる
        var region = _tool switch
        {
            CutRowsTool => new PixelRect(
                0, Math.Min(_regionOrigin.Y, current.Y),
                width, Math.Abs(current.Y - _regionOrigin.Y)),
            CutColumnsTool => new PixelRect(
                Math.Min(_regionOrigin.X, current.X), 0,
                Math.Abs(current.X - _regionOrigin.X), height),
            _ => PixelRect.FromPoints(_regionOrigin.X, _regionOrigin.Y, current.X, current.Y),
        };

        region = region.Clip(width, height);

        var keeping = _tool == CropTool;
        RegionAdorner.Stroke = keeping ? KeepStroke : CutStroke;
        RegionAdorner.Fill = keeping ? KeepFill : CutFill;

        Canvas.SetLeft(RegionAdorner, region.X * DisplayScale);
        Canvas.SetTop(RegionAdorner, region.Y * DisplayScale);
        RegionAdorner.Width = Math.Max(1, region.Width * DisplayScale);
        RegionAdorner.Height = Math.Max(1, region.Height * DisplayScale);
        RegionAdorner.Visibility = Visibility.Visible;

        UpdateStatus(_tool switch
        {
            CutRowsTool => $"抜く高さ: {region.Height:F0} px → 残り {height - (int)region.Height} px",
            CutColumnsTool => $"抜く幅: {region.Width:F0} px → 残り {width - (int)region.Width} px",
            _ => $"残す範囲: {region.Width:F0} x {region.Height:F0} px",
        });
    }

    private void CancelRegionDrag()
    {
        _regionDragging = false;
        RegionAdorner.Visibility = Visibility.Collapsed;
        CanvasHost.ReleaseMouseCapture();
    }

    // ------------------------------------------------- 中抜きの確定待ち

    /// <summary>辺をつまんだと見なす距離。画面上で約 7px 相当。</summary>
    private double GripTolerance => 7 / Math.Max(0.1, DisplayScale);

    /// <summary>いま抜こうとしている向きでの画像の長さ。</summary>
    private int BandTotal
        => _bandTool == CutColumnsTool ? _baseImage.PixelWidth : _baseImage.PixelHeight;

    /// <summary>中抜きで意味を持つ側の座標だけ取り出す。</summary>
    private double BandAxis(Point point) => _bandTool == CutColumnsTool ? point.X : point.Y;

    /// <summary>ドラッグし終えた範囲を、確定待ちの帯として置く。</summary>
    private void BeginBand(Point from, Point to)
    {
        _bandTool = _tool;
        _bandGrip = BandGrip.None;
        _band = BandSelection.FromDrag(BandAxis(from), BandAxis(to), BandTotal);
        ShowBand();
    }

    /// <summary>確定待ちの帯を見せ直して、太さと次にすることを伝える。</summary>
    private void ShowBand()
    {
        if (_band is not { } band)
        {
            return;
        }

        LayoutBand();

        var total = BandTotal;
        var count = (int)Math.Round(band.Thickness);

        UpdateStatus(_bandTool == CutColumnsTool
            ? $"抜く幅: {count} px → 残り {total - count} px ／ 端をドラッグか ← → で微調整 → Enter で確定（Esc で中止）"
            : $"抜く高さ: {count} px → 残り {total - count} px ／ 端をドラッグか ↑ ↓ で微調整 → Enter で確定（Esc で中止）");
    }

    /// <summary>確定待ちの帯・つまみ・確定バーを、今の表示倍率に合わせて置き直す。</summary>
    private void LayoutBand()
    {
        if (_band is not { } band)
        {
            return;
        }

        // 縦に中抜き＝横向きの帯。意味を持たない側は画像いっぱいに広げる
        var acrossWidth = _bandTool != CutColumnsTool;
        var canvasWidth = _baseImage.PixelWidth * DisplayScale;
        var canvasHeight = _baseImage.PixelHeight * DisplayScale;
        var from = band.Start * DisplayScale;
        var to = band.End * DisplayScale;
        var thickness = Math.Max(1, to - from);

        RegionAdorner.Stroke = CutStroke;
        RegionAdorner.Fill = CutFill;
        Canvas.SetLeft(RegionAdorner, acrossWidth ? 0 : from);
        Canvas.SetTop(RegionAdorner, acrossWidth ? from : 0);
        RegionAdorner.Width = acrossWidth ? canvasWidth : thickness;
        RegionAdorner.Height = acrossWidth ? thickness : canvasHeight;
        RegionAdorner.Visibility = Visibility.Visible;

        PlaceGrip(BandGripA, acrossWidth, from, canvasWidth, canvasHeight);
        PlaceGrip(BandGripB, acrossWidth, to, canvasWidth, canvasHeight);

        // 確定バーは帯の中央へ。画像の端にかかるときは内側へ寄せる
        var left = acrossWidth
            ? (canvasWidth - BandBar.Width) / 2
            : ((from + to) / 2) - (BandBar.Width / 2);
        var top = acrossWidth
            ? ((from + to) / 2) - (BandBar.Height / 2)
            : (canvasHeight - BandBar.Height) / 2;

        Canvas.SetLeft(BandBar, Math.Clamp(left, 0, Math.Max(0, canvasWidth - BandBar.Width)));
        Canvas.SetTop(BandBar, Math.Clamp(top, 0, Math.Max(0, canvasHeight - BandBar.Height)));
        BandBar.Visibility = Visibility.Visible;
    }

    /// <summary>帯の端のつまみを置く。帯と直角の向きには画像いっぱいに伸ばす。</summary>
    private static void PlaceGrip(
        System.Windows.Shapes.Rectangle grip,
        bool acrossWidth,
        double at,
        double width,
        double height)
    {
        const double Size = 7;

        Canvas.SetLeft(grip, acrossWidth ? 0 : at - (Size / 2));
        Canvas.SetTop(grip, acrossWidth ? at - (Size / 2) : 0);
        grip.Width = acrossWidth ? width : Size;
        grip.Height = acrossWidth ? Size : height;
        grip.Visibility = Visibility.Visible;
    }

    private void OnConfirmBand(object sender, RoutedEventArgs e) => ConfirmBand();

    private void OnCancelBand(object sender, RoutedEventArgs e) => ClearBand("中止しました");

    /// <summary>
    /// 確定待ちの帯を実際に抜く。
    /// 受け付けられない帯のときは理由を出して、確定待ちのまま微調整させる。
    /// </summary>
    private void ConfirmBand()
    {
        if (_band is not { } band)
        {
            return;
        }

        var sideways = _bandTool == CutColumnsTool;
        var from = sideways ? new Point(band.Start, 0) : new Point(0, band.Start);
        var to = sideways ? new Point(band.End, 0) : new Point(0, band.End);

        // 切ると寸法が変わる。描き直しのたびに古い帯が出ないよう、先に外しておく
        _band = null;
        HideBandParts();

        if (!ApplyImageTool(from, to))
        {
            _band = band;
            ShowBand();
            return;
        }

        _bandGrip = BandGrip.None;
    }

    /// <summary>確定待ちをやめる。ツールは選んだままにして、すぐ引き直せるようにする。</summary>
    private void ClearBand(string? message = null)
    {
        var had = _band is not null;

        if (_bandGrip != BandGrip.None)
        {
            CanvasHost.ReleaseMouseCapture();
        }

        _band = null;
        _bandGrip = BandGrip.None;
        HideBandParts();

        if (had && message is not null)
        {
            UpdateStatus(message);
        }
    }

    private void HideBandParts()
    {
        RegionAdorner.Visibility = Visibility.Collapsed;
        BandGripA.Visibility = Visibility.Collapsed;
        BandGripB.Visibility = Visibility.Collapsed;
        BandBar.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 指定された範囲を実際に画像へ反映する。
    /// 受け付けられない範囲だったときは、理由をステータスに出して false を返す。
    /// </summary>
    private bool ApplyImageTool(Point from, Point to)
    {
        var width = _baseImage.PixelWidth;
        var height = _baseImage.PixelHeight;

        switch (_tool)
        {
            case CropTool:
            {
                var dragged = PixelRect.FromPoints(from.X, from.Y, to.X, to.Y);

                if (!ImageCutPlan.TryPlanCrop(dragged, width, height, out var crop, out var problem))
                {
                    UpdateStatus($"トリミング: {problem}");
                    return false;
                }

                ApplyCut(
                    ImageEditing.Crop(_baseImage, crop.X, crop.Y, crop.Width, crop.Height),
                    AnnotationTransform.Crop(
                        _document, new PixelRect(crop.X, crop.Y, crop.Width, crop.Height)),
                    $"トリミングしました: {crop.Width} x {crop.Height} px");
                return true;
            }

            case CutRowsTool:
            {
                if (!ImageCutPlan.TryPlanBand(from.Y, to.Y, height, out var start, out var count, out var problem))
                {
                    UpdateStatus($"縦に中抜き: {problem}");
                    return false;
                }

                ApplyCut(
                    ImageEditing.RemoveRows(_baseImage, start, count),
                    AnnotationTransform.RemoveRows(_document, start, count),
                    $"縦に {count} px 抜きました: {width} x {height - count} px");
                return true;
            }

            case CutColumnsTool:
            {
                if (!ImageCutPlan.TryPlanBand(from.X, to.X, width, out var start, out var count, out var problem))
                {
                    UpdateStatus($"横に中抜き: {problem}");
                    return false;
                }

                ApplyCut(
                    ImageEditing.RemoveColumns(_baseImage, start, count),
                    AnnotationTransform.RemoveColumns(_document, start, count),
                    $"横に {count} px 抜きました: {width - count} x {height} px");
                return true;
            }
        }

        return false;
    }

    /// <summary>切った下地と、付け替えた注釈を 1 手として反映する。</summary>
    private void ApplyCut(BitmapSource image, AnnotationDocument document, string message)
    {
        BeginAction();

        _baseImage = image;
        _document = document;

        // 控えのオリジナルも差し替える必要がある、という印。
        // 元に戻しても差し替えは必要なので、一度立てたら下ろさない。
        _baseImageCut = true;

        _selected = null;
        _draft = null;

        CommitAction();

        UpdateSelectionAdorner();
        Render();
        UpdateStatus(message);
    }

    /// <summary>手前にある図形を優先して当たり判定する。</summary>
    private AnnotationShape? HitTest(Point point)
    {
        for (var i = _document.Shapes.Count - 1; i >= 0; i--)
        {
            if (_document.Shapes[i].HitBounds.Contains(point.X, point.Y))
            {
                return _document.Shapes[i];
            }
        }

        return null;
    }

    // ---------------------------------------------------------------- テキスト

    /// <summary>文字を持つ図形（＝ダブルクリックで書き直せる図形）か。</summary>
    private static bool IsTextual(AnnotationShape shape)
        => shape.Kind is ShapeKind.Text or ShapeKind.Callout;

    private void StartTextShape(Point point, string kind)
    {
        BeginAction();

        var shape = new AnnotationShape
        {
            Kind = kind,
            X = point.X,
            Y = point.Y,
            Color = _color,
            FontSize = _fontSize,
            FontFamily = _fontFamily,
        };

        if (kind == ShapeKind.Callout)
        {
            shape.CalloutShape = CurrentCalloutShape;
            shape.CalloutStyle = CurrentCalloutStyle;
        }

        _document.Shapes.Add(shape);
        _reediting = false;
        OpenInlineText(shape);

        UpdateStatus(
            (kind == ShapeKind.Callout ? "吹き出しの文字を入力" : "文字を入力")
            + "（Enter で改行 / Ctrl+Enter または他をクリックで確定 / Esc で取り消し）");
        Render();
    }

    /// <summary>
    /// 確定済みの文字・吹き出しをもう一度編集する。
    /// 新規と違い、取り消しても図形は消えず、吹き出しのしっぽの向きも保たれる。
    /// </summary>
    private void BeginReedit(AnnotationShape shape)
    {
        BeginAction();
        _reediting = true;
        OpenInlineText(shape);

        UpdateStatus(
            (shape.Kind == ShapeKind.Callout ? "吹き出しの文字" : "文字")
            + "を書き直しています（Enter で改行 / Ctrl+Enter で確定 / Esc で元に戻す）");
        Render();
    }

    /// <summary>インライン入力欄を、その図形の見え方・位置に合わせて開く。</summary>
    private void OpenInlineText(AnnotationShape shape)
    {
        _editingText = shape;

        var isCallout = shape.Kind == ShapeKind.Callout;

        InlineText.Text = shape.Text ?? string.Empty;
        InlineText.FontFamily = ShapeRenderer.ResolveFontFamily(shape.FontFamily);
        InlineText.FontSize = Math.Max(8, shape.FontSize * DisplayScale);

        if (isCallout)
        {
            // 確定後と同じ地色・文字色にして、入力中から仕上がりが分かるようにする
            var look = ShapeRenderer.ResolveCalloutLook(shape);
            InlineText.Foreground = new SolidColorBrush(look.Text);
            InlineText.Background = new SolidColorBrush(look.Fill);
        }
        else
        {
            InlineText.Foreground = new SolidColorBrush(ShapeRenderer.ParseColor(shape.Color));
            InlineText.Background = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
        }

        // 確定後に文字が来る位置へ入力欄を合わせる
        var origin = shape.Bounds.Normalized();
        var (offsetX, offsetY) = isCallout
            ? ShapeRenderer.CalloutTextOffset(shape)
            : (0d, 0d);

        Canvas.SetLeft(InlineText, (origin.X + offsetX) * DisplayScale);
        Canvas.SetTop(InlineText, (origin.Y + offsetY) * DisplayScale);

        EditOverlay.IsHitTestVisible = true;
        InlineText.Visibility = Visibility.Visible;

        // 表示直後はまだレイアウトが走っておらずフォーカスが乗らないことがあるため、
        // 入力イベントを処理し終えたあとに当てる。
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            new Action(() =>
            {
                if (!ReferenceEquals(_editingText, shape))
                {
                    return;
                }

                InlineText.Focus();
                InlineText.CaretIndex = InlineText.Text.Length;
            }));
    }

    private void OnInlineTextKeyDown(object sender, KeyEventArgs e)
    {
        // Enter は改行に使うため、確定は Ctrl+Enter（他をクリックしても確定する）。
        // IME 変換中の Enter は Key.ImeProcessed として届くのでここには来ない。
        if (e.Key == Key.Enter)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                e.Handled = true;
                CommitInlineText();
            }

            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelInlineText();
        }
    }

    private void OnInlineTextLostFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitInlineText();

    private void CommitInlineText()
    {
        if (_editingText is null)
        {
            return;
        }

        var shape = _editingText;
        var reediting = _reediting;
        _editingText = null;
        _reediting = false;
        HideInlineText();

        var text = InlineText.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            _document.Shapes.Remove(shape);

            if (reediting)
            {
                // 書き直して空にしたのは「削除」という操作なので履歴に残す
                _selected = null;
                CommitAction();
                UpdateStatus("文字を消したので図形を削除しました");
            }
            else
            {
                // 新規は「置かなかった」だけなので履歴を汚さない
                AbortAction();
            }
        }
        else
        {
            shape.Text = text;

            if (shape.Kind == ShapeKind.Callout)
            {
                // 本体は文字に合わせた大きさにする。
                // しっぽは新規のときだけ既定位置へ置き、書き直しでは向きを保つ。
                ShapeRenderer.ApplyCalloutSize(shape, _dpiScale);

                if (!reediting)
                {
                    shape.ResetTail();
                }

                UpdateStatus(reediting
                    ? "吹き出しの文字を書き直しました"
                    : "吹き出しを置きました。選択ツール (V) で○をドラッグすると先端が動きます");
            }
            else
            {
                MeasureText(shape);
            }

            _selected = shape;
            CommitAction();
        }

        Render();
    }

    private void CancelInlineText()
    {
        if (_editingText is null)
        {
            return;
        }

        // 書き直しの取り消しでは図形そのものを消さない（元の文字が残る）
        if (!_reediting)
        {
            _document.Shapes.Remove(_editingText);
        }

        _editingText = null;
        _reediting = false;
        HideInlineText();
        AbortAction();
        Render();
    }

    private void HideInlineText()
    {
        InlineText.Visibility = Visibility.Collapsed;
        EditOverlay.IsHitTestVisible = false;
        Keyboard.Focus(CanvasHost);
    }

    /// <summary>
    /// 当たり判定と選択枠のためにテキストの実寸を測る。
    /// 書体がずれると枠がずれるので、測定は描画側 (ShapeRenderer) と共有している。
    /// </summary>
    private void MeasureText(AnnotationShape shape)
        => ShapeRenderer.ApplyMeasuredSize(shape, _dpiScale);

    // ---------------------------------------------------------------- 番号マーカー

    private void AddStepShape(Point point)
    {
        BeginAction();

        var diameter = Math.Max(26, _thickness * 9);
        _document.Shapes.Add(new AnnotationShape
        {
            Kind = ShapeKind.Step,
            X = point.X - (diameter / 2),
            Y = point.Y - (diameter / 2),
            Width = diameter,
            Height = diameter,
            Color = _color,
            Number = _document.NextStepNumber,
        });

        _document.NextStepNumber++;
        CommitAction();
        Render();
        UpdateStatus($"番号 {_document.NextStepNumber - 1} を追加");
    }

    // ---------------------------------------------------------------- Undo / Redo

    private EditorSnapshot CurrentSnapshot() => new(_baseImage, _document.ToJson());

    private void BeginAction() => _pendingSnapshot = CurrentSnapshot();

    private void CommitAction()
    {
        if (_pendingSnapshot is not { } snapshot)
        {
            return;
        }

        _pendingSnapshot = null;

        // 掴んだだけで動かさなかった場合など、中身が変わっていなければ履歴に積まない
        // （ダブルクリックの 1 回目で空の「元に戻す」が増えるのを防ぐ）。
        // 画像も比べるのは、注釈が無い絵をトリミングしたときに
        // 「JSON は同じだから変化なし」と誤判定して戻せなくなるのを防ぐため。
        if (ReferenceEquals(snapshot.Image, _baseImage)
            && string.Equals(snapshot.DocumentJson, _document.ToJson(), StringComparison.Ordinal))
        {
            return;
        }

        _undo.Push(snapshot);
        _redo.Clear();
        _dirty = true;

        UndoButton.IsEnabled = true;
        RedoButton.IsEnabled = false;
    }

    /// <summary>操作が成立しなかった場合、Undo 履歴を汚さずに破棄する。</summary>
    private void AbortAction() => _pendingSnapshot = null;

    private void OnUndo(object sender, RoutedEventArgs e) => Undo();

    private void Undo()
    {
        CommitInlineText();
        if (_undo.Count == 0)
        {
            return;
        }

        _redo.Push(CurrentSnapshot());
        ApplySnapshot(_undo.Pop());
        UpdateStatus($"元に戻しました（{_baseImage.PixelWidth} x {_baseImage.PixelHeight} px）");
    }

    private void OnRedo(object sender, RoutedEventArgs e) => Redo();

    private void Redo()
    {
        CommitInlineText();
        if (_redo.Count == 0)
        {
            return;
        }

        _undo.Push(CurrentSnapshot());
        ApplySnapshot(_redo.Pop());
        UpdateStatus($"やり直しました（{_baseImage.PixelWidth} x {_baseImage.PixelHeight} px）");
    }

    private void ApplySnapshot(EditorSnapshot snapshot)
    {
        // 寸法が変わるので、確定待ちの帯は残さない
        ClearBand();

        _baseImage = snapshot.Image;
        _document = AnnotationDocument.FromJson(snapshot.DocumentJson) ?? new AnnotationDocument();
        _selected = null;
        _draft = null;
        _dirty = true;
        Render();
    }

    private void OnDeleteSelected(object sender, RoutedEventArgs e) => DeleteSelected();

    private void DeleteSelected()
    {
        if (_selected is null)
        {
            return;
        }

        BeginAction();
        _document.Shapes.Remove(_selected);
        _selected = null;
        CommitAction();
        Render();
        UpdateStatus("図形を削除しました");
    }

    // ---------------------------------------------------------------- 保存・コピー

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        CommitInlineText();

        var flattened = CaptureSaver.Flatten(_baseImage, _document);
        UpdateStatus(CaptureSaver.TryCopyToClipboard(flattened)
            ? "クリップボードへコピーしました"
            : "クリップボードへのコピーに失敗しました");
    }

    private void OnSave(object sender, RoutedEventArgs e) => Save();

    private bool Save()
    {
        CommitInlineText();

        try
        {
            CaptureSaver.SaveEdited(_path, _baseImage, _document, replaceOriginal: _baseImageCut);
            _dirty = false;
            UpdateStatus($"保存しました: {System.IO.Path.GetFileName(_path)}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("保存に失敗", ex);
            MessageBox.Show(
                this,
                $"保存できませんでした。\n\n{ex.Message}",
                "Torimaru",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
    }

    private void OnSaveAs(object sender, RoutedEventArgs e)
    {
        CommitInlineText();

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG 画像|*.png",
            DefaultExt = ".png",
            FileName = System.IO.Path.GetFileName(_path),
            InitialDirectory = System.IO.Path.GetDirectoryName(_path) ?? _settings.SaveFolder,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _path = dialog.FileName;
        Title = $"キャプチャの編集 — {System.IO.Path.GetFileName(_path)}";
        Save();
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var folder = System.IO.Path.GetDirectoryName(_path) ?? _settings.SaveFolder;
        ShellOpener.OpenFolderAndSelect(_path, folder);
    }

    // ---------------------------------------------------------------- OCR / コード読み取り

    /// <summary>
    /// 読み取り対象の画像を決める。
    /// 枠・蛍光・ぼかしのいずれかを選んでいれば、その範囲だけを切り出す
    /// （「この部分の文字が欲しい」という使い方が一番多いため）。
    /// </summary>
    private (System.Windows.Media.Imaging.BitmapSource Image, string Scope) ResolveTargetImage()
    {
        if (_selected is not null
            && _selected.Kind is ShapeKind.Rectangle or ShapeKind.Highlight or ShapeKind.Blur)
        {
            var bounds = _selected.Bounds
                .Normalized()
                .Clip(_baseImage.PixelWidth, _baseImage.PixelHeight);

            var (x, y, w, h) = bounds.ToIntegerBounds();
            w = Math.Min(w, _baseImage.PixelWidth - x);
            h = Math.Min(h, _baseImage.PixelHeight - y);

            if (w >= 4 && h >= 4)
            {
                var cropped = new System.Windows.Media.Imaging.CroppedBitmap(
                    _baseImage, new Int32Rect(x, y, w, h));

                cropped.Freeze();
                return (cropped, $"選択範囲 {w} x {h} px");
            }
        }

        return (_baseImage, $"画像全体 {_baseImage.PixelWidth} x {_baseImage.PixelHeight} px");
    }

    private async void OnExtractText(object sender, RoutedEventArgs e)
    {
        CommitInlineText();

        var (image, scope) = ResolveTargetImage();

        OcrButton.IsEnabled = false;
        UpdateStatus($"文字を読み取っています（{scope}）…");

        try
        {
            var outcome = await OcrService.RecognizeAsync(image, _settings.OcrLanguage);

            UpdateStatus(outcome.Message);

            if (!outcome.Success)
            {
                MessageBox.Show(this, outcome.Message, "文字の抽出", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            new TextResultWindow(
                "文字の抽出 (OCR)",
                $"{scope} から {outcome.LineCount} 行を読み取りました",
                outcome.Text)
            {
                Owner = this,
            }.ShowDialog();
        }
        finally
        {
            OcrButton.IsEnabled = true;
        }
    }

    private void OnScanCode(object sender, RoutedEventArgs e)
    {
        CommitInlineText();

        var (image, scope) = ResolveTargetImage();
        var hits = BarcodeService.DecodeAll(image);

        if (hits.Count == 0)
        {
            UpdateStatus("コードは見つかりませんでした");
            MessageBox.Show(
                this,
                $"{scope} から QR コード・バーコードを検出できませんでした。",
                "コードの読み取り",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var body = string.Join(
            Environment.NewLine + Environment.NewLine,
            hits.Select((hit, index) => $"[{index + 1}] {hit.Format}{Environment.NewLine}{hit.Text}"));

        UpdateStatus($"{hits.Count} 件のコードを読み取りました");

        new TextResultWindow(
            "コードの読み取り",
            $"{scope} から {hits.Count} 件を検出しました",
            body)
        {
            Owner = this,
        }.ShowDialog();
    }

    // ---------------------------------------------------------------- ズーム

    private void OnZoomIn(object sender, RoutedEventArgs e) => SetZoom(_zoom * 1.25);

    private void OnZoomOut(object sender, RoutedEventArgs e) => SetZoom(_zoom / 1.25);

    private void OnZoomReset(object sender, RoutedEventArgs e) => SetZoom(1.0);

    private void OnZoomFit(object sender, RoutedEventArgs e)
    {
        var availableWidth = Viewport.ActualWidth - 56;
        var availableHeight = Viewport.ActualHeight - 56;
        if (availableWidth <= 0 || availableHeight <= 0)
        {
            return;
        }

        // DIP ではなく物理ピクセル基準で収まる倍率を求める
        var fit = Math.Min(
            availableWidth * _dpiScale / _baseImage.PixelWidth,
            availableHeight * _dpiScale / _baseImage.PixelHeight);

        SetZoom(Math.Min(1.0, fit));
    }

    private void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, 0.1, 8.0);
        Render();
        UpdateStatus($"表示倍率 {_zoom * 100:0}%");
    }

    private void OnViewportMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        e.Handled = true;
        SetZoom(e.Delta > 0 ? _zoom * 1.15 : _zoom / 1.15);
    }

    // ---------------------------------------------------------------- キーボード

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // テキスト入力中はショートカットを奪わない
        if (_editingText is not null)
        {
            return;
        }

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.Z:
                    Undo();
                    e.Handled = true;
                    return;
                case Key.Y:
                    Redo();
                    e.Handled = true;
                    return;
                case Key.C:
                    OnCopy(sender, e);
                    e.Handled = true;
                    return;
                case Key.S:
                    Save();
                    e.Handled = true;
                    return;
            }

            return;
        }

        // 確定待ちの帯は、キーでも微調整できるようにする
        if (_band is { } pendingBand)
        {
            var sideways = _bandTool == CutColumnsTool;
            var step = e.Key switch
            {
                Key.Up when !sideways => -1.0,
                Key.Down when !sideways => 1.0,
                Key.Left when sideways => -1.0,
                Key.Right when sideways => 1.0,
                _ => 0.0,
            };

            if (step != 0)
            {
                // Shift を押していれば太さを変える。押していなければ帯ごと動かす
                _band = (Keyboard.Modifiers & ModifierKeys.Shift) != 0
                    ? pendingBand.Resize(step, BandTotal)
                    : pendingBand.Shift(step, BandTotal);

                ShowBand();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter)
            {
                ConfirmBand();
                e.Handled = true;
                return;
            }
        }

        switch (e.Key)
        {
            case Key.Delete or Key.Back:
                DeleteSelected();
                e.Handled = true;
                break;
            case Key.Escape:
                if (_band is not null)
                {
                    ClearBand("中止しました");
                }
                else if (_regionDragging)
                {
                    CancelRegionDrag();
                    UpdateStatus("中止しました");
                }
                else if (IsImageTool(_tool))
                {
                    // 画像を切るツールのままだと次のドラッグでまた切ってしまう
                    SelectTool_(SelectTool);
                    UpdateStatus("選択ツールに戻しました");
                }
                else
                {
                    _selected = null;
                    UpdateSelectionAdorner();
                }

                e.Handled = true;
                break;
            case Key.F2:
                if (_selected is { } editable && IsTextual(editable))
                {
                    BeginReedit(editable);
                }

                e.Handled = true;
                break;
            case Key.V:
                SelectTool_(SelectTool);
                e.Handled = true;
                break;
            case Key.A:
                SelectTool_(ShapeKind.Arrow);
                e.Handled = true;
                break;
            case Key.R:
                SelectTool_(ShapeKind.Rectangle);
                e.Handled = true;
                break;
            case Key.E:
                SelectTool_(ShapeKind.Ellipse);
                e.Handled = true;
                break;
            case Key.T:
                SelectTool_(ShapeKind.Text);
                e.Handled = true;
                break;
            case Key.C:
                SelectTool_(ShapeKind.Callout);
                e.Handled = true;
                break;
            case Key.S:
                SelectTool_(ShapeKind.Step);
                e.Handled = true;
                break;
            case Key.H:
                SelectTool_(ShapeKind.Highlight);
                e.Handled = true;
                break;
            case Key.B:
                SelectTool_(ShapeKind.Blur);
                e.Handled = true;
                break;
            case Key.X:
                SelectTool_(CropTool);
                e.Handled = true;
                break;
            case Key.N:
                SelectTool_(CutRowsTool);
                e.Handled = true;
                break;
            case Key.M:
                SelectTool_(CutColumnsTool);
                e.Handled = true;
                break;
        }
    }

    // ---------------------------------------------------------------- 終了

    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_editingText is not null)
        {
            CommitInlineText();
        }

        if (!_dirty)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            "編集内容が保存されていません。保存しますか？",
            "Torimaru",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        switch (answer)
        {
            case MessageBoxResult.Yes:
                if (!Save())
                {
                    e.Cancel = true;
                }

                break;

            case MessageBoxResult.Cancel:
                e.Cancel = true;
                break;
        }
    }

    private void UpdateStatus(string message) => StatusText.Text = message;
}
