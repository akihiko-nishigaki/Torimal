using System.Windows.Interop;

namespace Torimaru.App.Interop;

/// <summary>登録を試みたホットキーと、その結果。</summary>
public sealed record HotKeyRegistration(string Name, string DisplayKeys, bool Succeeded);

/// <summary>
/// グローバルホットキーを扱う。メッセージ専用ウィンドウを作って WM_HOTKEY を受ける。
/// PrintScreen は Windows 11 の「Snipping Tool を開く」設定に奪われることがあるため、
/// 失敗時は代替キーへフォールバックし、どのキーが有効かを呼び出し側へ返す。
/// </summary>
public sealed class HotKeyManager : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _handlers = [];
    private readonly List<HotKeyRegistration> _registrations = [];
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
    }

    public IReadOnlyList<HotKeyRegistration> Registrations => _registrations;

    /// <summary>
    /// ホットキーを登録する。候補を順に試し、最初に成功したものを使う。
    /// </summary>
    /// <param name="name">用途名（ユーザーへの表示用）。</param>
    /// <param name="handler">押されたときの処理。</param>
    /// <param name="candidates">(修飾キー, 仮想キー, 表示名) の候補列。</param>
    public HotKeyRegistration Register(
        string name,
        Action handler,
        params (uint Modifiers, uint Key, string Display)[] candidates)
    {
        foreach (var (modifiers, key, display) in candidates)
        {
            var id = _nextId;
            if (!NativeMethods.RegisterHotKey(_source.Handle, id, modifiers | NativeMethods.MOD_NOREPEAT, key))
            {
                continue;
            }

            _nextId++;
            _handlers[id] = handler;

            var registration = new HotKeyRegistration(name, display, true);
            _registrations.Add(registration);
            return registration;
        }

        var failed = new HotKeyRegistration(name, candidates.Length > 0 ? candidates[0].Display : "-", false);
        _registrations.Add(failed);
        return failed;
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

        foreach (var id in _handlers.Keys)
        {
            NativeMethods.UnregisterHotKey(_source.Handle, id);
        }

        _handlers.Clear();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
