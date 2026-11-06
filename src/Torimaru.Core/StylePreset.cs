namespace Torimaru.Core;

/// <summary>
/// 「よく使う描き方」をひとまとめにしたもの。
///
/// 枠を 1 本引くたびにツール・色・太さを選び直すのが手間なので、
/// その組み合わせに名前を付けて取り出せるようにする。
/// 設定ファイル（<see cref="AppSettings.StylePresets"/>）に保存され、全キャプチャで共通。
/// </summary>
public sealed class StylePreset
{
    /// <summary>画面に出す名前。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>どのツールか（<see cref="ShapeKind"/>）。画像を切るツールは対象外。</summary>
    public string Tool { get; set; } = ShapeKind.Rectangle;

    /// <summary>#AARRGGBB 形式。</summary>
    public string Color { get; set; } = "#FFE53935";

    public double Thickness { get; set; } = 3;

    /// <summary>文字・吹き出しの書体。null は「既定のまま」。</summary>
    public string? FontFamily { get; set; }

    public double FontSize { get; set; } = 24;

    /// <summary>吹き出し本体の形（<see cref="CalloutShapes"/>）。null は既定。</summary>
    public string? CalloutShape { get; set; }

    /// <summary>吹き出しの地色（<see cref="CalloutStyles"/>）。null は既定。</summary>
    public string? CalloutStyle { get; set; }

    /// <summary>この順番までならキー（Ctrl+1〜9）で呼べる。</summary>
    public const int ShortcutCount = 9;

    /// <summary>設定ファイルに持てる上限。増やしすぎても並べきれない。</summary>
    public const int MaxCount = 20;

    public StylePreset Clone() => (StylePreset)MemberwiseClone();

    /// <summary>プリセットとして使える内容か。</summary>
    public bool IsUsable
        => !string.IsNullOrWhiteSpace(Name) && ShapeKind.IsKnown(Tool);

    /// <summary>文字の設定が意味を持つツールか。</summary>
    public bool IsTextual => Tool is ShapeKind.Text or ShapeKind.Callout;

    /// <summary>線の太さが意味を持つツールか。</summary>
    public bool HasThickness => Tool is not (ShapeKind.Text or ShapeKind.Callout);

    /// <summary>
    /// 値を使える範囲へ寄せる。
    /// 名前の前後の空白を落とし、太さ・大きさを丸め、知らない吹き出し指定を既定へ戻す。
    /// </summary>
    public StylePreset Sanitized()
    {
        Name = (Name ?? string.Empty).Trim();

        if (Name.Length > 24)
        {
            Name = Name[..24];
        }

        if (!ShapeKind.IsKnown(Tool))
        {
            Tool = ShapeKind.Rectangle;
        }

        if (string.IsNullOrWhiteSpace(Color))
        {
            Color = "#FFE53935";
        }

        // 編集画面のスライダーと同じ範囲にする。
        // ここを広く取ると、呼び出した瞬間にスライダー側で丸められて食い違う
        Thickness = Math.Clamp(Thickness, 1, 20);
        FontSize = Math.Clamp(FontSize, 8, 200);

        if (string.IsNullOrWhiteSpace(FontFamily))
        {
            FontFamily = null;
        }

        // 吹き出し以外では使わない値なので、持たせたままにしない
        if (Tool == ShapeKind.Callout)
        {
            CalloutShape = CalloutShapes.Normalize(CalloutShape);
            CalloutStyle = CalloutStyles.Normalize(CalloutStyle);
        }
        else
        {
            CalloutShape = null;
            CalloutStyle = null;
        }

        return this;
    }

