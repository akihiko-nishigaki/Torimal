namespace Torimaru.App.Services;

/// <summary>読み取れたコード 1 件。</summary>
public sealed record BarcodeHit(string Format, string Text);

/// <summary>
/// 画像から QR コード / バーコードを読み取る。
/// ZXing.Net（純マネージド）を使うのでネイティブ依存は増えない。
/// </summary>
public static class BarcodeService
{
    /// <summary>画像に写っているコードをすべて読み取る。</summary>
    public static IReadOnlyList<BarcodeHit> DecodeAll(System.Windows.Media.Imaging.BitmapSource image)
    {
        try
        {
            var source = ToLuminanceSource(image);

            var reader = new ZXing.BarcodeReaderGeneric
            {
                AutoRotate = true,
                Options = new ZXing.Common.DecodingOptions
                {
                    TryHarder = true,
                    TryInverted = true,
                },
            };

            // まず複数読みを試し、取れなければ単独読みにフォールバックする
            var results = reader.DecodeMultiple(source);
            if (results is { Length: > 0 })
            {
                return results
                    .Where(r => !string.IsNullOrEmpty(r.Text))
                    .Select(r => new BarcodeHit(r.BarcodeFormat.ToString(), r.Text))
                    .ToList();
            }

            var single = reader.Decode(source);
            return single is null || string.IsNullOrEmpty(single.Text)
                ? []
                : [new BarcodeHit(single.BarcodeFormat.ToString(), single.Text)];
        }
        catch (Exception ex)
        {
            AppLog.Error("コードの読み取りに失敗", ex);
            return [];
        }
    }

    private static ZXing.RGBLuminanceSource ToLuminanceSource(
        System.Windows.Media.Imaging.BitmapSource image)
    {
        var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(
            image, System.Windows.Media.PixelFormats.Bgra32, null, 0);

        converted.Freeze();

        var stride = converted.PixelWidth * 4;
        var buffer = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(buffer, stride, 0);

        return new ZXing.RGBLuminanceSource(
            buffer,
            converted.PixelWidth,
            converted.PixelHeight,
            ZXing.RGBLuminanceSource.BitmapFormat.BGRA32);
    }
}
