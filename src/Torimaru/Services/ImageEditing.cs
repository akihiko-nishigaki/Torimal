using System.Windows.Media;
using System.Windows.Media.Imaging;
using Torimaru.Core;

namespace Torimaru.App.Services;

/// <summary>
/// 画像そのものを切る操作を WPF の画像に対して行う。
/// 計算は <see cref="ImageSurgery"/>（WPF 非依存）に任せ、ここは変換だけを担う。
///
/// 拡大縮小を一切挟まないので、切っても文字がぼやけない。
/// </summary>
public static class ImageEditing
{
    /// <summary>指定範囲だけを残す。</summary>
    public static BitmapSource Crop(BitmapSource image, int x, int y, int width, int height)
        => ToBitmapSource(ImageSurgery.Crop(ToRaw(image), x, y, width, height));

    /// <summary>横帯を抜いて上下を繋ぐ（画像が縦に縮む）。</summary>
    public static BitmapSource RemoveRows(BitmapSource image, int startY, int count)
        => ToBitmapSource(ImageSurgery.RemoveRows(ToRaw(image), startY, count));

    /// <summary>縦帯を抜いて左右を繋ぐ（画像が横に縮む）。</summary>
    public static BitmapSource RemoveColumns(BitmapSource image, int startX, int count)
        => ToBitmapSource(ImageSurgery.RemoveColumns(ToRaw(image), startX, count));

    /// <summary>
    /// BGRA 32bit の生ビットマップへ落とす。
    /// 透過を保つため Bgra32 に揃える（元が Bgr32 でも情報は失われない）。
    /// </summary>
    public static RawImage ToRaw(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.Freeze();

        var stride = converted.PixelWidth * RawImage.BytesPerPixel;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);

        return new RawImage(pixels, converted.PixelWidth, converted.PixelHeight, stride);
    }

    /// <summary>
    /// 生ビットマップを表示・保存できる画像に戻す。
    ///
    /// DPI は 96 に揃える。注釈の描画も焼き込みもピクセル数で座標を決めているので、
    /// ここで DPI を引きずると「切っただけなのに注釈がずれる」ことになる。
    /// </summary>
    public static BitmapSource ToBitmapSource(RawImage image)
    {
        var source = BitmapSource.Create(
            image.Width,
            image.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            image.Pixels,
            image.Stride);

        source.Freeze();
        return source;
    }
}
