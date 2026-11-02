using Torimaru.Core;

namespace Torimaru.App.Interop;

/// <summary>画面上のウィンドウ 1 個の情報（座標は仮想画面の物理ピクセル）。</summary>
public sealed record WindowInfo(IntPtr Handle, string Title, PixelRect Bounds)
{
    public double Area => Bounds.Width * Bounds.Height;
}

/// <summary>
/// 「ウィンドウにカーソルを乗せたら、そのウィンドウだけを掴める」ための列挙と当たり判定。
/// オーバーレイを表示する前に呼ぶことで、自分自身が候補に混ざるのを防ぐ。
/// </summary>
public static class WindowFinder
{
    /// <summary>
    /// 画面に見えているトップレベルウィンドウを Z オーダー（手前から奥）順に返す。
    /// </summary>
    public static List<WindowInfo> EnumerateVisibleWindows()
    {
        var result = new List<WindowInfo>();

        // EnumWindows は手前から奥の順に列挙する
        NativeMethods.EnumWindows(
            (hWnd, _) =>
            {
                var info = TryDescribe(hWnd);
                if (info is not null)
                {
                    result.Add(info);
                }

                return true;
            },
            IntPtr.Zero);

        return result;
    }

    private static WindowInfo? TryDescribe(IntPtr hWnd)
    {
        if (!NativeMethods.IsWindowVisible(hWnd) || NativeMethods.IsIconic(hWnd))
        {
            return null;
        }

        // ツールウィンドウ（タスクバーに出ない補助窓）は対象外
        var exStyle = NativeMethods.GetWindowLongSafe(hWnd, NativeMethods.GWL_EXSTYLE);
        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0)
        {
            return null;
        }

        // DWM が「隠している」ウィンドウ（別の仮想デスクトップ、UWP のサスペンド等）は対象外
        if (NativeMethods.DwmGetWindowAttribute(
                hWnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0
            && cloaked != 0)
        {
            return null;
        }

        var bounds = GetBounds(hWnd);
        if (bounds is null || bounds.Value.Width < 8 || bounds.Value.Height < 8)
        {
            return null;
        }

        return new WindowInfo(hWnd, GetTitle(hWnd), bounds.Value);
    }

    /// <summary>
    /// ウィンドウの見た目の矩形を返す。GetWindowRect は不可視のリサイズ枠を含むため、
    /// まず DWM の拡張フレーム境界を使う。
    /// </summary>
    private static PixelRect? GetBounds(IntPtr hWnd)
    {
        if (NativeMethods.DwmGetWindowAttribute(
                hWnd,
                NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out NativeMethods.RECT frame,
                System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.RECT>()) == 0
            && frame.Width > 0 && frame.Height > 0)
        {
            return new PixelRect(frame.Left, frame.Top, frame.Width, frame.Height);
        }

        if (NativeMethods.GetWindowRect(hWnd, out var rect) && rect.Width > 0 && rect.Height > 0)
        {
            return new PixelRect(rect.Left, rect.Top, rect.Width, rect.Height);
        }

        return null;
    }

    private static string GetTitle(IntPtr hWnd)
    {
        var length = NativeMethods.GetWindowTextLength(hWnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new char[length + 1];
        var copied = NativeMethods.GetWindowText(hWnd, buffer, buffer.Length);
        return copied > 0 ? new string(buffer, 0, copied) : string.Empty;
    }

    /// <summary>
    /// 指定座標にある最も手前のウィンドウを返す。
    /// 同じ位置に複数ある場合、Z オーダーが手前のものが優先される。
    /// </summary>
    public static WindowInfo? HitTest(IReadOnlyList<WindowInfo> windows, double x, double y)
    {
        foreach (var window in windows)
        {
            if (window.Bounds.Contains(x, y))
            {
                return window;
            }
        }

        return null;
    }
}
