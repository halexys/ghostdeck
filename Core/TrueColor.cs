using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GhostDeck;

/// <summary>
/// MSI True Color profiles from GhostDeck (roadmap #119, discussions #150 and #204). The display
/// colour modes (sRGB, Gamer, Movie, Anti-Blue, Office, AdobeRGB, Display P3) belong to MSI's own
/// True Color application; GhostDeck never calculates colours and never touches True Color's
/// files or registry. It asks the RUNNING True Color to switch, through the message its own
/// helper uses: a registered window message (<c>SetChromaTuneColorSpace</c>) sent to the resident
/// hidden window of class <c>MSITrueColorClass</c>, with the profile id as wParam. True Color
/// applies the profile and records it in its registry (<c>CurrentColorSpace</c> / <c>UserMode</c>),
/// which is the only readback there is: the matching Get message answers 0 whatever the profile.
///
/// The protocol is undocumented and confirmed on one machine (GE78HX, True Color 5.1.24.0,
/// Windows 11 26300.9711, 2026-10-17, all seven profiles). Switching is therefore allowed only
/// for versions in <see cref="ConfirmedVersions"/>, or one the owner confirmed with the in-app
/// test on their own machine (a settings value). Detection and the registry readback work on
/// any version. Everything here is synchronous and may wait up to 4.5 s - callers run it on a
/// worker, never on the window thread. See TECHNICAL §80.
/// </summary>
public static class TrueColor
{
    /// <summary>Versions of MsiTrueColor.exe the message protocol was confirmed on: one line per version.</summary>
    public static readonly string[] ConfirmedVersions =
    {
        "5.1.24.0",   // MSI Raider GE78HX 13VH, Windows 11 26H2 (26300.9711), 2026-10-17, seven profiles
    };

    /// <summary>The seven profile ids True Color records, with its own names (product names, not translated) and the CLI words.</summary>
    public static readonly (int Id, string Name, string Cli)[] Profiles =
    {
        (0, "sRGB", "srgb"), (2, "Gamer", "gamer"), (3, "Movie", "movie"), (4, "Anti-Blue", "antiblue"),
        (5, "Office", "office"), (6, "AdobeRGB", "adobergb"), (9, "Display P3", "displayp3"),
    };

    public static bool IsProfile(int id) => Profiles.Any(p => p.Id == id);
    public static string? Name(int id) => Profiles.FirstOrDefault(p => p.Id == id) is { Name.Length: > 0 } p ? p.Name : null;

    /// <summary>"movie", "Movie", "anti-blue", "display p3" or "3" -> the id; null = not a profile.</summary>
    public static int? Parse(string text)
    {
        string t = text.Trim().Replace(" ", "").Replace("-", "").ToLowerInvariant();
        if (t.Length == 0) return null;
        if (int.TryParse(t, out int n)) return IsProfile(n) ? n : null;
        foreach (var p in Profiles)
            if (t == p.Cli || t == p.Name.Replace(" ", "").Replace("-", "").ToLowerInvariant()) return p.Id;
        return null;
    }

    private const string RegistrySubkey = @"Software\Portrait Displays\MSI True Color";
    private const string WindowClass = "MSITrueColorClass";
    private const string WindowTitle = "MSI True Color";
    private const string SetMessage = "SetChromaTuneColorSpace";
    private const uint SmtoBlock = 0x0001, SmtoAbortIfHung = 0x0002;
    public const int SendTimeoutMs = 1500;      // the message must be taken within this
    public const int ConfirmTimeoutMs = 3000;   // the registry must show the profile within this (seen: ~60 ms)

    public enum Status { NotInstalled, NotRunning, Unconfirmed, Ready }

    public enum Result { Ok, AlreadySet, NotInstalled, NotRunning, Unconfirmed, BadProfile, Timeout, NotConfirmed, Busy, Error }

