namespace Torimaru.Core;

/// <summary>
/// BGRA 32bit の生ビットマップ。WPF / GDI+ に依存しないので、
/// スクロール合成のロジックをそのままテストできる。
/// </summary>
public sealed class RawImage
{
    public const int BytesPerPixel = 4;

    public RawImage(byte[] pixels, int width, int height, int stride)
    {
        ArgumentNullException.ThrowIfNull(pixels);

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "幅と高さは 1 以上である必要があります。");
        }

        if (stride < width * BytesPerPixel)
        {
            throw new ArgumentOutOfRangeException(nameof(stride), "ストライドが幅に対して小さすぎます。");
        }

        if (pixels.Length < stride * height)
        {
            throw new ArgumentException("ピクセル配列が指定の寸法に足りません。", nameof(pixels));
        }

        Pixels = pixels;
        Width = width;
        Height = height;
        Stride = stride;
    }

    public byte[] Pixels { get; }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public static RawImage Create(int width, int height)
    {
        var stride = width * BytesPerPixel;
        return new RawImage(new byte[stride * height], width, height, stride);
    }

    public ReadOnlySpan<byte> Row(int y) => Pixels.AsSpan(y * Stride, Width * BytesPerPixel);

    /// <summary>別の画像から行単位でコピーする。幅は一致している必要がある。</summary>
    public void CopyRowsFrom(RawImage source, int sourceY, int destinationY, int rowCount)
    {
        if (source.Width != Width)
        {
            throw new ArgumentException("幅が一致しません。", nameof(source));
        }

        if (sourceY < 0 || destinationY < 0 || rowCount < 0
            || sourceY + rowCount > source.Height
            || destinationY + rowCount > Height)
        {
            throw new ArgumentOutOfRangeException(nameof(rowCount), "コピー範囲が画像の外へはみ出します。");
        }

        var lineBytes = Width * BytesPerPixel;
        for (var i = 0; i < rowCount; i++)
        {
            Buffer.BlockCopy(
                source.Pixels,
                ((sourceY + i) * source.Stride),
                Pixels,
                ((destinationY + i) * Stride),
                lineBytes);
        }
    }

    /// <summary>上から <paramref name="height"/> 行だけを取り出した新しい画像を返す。</summary>
    public RawImage TakeRows(int startY, int height)
    {
        var result = Create(Width, height);
        result.CopyRowsFrom(this, startY, 0, height);
        return result;
    }
}
