using System.Windows.Media;
using System.Windows.Media.Imaging;
using Torimaru.Core;

namespace Torimaru.App.Capture;

/// <summary>
/// 取り込んだ画面のビットマップ。表示用の <see cref="BitmapSource"/> と
/// 色取得用の生ピクセルの両方を保持する（ルーペとカラー表示のため）。
/// </summary>
public sealed class CapturedImage
{
    private readonly byte[] _pixels;

    public CapturedImage(byte[] pixels, int width, int height, int stride, int originX, int originY)
    {
        _pixels = pixels;
        Width = width;
        Height = height;
        Stride = stride;
        OriginX = originX;
        OriginY = originY;

        Source = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
        Source.Freeze();
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    /// <summary>仮想画面の左上座標（マルチモニタでは負になりうる）。</summary>
    public int OriginX { get; }

    public int OriginY { get; }

    public BitmapSource Source { get; }

    public PixelRect FullBounds => new(0, 0, Width, Height);

    /// <summary>画像ピクセル座標の色を返す。範囲外は透明を返す。</summary>
    public Color ColorAt(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
        {
            return Colors.Transparent;
        }

        var offset = (y * Stride) + (x * 4);
        return Color.FromRgb(_pixels[offset + 2], _pixels[offset + 1], _pixels[offset]);
    }

    /// <summary>指定範囲を切り出した新しい <see cref="BitmapSource"/> を返す。</summary>
    public BitmapSource Crop(PixelRect region)
    {
        var clipped = region.Clip(Width, Height);
        var (x, y, w, h) = clipped.ToIntegerBounds();

        w = Math.Min(w, Width - x);
        h = Math.Min(h, Height - y);

        if (w <= 0 || h <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(region), "切り出し範囲が空です。");
        }

        var cropped = new CroppedBitmap(Source, new System.Windows.Int32Rect(x, y, w, h));
        cropped.Freeze();
        return cropped;
    }

    /// <summary>仮想画面座標を画像ピクセル座標へ変換する。</summary>
    public (double X, double Y) ToImagePoint(double virtualX, double virtualY)
        => (virtualX - OriginX, virtualY - OriginY);
}
