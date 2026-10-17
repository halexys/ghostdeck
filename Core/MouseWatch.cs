using System.Runtime.InteropServices;

namespace GhostDeck;

/// <summary>
/// Reports the left and right mouse buttons system-wide for the crosshair's click effect and
/// its "hide while aiming" option: a low-level mouse hook (WH_MOUSE_LL) on its own message-loop
/// thread, exactly like <see cref="WinKeyLock"/> for the keyboard and <see cref="TrayWheel"/>
/// for the wheel. The hook only observes - every event is passed on untouched - and it is
/// installed only while a crosshair option needs it (<see cref="CrosshairStyle.NeedsMouseHook"/>)
/// and the crosshair is on. The callback does nothing but post the button to the UI thread;
/// Windows drops a low-level hook that is slow.
/// </summary>
public sealed class MouseWatch : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const uint WM_QUIT = 0x0012;
    private const uint WM_APP_HOOK = 0x8002;      // WPARAM: 1 = install hook, 0 = remove it
    private const int WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr HWnd; public uint Message; public IntPtr WParam; public IntPtr LParam; public uint Time; public POINT Pt; }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);

    public enum Button { LeftDown, RightDown, RightUp }

    private readonly HookProc _proc;                       // keeps the delegate alive for the native hook
    private readonly SynchronizationContext _ui;
    private readonly Action<Button> _onButton;             // runs on the UI thread
    private readonly ManualResetEventSlim _ready = new();
    private IntPtr _hook;                                  // touched only on the hook thread
    private uint _threadId;
    private volatile bool _on;

    public MouseWatch(SynchronizationContext ui, Action<Button> onButton)
    {
        _ui = ui;
        _onButton = onButton;
        _proc = Callback;
    }

    public bool Enabled => _on;

    public void Set(bool on)
    {
        if (on == _on) return;
        _on = on;
        EnsureThread();
        if (_threadId != 0) PostThreadMessageW(_threadId, WM_APP_HOOK, on ? 1 : IntPtr.Zero, IntPtr.Zero);
    }

    private void EnsureThread()
    {
        if (_threadId != 0) return;
        new Thread(Loop) { IsBackground = true, Name = "GhostDeckMouseWatch", Priority = ThreadPriority.AboveNormal }.Start();
        _ready.Wait(2000);
    }

    private void Loop()
    {
        PeekMessageW(out _, IntPtr.Zero, 0, 0, 0);   // force-create this thread's message queue
        _threadId = GetCurrentThreadId();
        _ready.Set();
        while (GetMessageW(out var m, IntPtr.Zero, 0, 0) > 0)
        {
            if (m.Message != WM_APP_HOOK) continue;
            bool want = m.WParam != IntPtr.Zero;
            if (want && _hook == IntPtr.Zero)
                _hook = SetWindowsHookExW(WH_MOUSE_LL, _proc, GetModuleHandleW(null), 0);
            else if (!want && _hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        _threadId = 0;
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _on)
        {
            Button? b = (int)wParam switch
            {
                WM_LBUTTONDOWN => Button.LeftDown,
                WM_RBUTTONDOWN => Button.RightDown,
                WM_RBUTTONUP => Button.RightUp,
                _ => null,
            };
            if (b is { } btn)
            {
                try { _ui.Post(_ => { if (_on) _onButton(btn); }, null); } catch { }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        _on = false;
        if (_threadId != 0) PostThreadMessageW(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }
}