    /// <summary>What is on this machine right now. <see cref="Confirmed"/> = the version is on the list or the owner verified it.</summary>
    public sealed record Info(bool Installed, string ExePath, string Version, bool Running, bool Confirmed, bool UserVerified, int? Recorded)
    {
        public Status Status => !Installed ? Status.NotInstalled : !Confirmed ? Status.Unconfirmed : !Running ? Status.NotRunning : Status.Ready;
        public static Info None => new(false, "", "", false, false, false, null);
    }

    /// <summary>The render harness sets this to show the card in every state without the application installed.</summary>
    public static Func<Info>? TestOverride;

    /// <summary>Raised on the window thread after a switch, a test or an enable: the Settings card and the tray refresh from it.</summary>
    public static event Action? Changed;
    public static void RaiseChanged() => Changed?.Invoke();

    // The install lookup (registry walk + file version) is cached briefly: the tray menu and the
    // Settings card ask several times per second while they refresh; the window and the
    // recorded profile are always read live.
    private static (string exe, string ver, DateTime at) _install = ("", "", DateTime.MinValue);
    private static readonly object Gate = new();

    public static Info Detect(string? userVerifiedVersion)
    {
        if (TestOverride is { } t) return t();
        try
        {
            var (exe, ver) = InstallInfo();
            if (exe.Length == 0) return Info.None;
            bool userOk = userVerifiedVersion != null && string.Equals(userVerifiedVersion, ver, StringComparison.Ordinal);
            bool confirmed = ConfirmedVersions.Contains(ver, StringComparer.Ordinal) || userOk;
            bool running = FindResidentWindow(exe) != IntPtr.Zero;
            return new Info(true, exe, ver, running, confirmed, userOk, Recorded());
        }
        catch (Exception ex)
        {
            AppLifecycle.Report(ex, "truecolor-detect");
            return Info.None;
        }
    }

