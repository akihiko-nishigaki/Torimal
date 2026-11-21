using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Torimaru.Core;

namespace Torimaru.App.Interop;

/// <summary>登録を試みたホットキーと、その結果。</summary>
public sealed record HotKeyRegistration(string Name, string DisplayKeys, bool Succeeded);

/// <summary>
/// グローバルホットキーを扱う。PrintScreen 単体と Win+PrintScreen は
/// Snipping Tool / OS の動作に先回りするため、低レベルキーボードフックで受け取る。
/// </summary>
public sealed class HotKeyManager : IDisposable
{
    /// <summary>空きを調べるときだけ使う ID。実際の登録とは別のウィンドウに付くので衝突しない。</summary>
    private const int ProbeId = 0x3FFF;

    private static readonly NativeMethods.LowLevelKeyboardProc ProbeHookProcedure = ProbeHook;

    private readonly HwndSource _source;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<int, Action> _handlers = [];
    private readonly List<HotKeyRegistration> _registrations = [];
    private readonly HashSet<uint> _pressedOtherModifiers = [];
    private NativeMethods.LowLevelKeyboardProc? _printScreenHookProcedure;
    private IntPtr _printScreenHook;
    private Action? _printScreenHandler;
    private Action? _winPrintScreenHandler;
    private bool _leftWinDown;
    private bool _rightWinDown;
    private bool _suppressPrintScreen;
    private int _nextId = 1;
    private bool _disposed;

    public HotKeyManager()
    {
        var parameters = new HwndSourceParameters("TorimaruHotKeyWindow")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    public IReadOnlyList<HotKeyRegistration> Registrations => _registrations;

    /// <summary>
    /// ホットキーを登録する。候補を順に試し、最初に成功したものを使う。
    /// </summary>
    /// <param name="name">用途名（ユーザーへの表示用）。</param>
    /// <param name="handler">押されたときの処理。</param>
    /// <param name="candidates">試すキーの候補（先頭が最優先）。</param>
    public HotKeyRegistration Register(
        string name, Action handler, IReadOnlyList<HotKeySpec> candidates)
    {
        foreach (var spec in candidates)
        {
            if (!spec.IsAssigned)
            {
                continue;
            }

            if (IsPrintScreenHookSpec(spec))
            {
                if (!RegisterPrintScreenHook(spec, handler))
                {
                    continue;
                }

                var registration = new HotKeyRegistration(name, spec.ToString(), true);
                _registrations.Add(registration);
                return registration;
            }

            var id = _nextId;
            if (!NativeMethods.RegisterHotKey(
                    _source.Handle,
                    id,
                    (uint)spec.Modifiers | NativeMethods.MOD_NOREPEAT,
                    (uint)spec.VirtualKey))
            {
                continue;
            }

            _nextId++;
            _handlers[id] = handler;

            var hotKeyRegistration = new HotKeyRegistration(name, spec.ToString(), true);
            _registrations.Add(hotKeyRegistration);
            return hotKeyRegistration;
        }

        var failed = new HotKeyRegistration(
            name, candidates.Count > 0 ? candidates[0].ToString() : "-", false);

        _registrations.Add(failed);
        return failed;
    }

    /// <summary>
    /// 登録済みのホットキーをすべて解除する（<see cref="Register"/> で付け直せる）。
    /// 設定画面で記録・空き確認を行う間は、自分のフックも外しておく。
    /// </summary>
    public void UnregisterAll()
    {
        foreach (var id in _handlers.Keys)
        {
            NativeMethods.UnregisterHotKey(_source.Handle, id);
        }

        _handlers.Clear();

        if (_printScreenHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_printScreenHook);
            _printScreenHook = IntPtr.Zero;
        }

        _printScreenHookProcedure = null;
        _printScreenHandler = null;
        _winPrintScreenHandler = null;
        _pressedOtherModifiers.Clear();
        _leftWinDown = false;
        _rightWinDown = false;
        _suppressPrintScreen = false;
        _registrations.Clear();
        _nextId = 1;
    }

    /// <summary>そのキーを登録できるか調べる。</summary>
    public static bool IsAvailable(HotKeySpec spec)
    {
        if (!spec.IsAssigned)
        {
            return false;
        }

        if (IsPrintScreenHookSpec(spec))
        {
            var hook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_KEYBOARD_LL,
                ProbeHookProcedure,
                IntPtr.Zero,
                0);

            return hook != IntPtr.Zero && NativeMethods.UnhookWindowsHookEx(hook);
        }

        if (!NativeMethods.RegisterHotKey(
                IntPtr.Zero,
                ProbeId,
                (uint)spec.Modifiers | NativeMethods.MOD_NOREPEAT,
                (uint)spec.VirtualKey))
        {
            return false;
        }

