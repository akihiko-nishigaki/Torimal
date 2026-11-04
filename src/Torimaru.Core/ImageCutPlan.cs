namespace Torimaru.Core;

/// <summary>
/// ドラッグで指定された範囲から「実際に切る整数の範囲」を決める。
///
/// 画面側に置くと目で確かめるしかなくなるので、
/// 丸め方と受け付けない条件はここに集めてテストできるようにしている。
/// </summary>
public static class ImageCutPlan
{
    /// <summary>抜く帯として受け付ける最小の太さ（px）。誤クリックで切らないための下限。</summary>
    public const int MinimumBand = 2;

    /// <summary>切ったあとに残す最小の寸法（px）。</summary>
    public const int MinimumRemaining = 8;

    /// <summary>
    /// トリミングで残す範囲を決める。
    /// 受け付けられない場合は <paramref name="problem"/> に理由を入れて false を返す。
    /// </summary>
    public static bool TryPlanCrop(
        PixelRect dragged,
        int width,
        int height,
        out (int X, int Y, int Width, int Height) crop,
        out string? problem)
    {
        var (x, y, w, h) = dragged.Clip(width, height).ToIntegerBounds();

        // 整数へ丸める過程で 1px はみ出ることがあるので詰め直す
        w = Math.Min(w, width - x);
        h = Math.Min(h, height - y);

        crop = (x, y, w, h);

        if (w < MinimumRemaining || h < MinimumRemaining)
        {
            problem = "範囲が小さすぎます。残したい範囲をドラッグしてください";
            return false;
        }

        if (w == width && h == height)
        {
            problem = "画像全体を指定したので何も切りませんでした";
            return false;
        }

        problem = null;
        return true;
    }

    /// <summary>
    /// 中抜きで抜く帯を決める。<paramref name="a"/> と <paramref name="b"/> は
    /// ドラッグの始点・終点（順番はどちらでもよい）。
    /// </summary>
    public static bool TryPlanBand(
        double a, double b, int total, out int start, out int count, out string? problem)
    {
        // 指定した端のピクセルまで含めて抜く（内側に丸めると 1px 残る）
        start = Math.Clamp((int)Math.Floor(Math.Min(a, b)), 0, total);
        var endExclusive = Math.Clamp((int)Math.Ceiling(Math.Max(a, b)), start, total);
        count = endExclusive - start;

        if (count < MinimumBand)
        {
            problem = "帯が細すぎます。消したい中間部分をドラッグしてください";
            return false;
        }

        if (total - count < MinimumRemaining)
        {
            problem = $"残りが {total - count} px になってしまいます。もっと狭い範囲を指定してください";
            count = 0;
            return false;
        }

        problem = null;
        return true;
    }
}
