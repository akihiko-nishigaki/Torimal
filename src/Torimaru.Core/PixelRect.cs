namespace Torimaru.Core;

/// <summary>
/// 画像ピクセル座標系の矩形。WPF / System.Drawing のどちらにも依存しない。
/// </summary>
public readonly record struct PixelRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>2 点から矩形を作る（ドラッグ方向に依らず正の幅・高さにする）。</summary>
    public static PixelRect FromPoints(double x1, double y1, double x2, double y2)
        => new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));

    /// <summary>負の幅・高さを正規化する。</summary>
    public PixelRect Normalized()
    {
        var x = Width < 0 ? X + Width : X;
        var y = Height < 0 ? Y + Height : Y;
        return new PixelRect(x, y, Math.Abs(Width), Math.Abs(Height));
    }

    /// <summary>指定境界に収まるようクリップする。</summary>
    public PixelRect Clip(double boundsWidth, double boundsHeight)
    {
        var n = Normalized();
        var x = Math.Clamp(n.X, 0, boundsWidth);
        var y = Math.Clamp(n.Y, 0, boundsHeight);
        var r = Math.Clamp(n.Right, 0, boundsWidth);
        var b = Math.Clamp(n.Bottom, 0, boundsHeight);
        return new PixelRect(x, y, Math.Max(0, r - x), Math.Max(0, b - y));
    }

    /// <summary>全方向に拡張する（ブラーの縁対策などで使用）。</summary>
    public PixelRect Inflate(double amount)
        => new(X - amount, Y - amount, Width + amount * 2, Height + amount * 2);

    public bool Contains(double px, double py)
        => px >= X && px <= Right && py >= Y && py <= Bottom;

    /// <summary>
    /// 別の矩形と面で重なっているか。
    /// 辺が触れているだけ（幅 0 の重なり）は重なりとみなさない。
    /// </summary>
    public bool IntersectsWith(PixelRect other)
    {
        var a = Normalized();
        var b = other.Normalized();
        return a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;
    }

    /// <summary>ピクセル整数へ丸める（キャプチャ切り出し用）。</summary>
    public (int X, int Y, int Width, int Height) ToIntegerBounds()
    {
        var n = Normalized();
        var x = (int)Math.Floor(n.X);
        var y = (int)Math.Floor(n.Y);
        var w = (int)Math.Ceiling(n.Right) - x;
        var h = (int)Math.Ceiling(n.Bottom) - y;
        return (x, y, Math.Max(0, w), Math.Max(0, h));
    }
}
