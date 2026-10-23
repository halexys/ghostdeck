using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GhostDeck;

public enum GpuMode { Hybrid = 0, Discrete = 1, Integrated = 2 }

public sealed record GpuSwitchState(bool Supported, bool IntegratedSupported, bool DiscreteSupported,
                                    GpuMode Current, GpuMode Requested)
{
    public bool Pending => Requested != Current;
}

/// <summary>
/// MSI's graphics-mode switch (Hybrid / Integrated only), done the way MSI Center does it -
/// reverse-engineered by tim-rue/MsiGT from OmApSvcBroker.exe and API_NB_Base Module.dll, see
/// TECHNICAL.md §84. Byte 5 of the UEFI variable MsiDCVarData holds:
///   bits 0-1  mode for the next boot (0 = Hybrid, 1 = Discrete, 2 = Integrated)
///   bits 2-3  mode the machine booted in
///   bit 4     switch supported      bit 5  Integrated supported      bit 6  Discrete NOT supported
/// A request = those two bits + arming EC 0xD1 (<see cref="Ec.ArmGpuSwitch"/>); the firmware
/// switches during the next boot. Only the two request bits are ever changed.
/// </summary>
public static class GpuSwitch
{
    private const string VarName = "MsiDCVarData";
    private const string VarGuid = "{DD96BAAF-145E-4F56-B1CF-193256298E99}";
    private const int FlagsOffset = 5;

    // With MSI Center installed its service owns the variable: it watches GPUswitchCH and edits
    // the variable itself, so going through it keeps MSI Center's UI in sync. 6 = "no request".
    private const string MsiCenterKey = @"SOFTWARE\WOW6432Node\MSI\MSI Center\Component\Base Module\GeneralSetting";
    private const string MsiCenterValue = "GPUswitchCH";
    private const int MsiCenterIdle = 6;

    /// <summary>The switch state, or null when this machine has no MSI GPU switch (no variable, not admin, not UEFI).</summary>
    public static GpuSwitchState? Read()
    {
        try
        {
            var (data, _) = ReadVariable();
            byte f = data[FlagsOffset];
            var s = new GpuSwitchState((f & 0x10) != 0, (f & 0x20) != 0, (f & 0x40) == 0,
                                       (GpuMode)((f >> 2) & 3), (GpuMode)(f & 3));
            return s.Supported ? s : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Requests <paramref name="mode"/> for the next boot. Going back to the mode the machine
    /// booted in cancels a pending switch. If arming fails the stored request is reset to the
    /// current mode, so a half-done switch is never left behind. Throws with a readable message.
    /// </summary>
    public static void Request(GpuMode mode)
    {
        Store(mode);
        var current = Read()?.Current ?? throw new InvalidOperationException("The GPU switch state can't be read.");
        if (current == mode) return;
        try { Ec.ArmGpuSwitch(); }
        catch (Exception arm)
        {
            try { Store(current); }
            catch (Exception rb) { throw new InvalidOperationException($"{arm.Message} Resetting the request also failed: {rb.Message}", arm); }
            throw new InvalidOperationException($"{arm.Message} Nothing changes on the next restart.", arm);
        }
    }

    /// <summary>Restarts Windows now; false = shutdown.exe refused (the switch still happens on a manual restart).</summary>
    public static bool RestartNow()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { CreateNoWindow = true, UseShellExecute = false });
            p?.WaitForExit(5000);
            return p is { HasExited: true, ExitCode: 0 };
        }
        catch { return false; }
    }

    /// <summary>
    /// Active displays driven by the discrete card (by EDID name, "display" when unnamed): they
    /// go dark in Integrated mode. Empty on a one-card machine or when the roles are unknown.
    /// </summary>
    public static List<string> DisplaysOnDiscrete()
    {
        var roles = GpuTelemetry.GetRoles();
        if (roles.Integrated == null || roles.Main is not { Integrated: false } dgpu) return new();
        return Display.ActiveTargetsOn(dgpu.Luid);
    }

    private static void Store(GpuMode mode)
    {
        if (Read()?.Requested == mode) return;
        using (var key = Registry.LocalMachine.OpenSubKey(MsiCenterKey, writable: true))
        {
            if (key != null)
            {
                // bounce through the idle value so the service sees a change even for a repeat request
                key.SetValue(MsiCenterValue, MsiCenterIdle, RegistryValueKind.DWord);
                key.SetValue(MsiCenterValue, (int)mode, RegistryValueKind.DWord);
                var deadline = DateTime.UtcNow.AddSeconds(4);
                while (DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(250);
                    if (Read()?.Requested == mode) return;
                }
                // the service drops its watchers after sleep or hangs: do the same edit ourselves
            }
        }
        var (data, attr) = ReadVariable();
        data[FlagsOffset] = (byte)((data[FlagsOffset] & 0xFC) | (int)mode);
        if (!SetFirmwareEnvironmentVariableEx(VarName, VarGuid, data, (uint)data.Length, attr))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Writing the MsiDCVarData UEFI variable failed");
        if (Read()?.Requested != mode)
            throw new InvalidOperationException("The UEFI variable did not keep the requested GPU mode.");
    }

    private static (byte[] Data, uint Attr) ReadVariable()
    {
        EnablePrivilege();
        var buf = new byte[4096];
        uint len = GetFirmwareEnvironmentVariableEx(VarName, VarGuid, buf, (uint)buf.Length, out uint attr);
        if (len == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Reading the MsiDCVarData UEFI variable failed");
        if (len <= FlagsOffset) throw new InvalidOperationException($"MsiDCVarData is only {len} bytes long.");
        return (buf[..(int)len], attr);
    }

    private static bool _privilege;

    private static void EnablePrivilege()
    {
        if (_privilege) return;
        if (!OpenProcessToken(GetCurrentProcess(), 0x28 /* ADJUST_PRIVILEGES | QUERY */, out IntPtr token))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!LookupPrivilegeValue(null, "SeSystemEnvironmentPrivilege", out long luid))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var tp = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 /* SE_PRIVILEGE_ENABLED */ };
            // succeeds even when the privilege is not held, so the last error decides
            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero) || Marshal.GetLastWin32Error() == 1300)
                throw new Win32Exception(1300, "SeSystemEnvironmentPrivilege is not available (run as administrator)");
        }
        finally { CloseHandle(token); }
        _privilege = true;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivileges { public uint Count; public long Luid; public uint Attributes; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetFirmwareEnvironmentVariableExW")]
    private static extern uint GetFirmwareEnvironmentVariableEx(string name, string guid, byte[] buffer, uint size, out uint attributes);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetFirmwareEnvironmentVariableExW")]
    private static extern bool SetFirmwareEnvironmentVariableEx(string name, string guid, byte[] value, uint size, uint attributes);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr p, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "LookupPrivilegeValueW")]
    private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges state, uint len, IntPtr prev, IntPtr ret);
}
