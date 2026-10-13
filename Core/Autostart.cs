using System.Diagnostics;

namespace GhostDeck;

/// <summary>
/// Autostart przez zadanie Harmonogramu (ONLOGON + RL HIGHEST) — uruchamia
/// elevowany .exe przy logowaniu BEZ promptu UAC. Tworzone/usuwane z Ustawien.
/// </summary>
public static class Autostart
{
    private const string TaskName = "GhostDeck";
    private const string OldTaskName = "MSIProfileSwitcher";   // pre-rename task, migrated away on startup

    private static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;

    /// <summary>
    /// One-time rename migration: if the old "MSIProfileSwitcher" autostart task exists, delete it
    /// (it points at the old exe) and, since it means autostart was enabled, recreate it under the new
    /// name pointing at the current exe. Safe/idempotent — a no-op once the old task is gone.
    /// </summary>
    public static void Migrate()
    {
        try
        {
            if (Run($"/Query /TN \"{OldTaskName}\"") != 0) return;   // no old task -> nothing to do
            Run($"/Delete /TN \"{OldTaskName}\" /F");
            Set(true);                                               // recreate as "GhostDeck" at the current exe
        }
        catch { }
    }

    private static int Run(string args)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }

    private static string RunRead(string args)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }

    /// <summary>
    /// The task stores the exe path from the moment autostart was enabled, so moving the exe
    /// (e.g. into Program Files) silently broke autostart (discussion #9). If the task exists
    /// but points elsewhere, recreate it at the current location. Runs on every startup.
    /// </summary>
    public static void Heal()
    {
        try
        {
            if (!IsEnabled()) return;
            string xml = RunRead($"/Query /TN \"{TaskName}\" /XML");
            var m = System.Text.RegularExpressions.Regex.Match(xml, @"<Command>\s*""?([^""<]+?)""?\s*</Command>");
            if (m.Success && !string.Equals(m.Groups[1].Value.Trim(), ExePath, StringComparison.OrdinalIgnoreCase))
                Set(true);
            // Tasks made by older versions carry the scheduler's laptop-hostile defaults (see Set):
            // no start on battery, killed when unplugged, killed after 72 h. Recreate them once.
            else if (!xml.Contains("<DisallowStartIfOnBatteries>false", StringComparison.OrdinalIgnoreCase)
                     || !xml.Contains("<StopIfGoingOnBatteries>false", StringComparison.OrdinalIgnoreCase)
                     || !xml.Contains("<ExecutionTimeLimit>PT0S", StringComparison.OrdinalIgnoreCase))
                Set(true);
        }
        catch { }
    }

    private static bool? _known;   // the last answer of IsEnabled / Set

    /// <summary>Asks the Task Scheduler: starts schtasks.exe and waits for it (20-30 ms).</summary>
    public static bool IsEnabled()
    {
        bool on;
        try { on = Run($"/Query /TN \"{TaskName}\"") == 0; }
        catch { on = false; }
        _known = on;
        return on;
    }

    /// <summary>
    /// The remembered answer - for callers that ask often (status snapshots are built on every
    /// repaint of the tab strip). The task only changes through Set while the app runs.
    /// </summary>
    public static bool Known => _known ?? IsEnabled();

    public static void Set(bool enabled)
    {
        if (enabled) Create();
        else Run($"/Delete /TN \"{TaskName}\" /F");
        IsEnabled();   // remember what the scheduler really holds now
    }

    /// <summary>
    /// The task is created from a full definition, not from /SC ONLOGON flags: the flags leave the
    /// scheduler's defaults in place, and those are wrong for a laptop tray app - it would not
    /// start at a logon on battery, be killed the moment the charger is pulled, and be killed
    /// after 72 hours. Same trigger, user and elevation as before (logon of the current user,
    /// highest privileges, so no UAC prompt).
    /// </summary>
    private static void Create()
    {
        string user = System.Security.SecurityElement.Escape(System.Security.Principal.WindowsIdentity.GetCurrent().Name);
        string exe = System.Security.SecurityElement.Escape(ExePath);
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers>
                <LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec><Command>"{exe}"</Command></Exec>
              </Actions>
            </Task>
            """;
        string file = Path.Combine(Path.GetTempPath(), $"ghostdeck-task-{Environment.ProcessId}.xml");
        try
        {
            File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
            if (Run($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F") != 0)   // fallback: the old flags rather than no autostart
                Run($"/Create /TN \"{TaskName}\" /TR \"\\\"{ExePath}\\\"\" /SC ONLOGON /RL HIGHEST /F");
        }
        finally { try { File.Delete(file); } catch { } }
    }
}
