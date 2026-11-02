using System.Text.Json.Serialization;

namespace Torimaru.Core;

/// <summary>
/// 1 個の注釈図形。座標は常に「元画像のピクセル座標」で保持する
/// （表示倍率・DPI に依存させないため）。
/// </summary>
public sealed class AnnotationShape
{
    public string Kind { get; set; } = ShapeKind.Rectangle;

    /// <summary>矢印の場合は始点、それ以外は左上。</summary>
    public double X { get; set; }

    public double Y { get; set; }

    /// <summary>矢印の場合は終点までの差分。負値も許容する。</summary>
    public double Width { get; set; }

    public double Height { get; set; }

    /// <summary>#AARRGGBB 形式。</summary>
    public string Color { get; set; } = "#FFE53935";

    public double Thickness { get; set; } = 3;

    public string? Text { get; set; }

    public double FontSize { get; set; } = 24;

    /// <summary>
    /// 書体名。null は既定（Yu Gothic UI）。
    /// 旧バージョンで保存した注釈をそのまま再現するため省略可にしている。
    /// </summary>
    public string? FontFamily { get; set; }

    /// <summary>ステップツールの連番。</summary>
    public int Number { get; set; }

    public double BlurRadius { get; set; } = 14;

    public bool Filled { get; set; }

    /// <summary>吹き出しの先端（しっぽの先）の X。吹き出し以外では使わない。</summary>
    public double TailX { get; set; }

    /// <summary>吹き出しの先端（しっぽの先）の Y。吹き出し以外では使わない。</summary>
    public double TailY { get; set; }

    /// <summary>
    /// 吹き出し本体の形（<see cref="CalloutShapes"/>）。
    /// null は既定（角丸）。旧バージョンで保存した注釈を読めるよう省略可にしている。
    /// </summary>
    public string? CalloutShape { get; set; }

    /// <summary>
    /// 吹き出しの地と文字の色づかい（<see cref="CalloutStyles"/>）。null は既定（色で塗り）。
    /// </summary>
    public string? CalloutStyle { get; set; }

    [JsonIgnore]
    public PixelRect Bounds => new(X, Y, Width, Height);

    /// <summary>当たり判定用の矩形。細い図形でも掴めるよう最低サイズを確保する。</summary>
    [JsonIgnore]
    public PixelRect HitBounds
    {
        get
        {
            var n = Bounds.Normalized();
            const double min = 10;
            var w = Math.Max(n.Width, min);
            var h = Math.Max(n.Height, min);
            return new PixelRect(n.X - (w - n.Width) / 2, n.Y - (h - n.Height) / 2, w, h);
        }
    }

    public AnnotationShape Clone() => (AnnotationShape)MemberwiseClone();

    /// <summary>図形を平行移動する。吹き出しのしっぽも一緒に動く。</summary>
    public void Offset(double dx, double dy)
    {
        X += dx;
        Y += dy;
        TailX += dx;
        TailY += dy;
    }

    /// <summary>
    /// 吹き出しのしっぽを既定位置（本体のやや左下から下向き）に置く。
    /// 本体の大きさが決まったあとに呼ぶ。
    /// </summary>
    public void ResetTail()
    {
        var n = Bounds.Normalized();
        TailX = n.X + (n.Width * 0.22);
        TailY = n.Y + n.Height + Math.Max(26, FontSize * 1.6);
    }
}
