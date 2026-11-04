using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Torimaru.Core;

namespace Torimaru.App.Editor;

/// <summary>
/// 注釈モデルを WPF の視覚要素へ変換する。
///
/// 画面表示と PNG 書き出しで同じコードを通すのが要点。
/// scale を変えるだけで等倍書き出しにも画面表示にも使えるので、
/// 「見た目と保存結果が違う」というズレが起きない。
/// </summary>
public static class ShapeRenderer
{
    /// <summary>既定の注釈色（赤）。</summary>
    public const string DefaultColor = "#FFE53935";

    /// <summary>
    /// 下地の画像と注釈を載せた Canvas を組み立てる。
    /// </summary>
    /// <param name="baseImage">注釈前の画像。</param>
    /// <param name="document">注釈。</param>
    /// <param name="scale">画像 1 ピクセルあたりの DIP。書き出し時は 1.0。</param>
    /// <param name="hidden">
    /// 描かない図形。インライン入力中の文字・吹き出しを二重に見せないために使う。
    /// </param>
    public static Canvas BuildCanvas(
        BitmapSource baseImage,
        AnnotationDocument document,
        double scale,
        AnnotationShape? hidden = null)
    {
        var width = baseImage.PixelWidth * scale;
        var height = baseImage.PixelHeight * scale;

        var canvas = new Canvas
        {
            Width = width,
            Height = height,
            Background = Brushes.Transparent,
            SnapsToDevicePixels = true,

            // 書き出し時は画像の寸法で切られるので、画面でも同じように切る。
            // トリミング後に画像の外へ出た注釈が画面だけに残るのを防ぐ。
            ClipToBounds = true,
        };

        var backdrop = new Image
        {
            Source = baseImage,
            Width = width,
            Height = height,
            Stretch = Stretch.Fill,
        };

        RenderOptions.SetBitmapScalingMode(backdrop, BitmapScalingMode.HighQuality);
        Canvas.SetLeft(backdrop, 0);
        Canvas.SetTop(backdrop, 0);
        canvas.Children.Add(backdrop);

        foreach (var shape in document.Shapes)
        {
            if (ReferenceEquals(shape, hidden))
            {
                continue;
            }

            var element = CreateVisual(shape, baseImage, scale);
            if (element is not null)
            {
                canvas.Children.Add(element);
            }
        }

        return canvas;
    }

    private static UIElement? CreateVisual(AnnotationShape shape, BitmapSource baseImage, double scale)
        => shape.Kind switch
        {
            ShapeKind.Arrow => CreateArrow(shape, scale),
            ShapeKind.Rectangle => CreateRectangle(shape, scale),
            ShapeKind.Ellipse => CreateEllipse(shape, scale),
            ShapeKind.Text => CreateText(shape, scale),
            ShapeKind.Callout => CreateCallout(shape, scale),
            ShapeKind.Step => CreateStep(shape, scale),
            ShapeKind.Highlight => CreateHighlight(shape, scale),
            ShapeKind.Blur => CreateBlur(shape, baseImage, scale),
            _ => null,
        };

    public static Color ParseColor(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            try
            {
                var converted = ColorConverter.ConvertFromString(value);
                if (converted is Color color)
                {
                    return color;
                }
            }
            catch (FormatException)
            {
                // 下の既定色へ落ちる
            }
        }

