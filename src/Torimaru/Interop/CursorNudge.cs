using System.Windows.Input;

namespace Torimaru.App.Interop;

/// <summary>
/// 矢印キーで実際のマウスカーソルを 1 ピクセルずつ（Shift で 10 ピクセル）動かす。
///
/// カーソルそのものを動かすので、マウスのボタンを押したまま（ドラッグ中）でも効き、
/// 範囲の端や図形の角をピクセル単位で合わせられる。移動量は物理ピクセル。
/// </summary>
public static class CursorNudge
{
    public const int Step = 1;
    public const int ShiftStep = 10;

    /// <summary>矢印キーなら動かす方向を返す。それ以外は null。</summary>
    public static (int Dx, int Dy)? Delta(Key key, ModifierKeys modifiers)
    {
        var step = modifiers.HasFlag(ModifierKeys.Shift) ? ShiftStep : Step;

        return key switch
        {
            Key.Left => (-step, 0),
            Key.Right => (step, 0),
            Key.Up => (0, -step),
            Key.Down => (0, step),
            _ => null,
        };
    }

    /// <summary>
    /// 矢印キーならカーソルを動かして true を返す。
    /// <paramref name="clamp"/> を渡すと、その範囲（仮想画面座標・右下は含まない）の外へは出さない。
    /// </summary>
    public static bool TryMove(Key key, (int X, int Y, int Width, int Height)? clamp = null)
    {
        if (Delta(key, Keyboard.Modifiers) is not { } delta)
        {
            return false;
        }

        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return true;
        }

        var x = cursor.X + delta.Dx;
        var y = cursor.Y + delta.Dy;

        if (clamp is { } bounds)
        {
            x = Math.Clamp(x, bounds.X, bounds.X + bounds.Width - 1);
            y = Math.Clamp(y, bounds.Y, bounds.Y + bounds.Height - 1);
        }

        NativeMethods.SetCursorPos(x, y);
        return true;
    }
}
