namespace Torimaru.Core;

/// <summary>中抜きの確定待ちの帯で、いまつまんでいる場所。</summary>
public enum BandGrip
{
    /// <summary>帯の外（＝新しく引き直す）。</summary>
    None,

    /// <summary>始まりの辺（上端・左端）。</summary>
    Start,

    /// <summary>終わりの辺（下端・右端）。</summary>
    End,

    /// <summary>帯の内側（＝太さを変えずに帯ごと動かす）。</summary>
    Whole,
}

/// <summary>
/// 中抜きで「抜く帯」を確定する前の状態。画像座標の 1 軸だけを持つ。
///
/// どの辺をつまんだか・画像の外へ出さないための詰め方・前後が入れ替わったときの扱いは、
/// 画面側に置くと目で確かめるしかなくなるので、ここに集めてテストできるようにしている。
/// </summary>
/// <param name="Start">帯の始まり（上端・左端）。</param>
/// <param name="End">帯の終わり（下端・右端）。<paramref name="Start"/> 以上。</param>
public readonly record struct BandSelection(double Start, double End)
{
    /// <summary>帯の太さ。</summary>
    public double Thickness => End - Start;

    /// <summary>ドラッグの始点・終点から作る（順番はどちらでもよい）。</summary>
    public static BandSelection FromDrag(double a, double b, int total)
        => new(Clamp(Math.Min(a, b), total), Clamp(Math.Max(a, b), total));

    /// <summary>
    /// その位置でつまめる場所を返す。
    /// 辺は帯の内側より優先する（細い帯でも端をつまめるようにするため）。
    /// </summary>
    /// <param name="value">つまんだ位置（画像座標）。</param>
    /// <param name="tolerance">辺とみなす距離。画面上で指が届く幅を画像座標に直した値。</param>
    public BandGrip HitTest(double value, double tolerance)
    {
        var near = Math.Max(0, tolerance);
        var toStart = Math.Abs(value - Start);
        var toEnd = Math.Abs(value - End);

        // 細い帯では両方の辺が範囲に入るので、近いほうを選ぶ
        if (toStart <= near || toEnd <= near)
        {
            return toStart <= toEnd ? BandGrip.Start : BandGrip.End;
        }

        return value > Start && value < End ? BandGrip.Whole : BandGrip.None;
    }

    /// <summary>
    /// つまんだ辺を動かす。
    /// もう一方の辺を追い越したら前後を入れ替える（裏返したドラッグにも付いていく）。
    /// </summary>
    public BandSelection MoveEdge(BandGrip grip, double value, int total)
        => grip switch
        {
            BandGrip.Start => FromDrag(value, End, total),
            BandGrip.End => FromDrag(Start, value, total),
            _ => this,
        };

    /// <summary>太さを保ったまま、始まりがその位置へ来るように動かす（画像の外へは出さない）。</summary>
    public BandSelection MoveTo(double start, int total)
    {
        var limit = Math.Max(0, total);
        var thickness = Math.Min(Thickness, limit);
        var moved = Math.Clamp(start, 0, limit - thickness);
        return new BandSelection(moved, moved + thickness);
    }

    /// <summary>帯ごとずらす。</summary>
    public BandSelection Shift(double delta, int total) => MoveTo(Start + delta, total);

    /// <summary>終わりの辺だけを動かして太さを変える（始まりより手前には来ない）。</summary>
    public BandSelection Resize(double delta, int total)
        => new(Start, Clamp(Math.Max(Start, End + delta), total));

    private static double Clamp(double value, int total) => Math.Clamp(value, 0, Math.Max(0, total));
}
