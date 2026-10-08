using System.Runtime.InteropServices;

namespace GhostDeck;

public static class Hk
{
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
}

/// <summary>Globalne skroty przez RegisterHotKey na ukrytym oknie komunikatow.</summary>
public sealed class HotkeyManager : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 1;

    // MSI scenario key (Fn+F7 on the Prestige 16 AI Studio): not a normal key, so RegisterHotKey
    // never sees it. Measured on 15A2EMS1: each press arrives as raw keyboard input from MSI's
    // hotkey device ACPI\MSNB1001 with scan code E0 11 and vk 0xFF (plus an MSI_Event WMI
    // event). Raw input with INPUTSINK reads it without blocking it for anyone else.
    private const int WM_INPUT = 0x00FF;
    private const uint RIDEV_INPUTSINK = 0x100, RID_INPUT = 0x10000003, RIDI_DEVICENAME = 0x20000007;
    private const ushort RI_KEY_BREAK = 1, RI_KEY_E0 = 2;
    [StructLayout(LayoutKind.Sequential)] private struct RAWINPUTDEVICE { public ushort Page, Usage; public uint Flags; public IntPtr Target; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint n, uint size);
    [DllImport("user32.dll")] private static extern uint GetRawInputData(IntPtr h, uint cmd, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint GetRawInputDeviceInfo(IntPtr dev, uint cmd, IntPtr data, ref uint size);
    private readonly Dictionary<IntPtr, bool> _msiKeyDevice = new();

    /// <summary>Called on each press of the MSI scenario key; null = ignore the key.</summary>
    public Action? ScenarioKey { get; set; }

    public HotkeyManager()
    {
        CreateHandle(new CreateParams());
        var rid = new[] { new RAWINPUTDEVICE { Page = 0x01, Usage = 0x06, Flags = RIDEV_INPUTSINK, Target = Handle } };
        RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());   // best effort: failure = no scenario key
    }

    private bool IsMsiHotkeyDevice(IntPtr dev)
    {
        if (_msiKeyDevice.TryGetValue(dev, out var known)) return known;
        uint chars = 0;
        GetRawInputDeviceInfo(dev, RIDI_DEVICENAME, IntPtr.Zero, ref chars);
        var buf = Marshal.AllocHGlobal((int)chars * 2 + 2);
        try
        {
            GetRawInputDeviceInfo(dev, RIDI_DEVICENAME, buf, ref chars);
            var name = Marshal.PtrToStringUni(buf) ?? "";
            return _msiKeyDevice[dev] = name.Contains("MSNB1001", StringComparison.OrdinalIgnoreCase);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private void OnRawInput(IntPtr hRawInput)
    {
        uint header = (uint)(8 + IntPtr.Size * 2), size = 0;
        GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, header);
        if (size == 0) return;
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(hRawInput, RID_INPUT, buf, ref size, header) != size) return;
            if (Marshal.ReadInt32(buf) != 1) return;                       // RIM_TYPEKEYBOARD
            IntPtr dev = Marshal.ReadIntPtr(buf, 8);
            ushort make = (ushort)Marshal.ReadInt16(buf, (int)header);
            ushort flags = (ushort)Marshal.ReadInt16(buf, (int)header + 2);
            if (make == 0x11 && (flags & RI_KEY_E0) != 0 && (flags & RI_KEY_BREAK) == 0 && IsMsiHotkeyDevice(dev))
                ScenarioKey?.Invoke();
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public bool Register(uint mods, uint vk, Action onPressed)
    {
        if (vk == 0) return false;
        int id = _nextId++;
        if (!RegisterHotKey(Handle, id, mods | Hk.MOD_NOREPEAT, vk))
            return false;
        _actions[id] = onPressed;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys.ToList())
            UnregisterHotKey(Handle, id);
        _actions.Clear();
        _nextId = 1;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && _actions.TryGetValue((int)m.WParam, out var act))
            act();
        else if (m.Msg == WM_INPUT && ScenarioKey != null)
            OnRawInput(m.LParam);
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        UnregisterAll();
        DestroyHandle();
    }
}
