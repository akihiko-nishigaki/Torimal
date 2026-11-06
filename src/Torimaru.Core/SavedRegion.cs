namespace Torimaru.Core;

/// <summary>
/// 覚えておくキャプチャ範囲。
///
/// 座標は「仮想画面の物理ピクセル」で持つ。画像座標で持つと、
/// モニタ構成が変わって仮想画面の原点がずれた途端に別の場所を指してしまう。
/// 整数で持つのは、設定ファイルに書いて読み戻しても同じ 1 ピクセルを指すため。
/// </summary>
public sealed class SavedRegion
{
    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    /// <summary>範囲として成立しているか（0 ピクセルは撮れない）。</summary>
    public bool IsUsable => Width >= 1 && Height >= 1;

    public int Right => X + Width;

    public int Bottom => Y + Height;

    /// <summary>仮想画面座標の矩形から作る。成立しない範囲なら null。</summary>
    public static SavedRegion? From(PixelRect virtualRect)
    {
        var (x, y, w, h) = virtualRect.ToIntegerBounds();
        var region = new SavedRegion { X = x, Y = y, Width = w, Height = h };
        return region.IsUsable ? region : null;
    }

    public PixelRect ToPixelRect() => new(X, Y, Width, Height);

    public SavedRegion Clone() => (SavedRegion)MemberwiseClone();

    /// <summary>人に見せる表記。</summary>
    public string Describe() => $"{Width} x {Height} px（位置 {X}, {Y}）";
}

/// <summary>
/// 覚えた範囲を、いまの画面構成で撮れる形に直す。
/// </summary>
public static class RegionMemory
{
    /// <summary>
    /// 覚えた範囲をそのまま撮れるなら返す。撮れないなら null。
    ///
    /// はみ出した分を切って返すことはしない。「前回と同じ範囲」と言いながら
    /// 黙って違う大きさの画像が出てくるほうが分かりにくいため、
    /// 全部収まっているときだけ採用し、収まらなければ呼び出し側に選び直させる。
    /// </summary>
    /// <param name="saved">覚えた範囲（仮想画面座標）。null なら未記録。</param>
    /// <param name="screenX">いまの仮想画面の左端。</param>
    /// <param name="screenY">いまの仮想画面の上端。</param>
    /// <param name="screenWidth">いまの仮想画面の幅。</param>
    /// <param name="screenHeight">いまの仮想画面の高さ。</param>
    public static PixelRect? Resolve(
        SavedRegion? saved, int screenX, int screenY, int screenWidth, int screenHeight)
    {
        if (saved is null || !saved.IsUsable)
        {
            return null;
        }

        if (screenWidth < 1 || screenHeight < 1)
        {
            return null;
        }

        var fits = saved.X >= screenX
                   && saved.Y >= screenY
                   && saved.Right <= screenX + screenWidth
                   && saved.Bottom <= screenY + screenHeight;

        return fits ? saved.ToPixelRect() : null;
    }

    /// <summary>覚えた範囲が今の画面に収まらない理由の説明（収まる場合は null）。</summary>
    public static string? ExplainMismatch(
        SavedRegion? saved, int screenX, int screenY, int screenWidth, int screenHeight)
    {
        if (saved is null || !saved.IsUsable)
        {
            return "前回の範囲がまだ記録されていません";
        }

        return Resolve(saved, screenX, screenY, screenWidth, screenHeight) is null
            ? $"前回の範囲（{saved.Describe()}）が今の画面に収まりません。モニタ構成が変わった可能性があります"
            : null;
    }
}
