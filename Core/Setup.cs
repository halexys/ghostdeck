using System.Diagnostics;
using System.Management;
using System.Reflection;
using Microsoft.Win32;

namespace GhostDeck;

/// <summary>
/// The one-file installer of this fork (docs/TECHNICAL.md §84). The same GhostDeck.exe, started as
/// GhostDeckSetup*.exe or with --setup / --uninstall, installs itself: copy to Program Files,
/// the MSI WMI schema when Windows lacks it, the autostart task, a Start-menu shortcut and an
/// Apps &amp; Features entry. The schema (msiapcfg.dll, MSI-signed) is NOT in the repository: a
/// local build embeds the copy from the build machine (tools/build-setup.ps1, gitignored
/// local/ folder); a build without it installs everything else and says MSI Center is needed.
/// </summary>
internal static class Setup
{
    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GhostDeck");
    public static string InstalledExe => Path.Combine(InstallDir, "GhostDeck.exe");
    private static string ShortcutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "GhostDeck.lnk");

    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\GhostDeck";
    private const string SchemaResource = "GhostDeck.msiapcfg.dll";
    private const string SchemaRegValue = @"%windir%\sysWOW64\msiapcfg.dll";
    private static string SchemaFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64", "msiapcfg.dll");

    public static Version ThisVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    /// <summary>True when this process should show the installer instead of the tray app or the CLI.</summary>
    public static bool IsSetupLaunch(string[] args) =>
        args is ["--setup", ..] or ["--uninstall", ..]
        || Path.GetFileName(Environment.ProcessPath ?? "").StartsWith("GhostDeckSetup", StringComparison.OrdinalIgnoreCase);

    // ---------------- what is there now ----------------

    /// <summary>The installed version from the Apps &amp; Features entry, null = not installed by this setup.</summary>
    public static Version? InstalledVersion()
    {
        using var k = Registry.LocalMachine.OpenSubKey(UninstallKey);
        return k?.GetValue("DisplayVersion") is string s && Version.TryParse(s, out var v) ? v : null;
    }

    public static bool SchemaEmbedded => Assembly.GetExecutingAssembly().GetManifestResourceInfo(SchemaResource) != null;

    /// <summary>The MSI_ACPI class exists in Windows: the schema is deployed and published.</summary>
    public static bool SchemaPublished()
    {
        try
        {
            using var c = new ManagementClass(@"root\wmi", "MSI_ACPI", null);
            c.Get();
            return true;
        }
        catch { return false; }
    }

    public enum SchemaPlan { Present, WillInstall, Missing }

    public static SchemaPlan PlanSchema() =>
        SchemaPublished() ? SchemaPlan.Present : SchemaEmbedded ? SchemaPlan.WillInstall : SchemaPlan.Missing;

    // ---------------- install ----------------

    public sealed record Result(bool NeedsReboot, string? ProfileNote);

    /// <summary>
    /// Runs every step, reporting each by index (0-4) as it starts. On a failure the copied exe is
    /// rolled back and the exception propagates with a readable message.
    /// </summary>
    public static Result Install(bool autostart, Action<int> step)
    {
        step(0); StopOtherInstances();

        step(1);
        Directory.CreateDirectory(InstallDir);
        string self = Environment.ProcessPath ?? throw new InvalidOperationException("No se encuentra el ejecutable del instalador.");
        string backup = InstalledExe + ".bak";
        bool replaced = false;
        if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(InstalledExe)) { File.Copy(InstalledExe, backup, true); replaced = true; }
            File.Copy(self, InstalledExe, true);
        }
        try
        {
            step(2);
            bool reboot = DeploySchema();

            step(3);
            Autostart.Set(autostart, InstalledExe);

            step(4);
            CreateShortcut();
            WriteUninstallEntry();
            if (replaced) TryDelete(backup);

            return new Result(reboot, reboot ? null : ProfileNote());
        }
        catch
        {
            if (replaced) { try { File.Copy(backup, InstalledExe, true); TryDelete(backup); } catch { } }
            throw;
        }
    }

    /// <summary>
    /// Copies the embedded msiapcfg.dll and points WmiAcpi's MofImagePath at it, then restarts the
    /// ACPI-WMI devices so wmiacpi.sys publishes the classes now. Returns true when only a reboot
    /// will make them appear. Untouched when the class already exists.
    /// </summary>
    private static bool DeploySchema()
    {
        if (SchemaPublished()) return false;
        if (!SchemaEmbedded) return false;   // nothing to deploy; the done page says MSI Center is needed

        if (!File.Exists(SchemaFile))
        {
            using var src = Assembly.GetExecutingAssembly().GetManifestResourceStream(SchemaResource)!;
            using var dst = File.Create(SchemaFile);
            src.CopyTo(dst);
        }
        using (var k = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\WmiAcpi"))
            k.SetValue("MofImagePath", SchemaRegValue, RegistryValueKind.ExpandString);

        // what HandheldCompanion does for the same schema: restart the ACPI-WMI device(s)
        try
        {
            using var s = new ManagementObjectSearcher("SELECT PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'ACPI\\\\PNP0C14%'");
            foreach (ManagementObject d in s.Get())
                using (d) Run("pnputil.exe", $"/restart-device \"{d["PNPDeviceID"]}\"");
        }
        catch { }
        for (int i = 0; i < 10 && !SchemaPublished(); i++) Thread.Sleep(500);
        return !SchemaPublished();
    }

    private static string? ProfileNote()
    {
        try
        {
            string fw = Ec.ReadFirmware();
            if (Devices.Detect(fw) is not { } dev) return fw.Length > 0 ? $"firmware {fw} (modelo no reconocido)" : null;
            return Profiles.Get(Ec.GetCurrent(dev)).Label;
        }
        catch { return null; }
    }

    private static void StopOtherInstances()
    {
        int me = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName("GhostDeck"))
            using (p)
            {
                if (p.Id == me) continue;
                try { p.Kill(); p.WaitForExit(5000); } catch { }
            }
    }

    private static void CreateShortcut()
    {
        var t = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell no está disponible.");
        dynamic shell = Activator.CreateInstance(t)!;
        try
        {
            dynamic lnk = shell.CreateShortcut(ShortcutPath);
            lnk.TargetPath = InstalledExe;
            lnk.WorkingDirectory = InstallDir;
            lnk.IconLocation = InstalledExe + ",0";
            lnk.Description = "GhostDeck";
            lnk.Save();
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }

    private static void WriteUninstallEntry()
    {
        using var k = Registry.LocalMachine.CreateSubKey(UninstallKey);
        var v = ThisVersion;
        k.SetValue("DisplayName", "GhostDeck");
        k.SetValue("DisplayVersion", $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}");
        k.SetValue("Publisher", "halexys (fork de GhostDeck)");
        k.SetValue("URLInfoAbout", "https://github.com/halexys/ghostdeck");
        k.SetValue("InstallLocation", InstallDir);
        k.SetValue("DisplayIcon", InstalledExe + ",0");
        k.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        k.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
    }

    // ---------------- uninstall ----------------

    /// <summary>
    /// Removes the app, the task, the shortcut and the entry. Keeps the MSI schema (other MSI tools
    /// may use it) and %AppData%\GhostDeck (settings, scenes, curves). The folder goes after this
    /// process exits, because a running exe cannot delete itself.
    /// </summary>
    public static void Uninstall()
    {
        StopOtherInstances();
        try { Autostart.Set(false); } catch { }
        TryDelete(ShortcutPath);
        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 4 127.0.0.1 >nul & rmdir /s /q \"{InstallDir}\"")
            { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
        }
        catch { }
    }

    // ---------------- helpers ----------------

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    private static void Run(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false });
        p?.WaitForExit(15000);
    }
}
