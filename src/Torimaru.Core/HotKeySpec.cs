namespace Torimaru.Core;

/// <summary>
/// ホットキーの修飾キー。値は Win32 の MOD_ALT / MOD_CONTROL / MOD_SHIFT / MOD_WIN と同じ。
/// RegisterHotKey にそのまま渡せるようにしてある。
/// </summary>
[Flags]
public enum HotKeyModifiers
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
}

/// <summary>
/// ホットキー 1 つ分の割り当て（修飾キー + 1 つの通常キー）。
///
/// 設定ファイルには "Ctrl+Shift+S" のような文字列で保存するので、
/// 文字列と仮想キーコードの相互変換をここに集約している。
/// WPF / Win32 に依存しないため、そのままテストできる。
/// </summary>
public readonly record struct HotKeySpec(HotKeyModifiers Modifiers, int VirtualKey)
{
    /// <summary>修飾キーの表示順。Windows の慣習に合わせる。</summary>
    private static readonly (HotKeyModifiers Flag, string Name)[] ModifierNames =
    [
        (HotKeyModifiers.Control, "Ctrl"),
        (HotKeyModifiers.Shift, "Shift"),
        (HotKeyModifiers.Alt, "Alt"),
        (HotKeyModifiers.Win, "Win"),
    ];

    /// <summary>修飾キーの別名（入力のゆらぎを吸収する）。</summary>
    private static readonly Dictionary<string, HotKeyModifiers> ModifierAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ctrl"] = HotKeyModifiers.Control,
            ["control"] = HotKeyModifiers.Control,
            ["shift"] = HotKeyModifiers.Shift,
            ["alt"] = HotKeyModifiers.Alt,
            ["menu"] = HotKeyModifiers.Alt,
            ["win"] = HotKeyModifiers.Win,
            ["windows"] = HotKeyModifiers.Win,
            ["meta"] = HotKeyModifiers.Win,
        };

    /// <summary>
    /// 名前の付いた通常キー。表示にはこの名前を使う。
    /// ここに無い仮想キーは "Key123" 形式で表す（配列依存の記号キーに
    /// 勝手な名前を付けて誤解を生むより、素の番号で残すほうが安全）。
    /// </summary>
    private static readonly (int Vk, string Name)[] NamedKeys = BuildNamedKeys();

    private static readonly Dictionary<string, int> KeyAliases = BuildKeyAliases();

    /// <summary>
    /// 修飾キー無しでも登録を許すキー。
    /// 文字キーを単独で奪うと、どのアプリでも文字が打てなくなるため許さない。
    /// </summary>
    private static bool IsStandaloneAllowed(int vk)
        => vk is VkSnapshot or VkPause or VkScroll
            || (vk >= VkF13 && vk <= VkF24);

    public const int VkBack = 0x08;
    public const int VkTab = 0x09;
    public const int VkReturn = 0x0D;
    public const int VkPause = 0x13;
    public const int VkEscape = 0x1B;
    public const int VkSpace = 0x20;
    public const int VkSnapshot = 0x2C;
    public const int VkF1 = 0x70;
    public const int VkF13 = 0x7C;
    public const int VkF24 = 0x87;
    public const int VkScroll = 0x91;

    /// <summary>キーが決まっているか（既定に戻した状態は VirtualKey = 0）。</summary>
    public bool IsAssigned => VirtualKey != 0;

    /// <summary>
    /// "Ctrl+Shift+S" のような文字列を解釈する。
    /// 解釈できない場合は false を返す（例外は投げない）。
    /// </summary>
    public static bool TryParse(string? text, out HotKeySpec spec)
    {
        spec = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var modifiers = HotKeyModifiers.None;
        var key = 0;

        foreach (var rawPart in text.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = rawPart.Trim();
            if (part.Length == 0)
            {
                continue;
            }

            if (ModifierAliases.TryGetValue(part, out var modifier))
            {
                modifiers |= modifier;
                continue;
            }

            // 通常キーは 1 つだけ。2 つ以上あれば書き間違いとして拒否する。
            if (key != 0 || !TryParseKeyName(part, out key))
            {
                return false;
            }
        }

        if (key == 0)
        {
            return false;
        }

        spec = new HotKeySpec(modifiers, key);
        return true;
    }

    /// <summary>設定ファイルや画面に出す文字列。<see cref="TryParse"/> で元に戻せる。</summary>
    public override string ToString()
    {
        if (!IsAssigned)
        {
            return string.Empty;
        }

        var parts = new List<string>(5);
        foreach (var (flag, name) in ModifierNames)
        {
            if ((Modifiers & flag) != 0)
            {
                parts.Add(name);
            }
        }

        parts.Add(KeyName(VirtualKey));
        return string.Join("+", parts);
    }

    /// <summary>
    /// ホットキーとして無理のない組み合わせか調べる。
    /// 問題が無ければ null、あれば利用者向けの理由を返す。
    /// </summary>
    public string? Validate()
    {
        if (!IsAssigned)
        {
            return "キーが指定されていません。";
        }

        if (IsModifierKey(VirtualKey))
        {
            return "Ctrl や Shift だけでは登録できません。文字キーなどと組み合わせてください。";
        }

        if (Modifiers == HotKeyModifiers.None && !IsStandaloneAllowed(VirtualKey))
        {
            return "そのキーは単独では使えません（他のアプリで文字が打てなくなります）。"
                + "Ctrl / Shift / Alt / Win のいずれかと組み合わせてください。";
        }

        return null;
    }

    /// <summary>その仮想キー自体が修飾キーか。</summary>
    public static bool IsModifierKey(int vk)
        => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C
            or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    /// <summary>仮想キーの表示名。</summary>
    public static string KeyName(int vk)
    {
        foreach (var (candidate, name) in NamedKeys)
        {
            if (candidate == vk)
            {
                return name;
            }
        }

        return "Key" + vk.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>表示名から仮想キーを引く。</summary>
    public static bool TryParseKeyName(string name, out int vk)
    {
        if (KeyAliases.TryGetValue(name, out vk))
        {
            return true;
        }

        // 名前の無いキーは "Key123" として扱う
        if (name.Length > 3
            && name.StartsWith("Key", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(
                name.AsSpan(3),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var number)
            && number is > 0 and <= 255)
        {
            vk = number;
            return true;
        }

        vk = 0;
        return false;
    }

    private static (int Vk, string Name)[] BuildNamedKeys()
    {
        var keys = new List<(int, string)>();

        for (var c = 'A'; c <= 'Z'; c++)
        {
            keys.Add((c, c.ToString()));
        }

        for (var d = 0; d <= 9; d++)
        {
            keys.Add((0x30 + d, d.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        for (var f = 1; f <= 24; f++)
        {
            keys.Add((
                VkF1 + f - 1,
                "F" + f.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        for (var n = 0; n <= 9; n++)
        {
            keys.Add((
                0x60 + n,
                "Num" + n.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        keys.AddRange(
        [
            (VkBack, "Backspace"),
            (VkTab, "Tab"),
            (VkReturn, "Enter"),
            (VkPause, "Pause"),
            (VkEscape, "Escape"),
            (VkSpace, "Space"),
            (0x21, "PageUp"),
            (0x22, "PageDown"),
            (0x23, "End"),
            (0x24, "Home"),
            (0x25, "Left"),
            (0x26, "Up"),
            (0x27, "Right"),
            (0x28, "Down"),
            (VkSnapshot, "PrintScreen"),
            (0x2D, "Insert"),
            (0x2E, "Delete"),
            (0x6A, "Num*"),
            (0x6B, "Num+"),
            (0x6D, "Num-"),
            (0x6E, "Num."),
            (0x6F, "Num/"),
            (VkScroll, "ScrollLock"),
            (0xBB, "Plus"),
            (0xBC, "Comma"),
            (0xBD, "Minus"),
            (0xBE, "Period"),
        ]);

        return [.. keys];
    }

    private static Dictionary<string, int> BuildKeyAliases()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var (vk, name) in NamedKeys)
        {
            map[name] = vk;
        }

        // 省略表記・別名も受け付ける（手で設定ファイルを書く人のため）
        map["esc"] = VkEscape;
        map["return"] = VkReturn;
        map["del"] = 0x2E;
        map["ins"] = 0x2D;
        map["pgup"] = 0x21;
        map["pgdn"] = 0x22;
        map["pgdown"] = 0x22;
        map["prtsc"] = VkSnapshot;
        map["prtscn"] = VkSnapshot;
        map["printscrn"] = VkSnapshot;
        map["snapshot"] = VkSnapshot;
        // "+" 自体は区切り文字として使うので別名に入れない（"Plus" と書く）
        map["-"] = 0xBD;
        map[","] = 0xBC;
        map["."] = 0xBE;

        return map;
    }
}