        return (Color)ColorConverter.ConvertFromString(DefaultColor)!;
    }

    private static SolidColorBrush Brush(string? value, double opacity = 1.0)
    {
        var brush = new SolidColorBrush(ParseColor(value)) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static void Place(FrameworkElement element, PixelRect bounds, double scale)
    {
        Canvas.SetLeft(element, bounds.X * scale);
        Canvas.SetTop(element, bounds.Y * scale);
    }

    /// <summary>
    /// 塗りつぶしの矢印。輪郭を 1 本のポリゴンで作るので、
    /// 軸と矢じりの継ぎ目が見えない。
    /// </summary>
    private static UIElement? CreateArrow(AnnotationShape shape, double scale)
    {
        var sx = shape.X * scale;
        var sy = shape.Y * scale;
        var ex = (shape.X + shape.Width) * scale;
        var ey = (shape.Y + shape.Height) * scale;

        var dx = ex - sx;
        var dy = ey - sy;
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length < 1)
        {
            return null;
        }

        var ux = dx / length;
        var uy = dy / length;
        var px = -uy;
        var py = ux;

        var thickness = Math.Max(1, shape.Thickness * scale);
        var halfShaft = thickness / 2;
        var headLength = Math.Min(length, Math.Max(thickness * 3.2, 10 * scale));
        var halfHead = Math.Max(thickness * 1.6, 6 * scale);

        var bx = ex - (ux * headLength);
        var by = ey - (uy * headLength);

        var points = new PointCollection
        {
            new(sx + (px * halfShaft), sy + (py * halfShaft)),
            new(bx + (px * halfShaft), by + (py * halfShaft)),
            new(bx + (px * halfHead), by + (py * halfHead)),
            new(ex, ey),
            new(bx - (px * halfHead), by - (py * halfHead)),
            new(bx - (px * halfShaft), by - (py * halfShaft)),
            new(sx - (px * halfShaft), sy - (py * halfShaft)),
        };

        var polygon = new Polygon
        {
            Points = points,
            Fill = Brush(shape.Color),
            StrokeThickness = 0,
        };

        Canvas.SetLeft(polygon, 0);
        Canvas.SetTop(polygon, 0);
        return polygon;
    }

    private static UIElement CreateRectangle(AnnotationShape shape, double scale)
    {
        var bounds = shape.Bounds.Normalized();
        var rectangle = new System.Windows.Shapes.Rectangle
        {
            Width = Math.Max(1, bounds.Width * scale),
            Height = Math.Max(1, bounds.Height * scale),
            Stroke = Brush(shape.Color),
            StrokeThickness = Math.Max(1, shape.Thickness * scale),
            Fill = shape.Filled ? Brush(shape.Color, 0.28) : Brushes.Transparent,
        };

        Place(rectangle, bounds, scale);
        return rectangle;
    }

    private static UIElement CreateEllipse(AnnotationShape shape, double scale)
    {
        var bounds = shape.Bounds.Normalized();
        var ellipse = new System.Windows.Shapes.Ellipse
        {
            Width = Math.Max(1, bounds.Width * scale),
            Height = Math.Max(1, bounds.Height * scale),
            Stroke = Brush(shape.Color),
            StrokeThickness = Math.Max(1, shape.Thickness * scale),
            Fill = shape.Filled ? Brush(shape.Color, 0.28) : Brushes.Transparent,
        };

        Place(ellipse, bounds, scale);
        return ellipse;
    }

    /// <summary>
    /// 書体を指定していない注釈の書体。
    /// 書体を選べるようにする前に保存した PNG を、当時と同じ見た目で再現するために残している。
    /// </summary>
    public static readonly FontFamily TextFontFamily = new("Yu Gothic UI, Segoe UI");

    private static readonly Dictionary<string, FontFamily> FontFamilyCache = [];

    /// <summary>
    /// 書体名から FontFamily を得る。測定 (MeasureText) と描画で必ずこれを通し、
    /// 選択枠と実際の文字がずれないようにする。
    /// </summary>
    public static FontFamily ResolveFontFamily(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return TextFontFamily;
        }

        if (FontFamilyCache.TryGetValue(name, out var cached))
        {
            return cached;
        }

        // 入っていない書体を指定しても WPF は代替書体で描くので、ここでは弾かない
        var family = new FontFamily(name);
        FontFamilyCache[name] = family;
        return family;
    }

    /// <summary>
    /// テキストの実寸を測る。改行を含む場合は行数ぶんの高さ・最長行の幅になる。
    /// </summary>
    public static (double Width, double Height) MeasureText(
        string? text, double fontSize, double dpiScale, string? fontFamily = null)
    {
        var typeface = new Typeface(
            ResolveFontFamily(fontFamily),
            FontStyles.Normal,
            FontWeights.Bold,
            FontStretches.Normal);

        var formatted = new FormattedText(
            text ?? string.Empty,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            Brushes.Black,
            dpiScale <= 0 ? 1 : dpiScale);

        return (formatted.Width, formatted.Height);
    }

    /// <summary>吹き出しの内側余白（横）。</summary>
    public static double CalloutPaddingX(double fontSize) => Math.Max(6, fontSize * 0.55);

    /// <summary>吹き出しの内側余白（縦）。</summary>
    public static double CalloutPaddingY(double fontSize) => Math.Max(4, fontSize * 0.32);

    /// <summary>楕円に文字を収めるために本体を広げる倍率（内接長方形の対角比）。</summary>
    private static readonly double OvalMargin = Math.Sqrt(2);

    /// <summary>吹き出し本体を文字に合わせた大きさにする。形によって必要な大きさが違う。</summary>
    public static void ApplyCalloutSize(AnnotationShape shape, double dpiScale)
    {
        var (textWidth, textHeight) = MeasureText(
            shape.Text, shape.FontSize, dpiScale, shape.FontFamily);
        var width = textWidth + (CalloutPaddingX(shape.FontSize) * 2);
        var height = textHeight + (CalloutPaddingY(shape.FontSize) * 2);

        // 楕円は長方形より内側が狭いので、同じ文字を収めるには軸を伸ばす必要がある
        if (CalloutShapes.Normalize(shape.CalloutShape) == CalloutShapes.Oval)
        {
            width *= OvalMargin;
            height *= OvalMargin;
        }

        shape.Width = width;
        shape.Height = height;
    }

    /// <summary>
    /// 本体の左上から見た文字の置き位置（画像ピクセル）。
    /// 入力欄 (EditorWindow) と焼き込みで同じ値を使い、確定前後で文字がずれないようにする。
    /// </summary>
    public static (double X, double Y) CalloutTextOffset(AnnotationShape shape)
    {
        var padX = CalloutPaddingX(shape.FontSize);
        var padY = CalloutPaddingY(shape.FontSize);
        var bounds = shape.Bounds.Normalized();

        // 楕円だけは中央寄せ（四隅が使えないため）。大きさが未確定なら余白ぶんに逃がす。
        if (CalloutShapes.Normalize(shape.CalloutShape) != CalloutShapes.Oval
            || bounds.Width < 2
            || bounds.Height < 2)
        {
            return (padX, padY);
        }

        var textWidth = Math.Max(0, (bounds.Width / OvalMargin) - (padX * 2));
        var textHeight = Math.Max(0, (bounds.Height / OvalMargin) - (padY * 2));
        return ((bounds.Width - textWidth) / 2, (bounds.Height - textHeight) / 2);
    }

    /// <summary>吹き出しの配色。地・枠・文字の 3 色と枠の太さ。</summary>
    public readonly record struct CalloutLook(
        Color Fill, Color Stroke, Color Text, double StrokeWidth);

    private static readonly Color DarkFill = Color.FromRgb(0x21, 0x21, 0x21);

    /// <summary>明るさ（0〜1）。白地に白い文字を描いてしまう事故を避けるために使う。</summary>
    private static double Luminance(Color color)
        => ((0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B)) / 255.0;

    /// <summary>選んだ色とスタイルから実際の 3 色を決める。</summary>
    public static CalloutLook ResolveCalloutLook(AnnotationShape shape)
    {
        var color = ParseColor(shape.Color);

        return CalloutStyles.Normalize(shape.CalloutStyle) switch
        {
            // 白地。白に近い色を選んだときは何も見えなくなるので、地を暗い色へ反転する
            CalloutStyles.Outline => new CalloutLook(
                Luminance(color) > 0.82 ? DarkFill : Colors.White, color, color, 2.4),

            // 黒地。明るい画面の上でも文字が沈まない
            CalloutStyles.Dark => new CalloutLook(DarkFill, color, Colors.White, 2.4),

            // 色で塗り。暗い背景でも輪郭が分かるように白で縁取る
            _ => new CalloutLook(color, Colors.White, Colors.White, 1.5),
        };
    }

    /// <summary>測った実寸を図形に反映する（当たり判定と選択枠に使われる）。</summary>
    public static void ApplyMeasuredSize(AnnotationShape shape, double dpiScale)
    {
        var (width, height) = MeasureText(
            shape.Text, shape.FontSize, dpiScale, shape.FontFamily);
        shape.Width = Math.Max(4, width);
        shape.Height = Math.Max(4, height);
    }

    private static UIElement CreateText(AnnotationShape shape, double scale)
    {
        var block = new TextBlock
        {
            Text = shape.Text ?? string.Empty,
            Foreground = Brush(shape.Color),
            FontFamily = ResolveFontFamily(shape.FontFamily),
            FontSize = Math.Max(6, shape.FontSize * scale),
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.NoWrap,

            // どんな背景でも読めるよう、薄く影を落とす
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 3 * scale,
                ShadowDepth = 1 * scale,
                Opacity = 0.55,
            },
        };

        Place(block, shape.Bounds.Normalized(), scale);
        return block;
    }

    /// <summary>
    /// 吹き出し。本体と、しっぽ（三角）を 1 つの Geometry に合成して描く。
    ///
    /// 2 つの図形を重ねて描くと接合部に線が残るので、Geometry.Combine で
    /// 輪郭ごと 1 つにまとめてから塗り + 縁取りを乗せている。
    /// </summary>
    private static UIElement CreateCallout(AnnotationShape shape, double scale)
    {
        var bounds = shape.Bounds.Normalized();
        var width = Math.Max(1, bounds.Width * scale);
        var height = Math.Max(1, bounds.Height * scale);

        var body = CalloutShapes.Normalize(shape.CalloutShape);
        var look = ResolveCalloutLook(shape);

        // 本体座標系（左上が原点）で組む。しっぽが本体の外に出るので負の座標も使う。
        var geometry = BuildBody(body, width, height, scale);

        var tip = new Point(
            (shape.TailX - bounds.X) * scale,
            (shape.TailY - bounds.Y) * scale);

        var tail = BuildTail(geometry, body, width, height, tip, scale);
        if (tail is not null)
        {
            geometry = Geometry.Combine(geometry, tail, GeometryCombineMode.Union, null);
        }

        var container = new Canvas();

        container.Children.Add(new System.Windows.Shapes.Path
        {
            Data = geometry,
            Fill = Brush(look.Fill),
            Stroke = Brush(look.Stroke),
            StrokeThickness = Math.Max(1, look.StrokeWidth * scale),
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 4 * scale,
                ShadowDepth = 1.5 * scale,
                Opacity = 0.45,
            },
        });

        var text = new TextBlock
        {
            Text = shape.Text ?? string.Empty,
            Foreground = Brush(look.Text),
            FontFamily = ResolveFontFamily(shape.FontFamily),
            FontSize = Math.Max(6, shape.FontSize * scale),
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.NoWrap,
        };

        var (offsetX, offsetY) = CalloutTextOffset(shape);
        Canvas.SetLeft(text, offsetX * scale);
        Canvas.SetTop(text, offsetY * scale);
        container.Children.Add(text);

        Place(container, bounds, scale);
        return container;
    }

    /// <summary>吹き出し本体の輪郭。</summary>
    private static Geometry BuildBody(string body, double width, double height, double scale)
    {
        if (body == CalloutShapes.Oval)
        {
            return new EllipseGeometry(
                new Point(width / 2, height / 2), width / 2, height / 2);
        }

        var radius = body == CalloutShapes.Rect
            ? 0
            : Math.Min(Math.Min(width, height) / 3, 12 * scale);

        return new RectangleGeometry(new Rect(0, 0, width, height), radius, radius);
    }

    /// <summary>中心から本体の縁までの距離（長方形）。</summary>
    private static double RectEdgeDistance(double halfWidth, double halfHeight, double dx, double dy)
        => Math.Min(
            Math.Abs(dx) < 1e-6 ? double.MaxValue : halfWidth / Math.Abs(dx),
            Math.Abs(dy) < 1e-6 ? double.MaxValue : halfHeight / Math.Abs(dy));

    /// <summary>中心から本体の縁までの距離（楕円）。</summary>
    private static double EllipseEdgeDistance(double halfWidth, double halfHeight, double dx, double dy)
    {
        var qx = dx / halfWidth;
        var qy = dy / halfHeight;
        var q = Math.Sqrt((qx * qx) + (qy * qy));
        return q < 1e-9 ? double.MaxValue : 1 / q;
    }

    /// <summary>
    /// しっぽの三角形を作る。先端が本体の内側にあるときは null（しっぽなし）。
    /// 付け根は「中心から先端へ向かう線」と本体の縁の交点に置くので、
    /// 先端をどこへ動かしても正しい辺から生える。
    /// </summary>
    private static Geometry? BuildTail(
        Geometry bodyGeometry, string body, double width, double height, Point tip, double scale)
    {
        // 文字を入力している最中の吹き出しはまだ大きさが決まっていない（0 x 0）。
        // その状態で付け根の幅を計算すると下限が上限を上回るので、しっぽは描かない。
        if (width < 2 || height < 2)
        {
            return null;
        }

        var center = new Point(width / 2, height / 2);
        var dx = tip.X - center.X;
        var dy = tip.Y - center.Y;
        var length = Math.Sqrt((dx * dx) + (dy * dy));

        if (length < 1)
        {
            return null;
        }

        dx /= length;
        dy /= length;

        var isOval = body == CalloutShapes.Oval;
        var toEdge = isOval
            ? EllipseEdgeDistance(width / 2, height / 2, dx, dy)
            : RectEdgeDistance(width / 2, height / 2, dx, dy);

        // 先端が本体の内側なら、しっぽを出さない（角丸ラベルになる）
        if (length <= toEdge)
        {
            return null;
        }

        // 根元の幅。先端を遠くへ伸ばすほど広げないと針のように細く見えるが、
        // 広すぎると吹き出しではなく「旗つきの矢印」になってしまう。
        //
        // 上限は本体の短辺ではなく、しっぽが生える辺の長さ（＝しっぽと直角の
        // 向きに本体を横切る長さ）で決める。短辺で決めると、横向きのしっぽが
        // 高さいっぱいまで広がって本体の片側を食いつぶす。
        var perpHalf = isOval
            ? EllipseEdgeDistance(width / 2, height / 2, -dy, dx)
            : RectEdgeDistance(width / 2, height / 2, -dy, dx);

        var maxHalf = Math.Max(3 * scale, perpHalf * 0.42);
        var minHalf = Math.Min(maxHalf, Math.Max(4 * scale, perpHalf * 0.22));
        var half = Math.Clamp(length * 0.16, minHalf, maxHalf);

        // 付け根は「本体の中心から見た角度」で縁の上に 2 点取る。
        //
        // 「縁まで進んでから直角方向へずらす」方式だと、斜めに引いたときに
        // ずらした先が本体の外へ出てしまい、しっぽが本体から切り離されて
        // 見える（＝針が刺さったような形。しっぽを伸ばすほど悪化する）。
        // 角度で取れば 2 点とも必ず縁の上に乗るので、どの向きでも縁から生える。
        var angle = Math.Atan2(dy, dx);
        var spread = Math.Clamp(Math.Atan2(half, Math.Max(toEdge, 1)), 0.03, 1.0);

        var start = center;
        var end = center;

        for (var attempt = 0; attempt < 8; attempt++)
        {
            start = EdgeAnchor(bodyGeometry, center, width, height, isOval, angle - spread);
            end = EdgeAnchor(bodyGeometry, center, width, height, isOval, angle + spread);

            // 平たい本体を斜めに引くと、同じ角度でも縁までの距離が左右で大きく
            // 違い、付け根だけが間延びする。狙った幅に収まるまで角度を狭める。
            var spanX = start.X - end.X;
            var spanY = start.Y - end.Y;

            if (Math.Sqrt((spanX * spanX) + (spanY * spanY)) <= half * 2.2 || spread <= 0.04)
            {
                break;
            }

            spread *= 0.7;
        }

        var figure = new PathFigure
        {
            StartPoint = start,
            IsClosed = true,
            IsFilled = true,
        };

        figure.Segments.Add(new LineSegment(tip, isStroked: true));
        figure.Segments.Add(new LineSegment(end, isStroked: true));

        var path = new PathGeometry();
        path.Figures.Add(figure);
        return path;
    }

    /// <summary>
    /// 中心から指定の角度へ進み、本体の縁のすぐ内側に点を置く。
    /// 角丸や楕円では計算上の縁より実際の輪郭が内側にあるので、
    /// 本当に本体の中に入るまで少しずつ中心側へ戻す。
    /// </summary>
    private static Point EdgeAnchor(
        Geometry bodyGeometry, Point center, double width, double height, bool isOval, double angle)
    {
        var ux = Math.Cos(angle);
        var uy = Math.Sin(angle);

        var edge = isOval
            ? EllipseEdgeDistance(width / 2, height / 2, ux, uy)
            : RectEdgeDistance(width / 2, height / 2, ux, uy);

        for (var inset = 0.94; inset > 0.1; inset -= 0.06)
        {
            var point = new Point(
                center.X + (ux * edge * inset),
                center.Y + (uy * edge * inset));

            if (bodyGeometry.FillContains(point))
            {
                return point;
            }
        }

        return center;
    }

    /// <summary>番号付きマーカー（手順書で多用するのでステップ専用ツールにしている）。</summary>
    private static UIElement CreateStep(AnnotationShape shape, double scale)
    {
        var bounds = shape.Bounds.Normalized();
        var diameter = Math.Max(1, Math.Max(bounds.Width, bounds.Height) * scale);

        var container = new Grid
        {
            Width = diameter,
            Height = diameter,
        };

        container.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Fill = Brush(shape.Color),
            Stroke = Brushes.White,
            StrokeThickness = Math.Max(1, diameter * 0.07),
        });

        container.Children.Add(new TextBlock
        {
            Text = shape.Number.ToString(CultureInfo.InvariantCulture),
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold,
            FontSize = Math.Max(6, diameter * 0.56),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        });

        Place(container, bounds, scale);
        return container;
    }

    private static UIElement CreateHighlight(AnnotationShape shape, double scale)
    {
        var bounds = shape.Bounds.Normalized();
        var rectangle = new System.Windows.Shapes.Rectangle
        {
            Width = Math.Max(1, bounds.Width * scale),
            Height = Math.Max(1, bounds.Height * scale),
            Fill = Brush(shape.Color, 0.35),
            StrokeThickness = 0,
        };

        Place(rectangle, bounds, scale);
        return rectangle;
    }

    /// <summary>
    /// ぼかし。下地を切り出してからブラーをかける。
    /// ブラーは縁が内側へ透けるため、少し広めに切り出して内側だけを見せる。
    /// </summary>
    private static UIElement? CreateBlur(AnnotationShape shape, BitmapSource baseImage, double scale)
    {
        var bounds = shape.Bounds.Normalized().Clip(baseImage.PixelWidth, baseImage.PixelHeight);
        if (bounds.Width < 1 || bounds.Height < 1)
        {
            return null;
        }

        var radius = Math.Max(1, shape.BlurRadius);
        var padded = bounds.Inflate(radius * 2).Clip(baseImage.PixelWidth, baseImage.PixelHeight);
        var (px, py, pw, ph) = padded.ToIntegerBounds();

        pw = Math.Min(pw, baseImage.PixelWidth - px);
        ph = Math.Min(ph, baseImage.PixelHeight - py);
        if (pw < 1 || ph < 1)
        {
            return null;
        }

        CroppedBitmap crop;
        try
        {
            crop = new CroppedBitmap(baseImage, new Int32Rect(px, py, pw, ph));
            crop.Freeze();
        }
        catch (ArgumentException)
        {
            return null;
        }

        var image = new Image
        {
            Source = crop,
            Width = pw * scale,
            Height = ph * scale,
            Stretch = Stretch.Fill,
            Effect = new BlurEffect
            {
                Radius = radius * scale,
                KernelType = KernelType.Gaussian,
                RenderingBias = RenderingBias.Quality,
            },

            // はみ出した「ぼけ足」を隠し、指定範囲だけを置き換える
            Clip = new RectangleGeometry(new Rect(
                (bounds.X - px) * scale,
                (bounds.Y - py) * scale,
                bounds.Width * scale,
                bounds.Height * scale)),
        };

        Canvas.SetLeft(image, px * scale);
        Canvas.SetTop(image, py * scale);
        return image;
    }
}
