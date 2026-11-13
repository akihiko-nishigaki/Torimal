namespace Torimaru.Core;

/// <summary>
/// 吹き出し本体の形。
/// 値が入っていない（旧バージョンで保存した）注釈は既定の角丸として扱う。
/// </summary>
public static class CalloutShapes
{
    public const string Round = "Round";
    public const string Rect = "Rect";
    public const string Oval = "Oval";

    /// <summary>四角い枠の中に文字を入れるだけ（しっぽを出さない）。</summary>
    public const string Frame = "Frame";

    public static readonly string[] All = [Round, Rect, Oval, Frame];

    /// <summary>未設定・未知の値を既定（角丸）へ寄せる。</summary>
    public static string Normalize(string? value)
        => value is not null && Array.IndexOf(All, value) >= 0 ? value : Round;

    /// <summary>
    /// しっぽを出す形か。四角枠は出さない
    /// （先端の座標は持ち続けるので、形を戻せば元の向きのしっぽが戻る）。
    /// </summary>
    public static bool HasTail(string? value) => Normalize(value) != Frame;
}

/// <summary>
/// 吹き出しの地と文字の色づかい。
/// 値が入っていない（旧バージョンで保存した）注釈は既定の「色で塗り」として扱う。
/// </summary>
public static class CalloutStyles
{
    /// <summary>選んだ色で塗り、白い縁と白い文字。</summary>
    public const string Solid = "Solid";

    /// <summary>白地に、選んだ色の枠と文字。</summary>
    public const string Outline = "Outline";

    /// <summary>黒地に、選んだ色の枠と白い文字。</summary>
    public const string Dark = "Dark";

    public static readonly string[] All = [Solid, Outline, Dark];

    /// <summary>未設定・未知の値を既定（色で塗り）へ寄せる。</summary>
    public static string Normalize(string? value)
        => value is not null && Array.IndexOf(All, value) >= 0 ? value : Solid;
}