    /// <summary>名前を除いて中身が同じか（いま選んでいる設定と見比べるのに使う）。</summary>
    public bool SameStyleAs(StylePreset other)
    {
        if (!string.Equals(Tool, other.Tool, StringComparison.Ordinal)
            || !string.Equals(Color, other.Color, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 太さや文字はツールによって効かない。効かない値の違いで別物扱いにしない
        if (HasThickness && Math.Abs(Thickness - other.Thickness) > 0.01)
        {
            return false;
        }

        if (IsTextual)
        {
            if (!string.Equals(FontFamily, other.FontFamily, StringComparison.OrdinalIgnoreCase)
                || Math.Abs(FontSize - other.FontSize) > 0.01)
            {
                return false;
            }
        }

        if (Tool == ShapeKind.Callout)
        {
            if (!string.Equals(
                    CalloutShapes.Normalize(CalloutShape),
                    CalloutShapes.Normalize(other.CalloutShape),
                    StringComparison.Ordinal)
                || !string.Equals(
                    CalloutStyles.Normalize(CalloutStyle),
                    CalloutStyles.Normalize(other.CalloutStyle),
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>ツールの表示名。</summary>
    public static string ToolLabel(string? tool) => tool switch
    {
        ShapeKind.Arrow => "矢印",
        ShapeKind.Rectangle => "枠",
        ShapeKind.Ellipse => "楕円",
        ShapeKind.Text => "文字",
        ShapeKind.Callout => "吹き出し",
        ShapeKind.Step => "番号",
        ShapeKind.Highlight => "蛍光",
        ShapeKind.Blur => "ぼかし",
        _ => tool ?? "?",
    };

    /// <summary>中身の説明（ツールチップ用）。効かない項目は出さない。</summary>
    public string Describe()
    {
        var parts = new List<string> { ToolLabel(Tool) };

        if (Tool != ShapeKind.Blur)
        {
            parts.Add(Color);
        }

        if (HasThickness)
        {
            parts.Add(Tool == ShapeKind.Blur ? $"強さ {Thickness:0}" : $"太さ {Thickness:0}");
        }

        if (IsTextual)
        {
            parts.Add($"{FontFamily ?? "既定の書体"} {FontSize:0} px");
        }

        if (Tool == ShapeKind.Callout)
        {
            parts.Add(CalloutShapes.Normalize(CalloutShape) switch
            {
                CalloutShapes.Rect => "四角",
                CalloutShapes.Oval => "楕円",
                _ => "角丸",
            });

            parts.Add(CalloutStyles.Normalize(CalloutStyle) switch
            {
                CalloutStyles.Outline => "白地",
                CalloutStyles.Dark => "黒地",
                _ => "色で塗り",
            });
        }

        return string.Join(" / ", parts);
    }

    /// <summary>
    /// 一度も作っていないときに入れておく初期セット。
    ///
    /// 空のリストは「全部消した」という意思表示なので、そのときは補充しない
    /// （<see cref="AppSettings.StylePresets"/> が null のときだけ使う）。
    /// </summary>
    public static List<StylePreset> Defaults() =>
    [
        new() { Name = "赤枠", Tool = ShapeKind.Rectangle, Color = "#FFE53935", Thickness = 4 },
        new() { Name = "赤矢印", Tool = ShapeKind.Arrow, Color = "#FFE53935", Thickness = 5 },
        new() { Name = "黄マーカー", Tool = ShapeKind.Highlight, Color = "#FFFDD835", Thickness = 16 },
        new() { Name = "ぼかし", Tool = ShapeKind.Blur, Thickness = 6 },
        new()
        {
            Name = "赤吹き出し",
            Tool = ShapeKind.Callout,
            Color = "#FFE53935",
            FontSize = 24,
            CalloutShape = CalloutShapes.Round,
            CalloutStyle = CalloutStyles.Solid,
        },
    ];

    /// <summary>
    /// 一覧を保存できる形へ整える。
    /// 使えない項目を捨て、同じ名前は後から来たほうに「(2)」を付け、上限で打ち切る。
    /// </summary>
    public static List<StylePreset> SanitizeList(IEnumerable<StylePreset?>? presets)
    {
        var result = new List<StylePreset>();
        if (presets is null)
        {
            return result;
        }

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in presets)
        {
            if (candidate is null)
            {
                continue;
            }

            var preset = candidate.Sanitized();
            if (!preset.IsUsable)
            {
                continue;
            }

            // 同名が並ぶと、どれを押したのか分からなくなる
            if (!used.Add(preset.Name))
            {
                var suffix = 2;
                string renamed;
                do
                {
                    renamed = $"{preset.Name}({suffix})";
                    suffix++;
                }
                while (!used.Add(renamed) && suffix < 100);

                preset.Name = renamed;
            }

            result.Add(preset);

            if (result.Count >= MaxCount)
            {
                break;
            }
        }

        return result;
    }
}
