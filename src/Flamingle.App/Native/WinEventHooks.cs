namespace Flamingle.App.Native;

/// <summary>
/// Out-of-context WinEvent hooks for the top-level window changes Flamingle reacts to. Must be started on the
/// Avalonia UI thread: out-of-context hooks are delivered through the installing thread's message loop, so
/// every event below fires on the UI thread. Flamingle's own process is skipped.
/// </summary>
internal sealed class WinEventHooks : IDisposable
{
    // Held so the unmanaged callback stays alive as long as the hooks do.
    private readonly Win32.WinEventProc _proc;
    private readonly List<IntPtr> _hooks = [];

    public WinEventHooks() => _proc = OnEvent;

    public event Action<IntPtr>? ForegroundChanged;
    public event Action<IntPtr>? MinimiseStarted;
    public event Action<IntPtr>? MinimiseEnded;
    public event Action<IntPtr>? Destroyed;
    public event Action<IntPtr>? MoveSizeStarted;
    public event Action<IntPtr>? MoveSizeEnded;

    /// <summary>A window was cloaked or uncloaked — which is what happens when it moves to another virtual
    /// desktop, or the user switches desktop.</summary>
    public event Action<IntPtr>? CloakChanged;

    public void Start()
    {
        Hook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND);
        Hook(Win32.EVENT_SYSTEM_MOVESIZESTART, Win32.EVENT_SYSTEM_MOVESIZEEND);
        Hook(Win32.EVENT_SYSTEM_MINIMIZESTART, Win32.EVENT_SYSTEM_MINIMIZEEND);
        Hook(Win32.EVENT_OBJECT_DESTROY, Win32.EVENT_OBJECT_DESTROY);
        Hook(Win32.EVENT_OBJECT_CLOAKED, Win32.EVENT_OBJECT_UNCLOAKED);
    }

    private void Hook(uint min, uint max)
    {
        var h = Win32.SetWinEventHook(min, max, IntPtr.Zero, _proc, 0, 0,
            Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);
        if (h != IntPtr.Zero) _hooks.Add(h);
    }

    private void OnEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // Only the window itself — not its caret, scrollbars, child controls, etc.
        if (hwnd == IntPtr.Zero || idObject != Win32.OBJID_WINDOW || idChild != Win32.CHILDID_SELF) return;

        switch (eventType)
        {
            case Win32.EVENT_SYSTEM_FOREGROUND: ForegroundChanged?.Invoke(hwnd); break;
            case Win32.EVENT_SYSTEM_MINIMIZESTART: MinimiseStarted?.Invoke(hwnd); break;
            case Win32.EVENT_SYSTEM_MINIMIZEEND: MinimiseEnded?.Invoke(hwnd); break;
            case Win32.EVENT_OBJECT_DESTROY: Destroyed?.Invoke(hwnd); break;
            case Win32.EVENT_SYSTEM_MOVESIZESTART: MoveSizeStarted?.Invoke(hwnd); break;
            case Win32.EVENT_SYSTEM_MOVESIZEEND: MoveSizeEnded?.Invoke(hwnd); break;
            case Win32.EVENT_OBJECT_CLOAKED:
            case Win32.EVENT_OBJECT_UNCLOAKED: CloakChanged?.Invoke(hwnd); break;
        }
    }

    public void Dispose()
    {
        foreach (var h in _hooks) Win32.UnhookWinEvent(h);
        _hooks.Clear();
        GC.KeepAlive(_proc);
    }
}
