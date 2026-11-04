namespace Torimaru.Core;

/// <summary>
/// 画像そのものを切る操作（トリミングと中抜き）。
///
/// 中抜きは「長い画像の不要な中間部分を捨てて前後を繋ぐ」操作で、
/// 縦長のページを 1 枚に収めたいときに使う。
/// 拡大縮小はせず行・列をそのまま詰めるので、文字がぼやけない。
///
/// WPF / GDI+ に依存しないので、そのままテストできる。
/// </summary>
public static class ImageSurgery
{
    /// <summary>指定範囲だけを残した新しい画像を返す。</summary>
    public static RawImage Crop(RawImage source, int x, int y, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "切り抜く範囲が空です。");
        }

        if (x < 0 || y < 0 || x + width > source.Width || y + height > source.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(x), "切り抜く範囲が画像の外へはみ出します。");
        }

        var result = RawImage.Create(width, height);
        var lineBytes = width * RawImage.BytesPerPixel;

        for (var row = 0; row < height; row++)
        {
            Buffer.BlockCopy(
                source.Pixels,
                ((y + row) * source.Stride) + (x * RawImage.BytesPerPixel),
                result.Pixels,
                row * result.Stride,
                lineBytes);
        }

        return result;
    }

    /// <summary>
    /// 横帯（<paramref name="count"/> 行）を抜いて、上下をそのまま繋いだ画像を返す。
    /// 画像は縦に縮む。
    /// </summary>
    public static RawImage RemoveRows(RawImage source, int startY, int count)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateBand(startY, count, source.Height, "行");

        var result = RawImage.Create(source.Width, source.Height - count);

        // 帯より上はそのまま、帯より下は帯の分だけ上へ詰める
        result.CopyRowsFrom(source, 0, 0, startY);
        result.CopyRowsFrom(source, startY + count, startY, source.Height - startY - count);

        return result;
    }

    /// <summary>
    /// 縦帯（<paramref name="count"/> 列）を抜いて、左右をそのまま繋いだ画像を返す。
    /// 画像は横に縮む。
    /// </summary>
    public static RawImage RemoveColumns(RawImage source, int startX, int count)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateBand(startX, count, source.Width, "列");

        var result = RawImage.Create(source.Width - count, source.Height);

        var leftBytes = startX * RawImage.BytesPerPixel;
        var rightBytes = (source.Width - startX - count) * RawImage.BytesPerPixel;

        for (var y = 0; y < source.Height; y++)
        {
            if (leftBytes > 0)
            {
                Buffer.BlockCopy(
                    source.Pixels, y * source.Stride,
                    result.Pixels, y * result.Stride,
                    leftBytes);
            }

            if (rightBytes > 0)
            {
                Buffer.BlockCopy(
                    source.Pixels, (y * source.Stride) + ((startX + count) * RawImage.BytesPerPixel),
                    result.Pixels, (y * result.Stride) + leftBytes,
                    rightBytes);
            }
        }

        return result;
    }

    private static void ValidateBand(int start, int count, int total, string unit)
    {
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), $"抜く{unit}数は 1 以上である必要があります。");
        }

        if (start < 0 || start + count > total)
        {
            throw new ArgumentOutOfRangeException(nameof(start), $"抜く{unit}が画像の外へはみ出します。");
        }

        if (count >= total)
        {
            throw new ArgumentOutOfRangeException(nameof(count), $"すべての{unit}を抜くことはできません。");
        }
    }
}