    /// <summary>The profile True Color last recorded (its own choice, Fn+A or ours); null when the two registry values disagree or are missing.</summary>
    public static int? Recorded()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistrySubkey);
            if (key?.GetValue("CurrentColorSpace") is int cur && key.GetValue("UserMode") is int user && cur == user && IsProfile(cur)) return cur;
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Ask True Color for a profile and wait for its registry to show it. <paramref name="allowUnconfirmed"/>
    /// is for the owner's own test of a version not on the list; every other caller leaves it false.
    /// </summary>
    public static Result Set(int id, string? userVerifiedVersion, out string detail, bool allowUnconfirmed = false)
    {
        detail = "";
        if (!IsProfile(id)) return Result.BadProfile;
        if (!Monitor.TryEnter(Gate, 5000)) return Result.Busy;   // one request at a time: a scene and a click never race
        try
        {
            var info = Detect(userVerifiedVersion);
            if (!info.Installed) return Result.NotInstalled;
            if (!info.Confirmed && !allowUnconfirmed) { detail = info.Version; return Result.Unconfirmed; }
            IntPtr hwnd = FindResidentWindow(info.ExePath);
            if (hwnd == IntPtr.Zero) return Result.NotRunning;
            if (info.Recorded == id) return Result.AlreadySet;

            uint msg = RegisterWindowMessage(SetMessage);
            if (msg == 0) { detail = "RegisterWindowMessage failed"; return Result.Error; }
            // LRESULT is 0 on success as well, so only the delivery counts here; the registry decides.
            IntPtr delivered = SendMessageTimeout(hwnd, msg, new UIntPtr((uint)id), IntPtr.Zero,
                SmtoBlock | SmtoAbortIfHung, SendTimeoutMs, out _);
            if (delivered == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                detail = "SendMessageTimeout: " + err;
                return err == 1460 /* ERROR_TIMEOUT */ || err == 0 ? Result.Timeout : Result.Error;
            }
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ConfirmTimeoutMs)
            {
                if (Recorded() == id) { detail = sw.ElapsedMilliseconds + " ms"; return Result.Ok; }
                Thread.Sleep(50);
            }
            detail = "recorded " + (Recorded()?.ToString() ?? "?");
            return Result.NotConfirmed;
        }
        catch (Exception ex)
        {
            AppLifecycle.Report(ex, "truecolor-set");
            detail = ex.Message;
            return Result.Error;
        }
        finally { Monitor.Exit(Gate); }
    }

    /// <summary>The failure in the UI language, for cards and the change history.</summary>
    public static string ReasonText(Result r, string detail) => r switch
    {
        Result.NotInstalled => Lang.T("tc_err_not_installed"),
        Result.NotRunning => Lang.T("tc_err_not_running"),
        Result.Unconfirmed => string.Format(Lang.T("tc_err_unconfirmed"), detail),
        Result.Timeout => Lang.T("tc_err_timeout"),
        Result.NotConfirmed => Lang.T("tc_err_not_confirmed"),
        Result.Busy => Lang.T("tc_err_busy"),
        _ => Lang.T("tc_err_failed") + (detail.Length > 0 ? " (" + detail + ")" : ""),
    };

    /// <summary>One line for the diagnostic package: what is installed and recorded, plus the exe's SHA-256 (not a gate, a triage aid).</summary>
    public static string DiagLine()
    {
        try
        {
            var (exe, ver) = InstallInfo();
            if (exe.Length == 0) return "MSI True Color: not installed";
            string sha;
            try { using var s = File.OpenRead(exe); sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s)); }
            catch (Exception ex) { sha = "unreadable (" + ex.Message + ")"; }
            bool running = FindResidentWindow(exe) != IntPtr.Zero;
            return $"MSI True Color: {ver} at {exe}   running: {running}   recorded profile: {(Recorded() is { } r ? Name(r) + " (" + r + ")" : "unknown")}" +
                   $"   confirmed version: {ConfirmedVersions.Contains(ver)}   SHA-256: {sha}";
        }
        catch (Exception ex) { return "MSI True Color: lookup failed (" + ex.Message + ")"; }
    }

    // ---------------- detection ----------------

    private static (string exe, string ver) InstallInfo()
    {
        lock (Gate)
        {
            if ((DateTime.UtcNow - _install.at).TotalSeconds < 10) return (_install.exe, _install.ver);
            string? dir = InstallLocation() ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Portrait Displays", "MSI True Color");
            string exe = Path.Combine(dir, "MsiTrueColor.exe");
            string ver = "";
            if (File.Exists(exe))
            {
                try { ver = FileVersionInfo.GetVersionInfo(exe).FileVersion ?? ""; } catch { }
            }
            else exe = "";
            _install = (exe, ver, DateTime.UtcNow);
            return (exe, ver);
        }
    }

    /// <summary>The install folder from the Uninstall hive (the installer records it); null = no entry.</summary>
    private static string? InstallLocation()
    {
        foreach (var (hive, path) in new[]
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        })
        {
            try
            {
                using var root = hive.OpenSubKey(path);
                if (root == null) continue;
                foreach (var sub in root.GetSubKeyNames())
                {
                    try
                    {
                        using var k = root.OpenSubKey(sub);
                        if (k?.GetValue("DisplayName") is not string dn || !dn.Contains("True Color", StringComparison.OrdinalIgnoreCase)) continue;
                        if (k.GetValue("InstallLocation") is string loc && loc.Trim().Length > 0) return loc.Trim().TrimEnd('\\');
                    }
                    catch { }
                }
            }
            catch { }
        }
        return null;
    }

    /// <summary>The hidden resident window, but only when it belongs to the installed exe - not to any process that picked the class name.</summary>
    private static IntPtr FindResidentWindow(string exe)
    {
        IntPtr hwnd = FindWindow(WindowClass, WindowTitle);
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return IntPtr.Zero;
        string owner = ProcessPath(pid);
        return string.Equals(owner, exe, StringComparison.OrdinalIgnoreCase) ? hwnd : IntPtr.Zero;
    }

    // QueryFullProcessImageName with the limited-information right works across integrity levels
    // (GhostDeck runs elevated, True Color as the user); Process.MainModule would need more.
    private static string ProcessPath(uint pid)
    {
        IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            int len = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref len) ? sb.ToString(0, len) : "";
        }
        finally { CloseHandle(h); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint flags, System.Text.StringBuilder exeName, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);
}