        NativeMethods.UnregisterHotKey(IntPtr.Zero, ProbeId);
        return true;
    }

    private static bool IsPrintScreenHookSpec(HotKeySpec spec)
        => spec.VirtualKey == (int)NativeMethods.VK_SNAPSHOT
            && spec.Modifiers is HotKeyModifiers.None or HotKeyModifiers.Win;

    private bool RegisterPrintScreenHook(HotKeySpec spec, Action handler)
    {
        if (_printScreenHook == IntPtr.Zero)
        {
            _printScreenHookProcedure = LowLevelKeyboardHook;
            _printScreenHook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_KEYBOARD_LL,
                _printScreenHookProcedure,
                IntPtr.Zero,
                0);

            if (_printScreenHook == IntPtr.Zero)
            {
                _printScreenHookProcedure = null;
                return false;
            }
        }

        if (spec.Modifiers == HotKeyModifiers.Win)
        {
            _winPrintScreenHandler = handler;
        }
        else
        {
            _printScreenHandler = handler;
        }

        return true;
    }

    private IntPtr LowLevelKeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0 || (_printScreenHandler is null && _winPrintScreenHandler is null))
        {
            return NativeMethods.CallNextHookEx(_printScreenHook, code, wParam, lParam);
        }

        var message = wParam.ToInt32();
        var key = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam).vkCode;
        var isDown = message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
        var isUp = message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;

        if (IsOtherModifier(key))
        {
            if (isDown)
            {
                _pressedOtherModifiers.Add(key);
            }
            else if (isUp)
            {
                _pressedOtherModifiers.Remove(key);
            }
        }

        if (key == NativeMethods.VK_LWIN)
        {
            _leftWinDown = isDown;
        }
        else if (key == NativeMethods.VK_RWIN)
        {
            _rightWinDown = isDown;
        }

        if (key == NativeMethods.VK_SNAPSHOT)
        {
            if (isUp && _suppressPrintScreen)
            {
                _suppressPrintScreen = false;
                return new IntPtr(1);
            }

            if ((isDown || isUp) && (_leftWinDown || _rightWinDown))
            {
                if (_pressedOtherModifiers.Count == 0)
                {
                    if (!_suppressPrintScreen && _winPrintScreenHandler is { } winHandler)
                    {
                        _suppressPrintScreen = true;
                        _dispatcher.BeginInvoke(new Action(() => InvokeHandler(winHandler)));
                    }

                    if (_suppressPrintScreen)
                    {
                        if (isUp)
                        {
                            _suppressPrintScreen = false;
                        }

                        // Windows 側のスクリーンショット動作に渡さず、Torimaru で処理する。
                        return new IntPtr(1);
                    }
                }
            }
            else if ((isDown || isUp) && _pressedOtherModifiers.Count == 0)
            {
                if (!_suppressPrintScreen && _printScreenHandler is { } printScreenHandler)
                {
                    _suppressPrintScreen = true;
                    _dispatcher.BeginInvoke(new Action(() => InvokeHandler(printScreenHandler)));
                }

                if (_suppressPrintScreen)
                {
                    if (isUp)
                    {
                        _suppressPrintScreen = false;
                    }

                    // Windows 側の Snipping Tool に渡さず、範囲キャプチャで処理する。
                    return new IntPtr(1);
                }
            }
        }

        return NativeMethods.CallNextHookEx(_printScreenHook, code, wParam, lParam);
    }

    private static bool IsOtherModifier(uint key)
        => key is NativeMethods.VK_SHIFT or NativeMethods.VK_LSHIFT or NativeMethods.VK_RSHIFT
            or NativeMethods.VK_CONTROL or NativeMethods.VK_LCONTROL or NativeMethods.VK_RCONTROL
            or NativeMethods.VK_MENU or NativeMethods.VK_LMENU or NativeMethods.VK_RMENU;

    private static IntPtr ProbeHook(int code, IntPtr wParam, IntPtr lParam)
        => NativeMethods.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);

    private static void InvokeHandler(Action? handler)
    {
        if (handler is null)
        {
            return;
        }

        try
        {
            handler();
        }
        catch (Exception ex)
        {
            AppLog.Error("PrintScreen ホットキー処理で例外", ex);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_HOTKEY)
        {
            return IntPtr.Zero;
        }

        if (_handlers.TryGetValue(wParam.ToInt32(), out var handler))
        {
            handled = true;

            // ホットキー処理中に例外が出てもメッセージループを壊さない
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                AppLog.Error("ホットキー処理で例外", ex);
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
