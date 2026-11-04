using System.Windows.Interop;
using Torimaru.Core;

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
    /// <summary>空きを調べるときだけ使う ID。実際の登録とは別のウィンドウに付くので衝突しない。</summary>
    private const int ProbeId = 0x3FFF;

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

            var registration = new HotKeyRegistration(name, spec.ToString(), true);
            _registrations.Add(registration);
            return registration;
        }

        var failed = new HotKeyRegistration(
            name, candidates.Count > 0 ? candidates[0].ToString() : "-", false);

        _registrations.Add(failed);
        return failed;
    }

    /// <summary>
    /// 登録済みのホットキーをすべて解除する（<see cref="Register"/> で付け直せる）。
    /// ショートカットの設定画面を開いている間は、押したキーが即キャプチャを始めてしまうので
    /// いったん全部外す。空きを調べるときに自分自身が邪魔になるのも防げる。
    /// </summary>
    public void UnregisterAll()
    {
        foreach (var id in _handlers.Keys)
        {
            NativeMethods.UnregisterHotKey(_source.Handle, id);
        }

        _handlers.Clear();
        _registrations.Clear();
        _nextId = 1;
    }

    /// <summary>
    /// そのキーが他のアプリに取られていないか試す。
    /// 実際に登録してすぐ解除するので、OS の判定そのままの結果になる。
    ///
    /// hWnd に NULL を渡すとホットキーは呼び出しスレッドに紐づき、
    /// <see cref="_source"/> に付けた本番の登録とは ID 空間が別になる。
    /// </summary>
    public static bool IsAvailable(HotKeySpec spec)
    {
        if (!spec.IsAssigned)
        {
            return false;
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
