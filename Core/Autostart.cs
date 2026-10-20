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
            else if (NeedsConditionFix(xml))
                Set(true);   // (#172) a task from an older version still carries the battery conditions
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
        if (enabled)
        {
            // (#172) Registered from our own XML so the task starts on battery too. The plain
            // schtasks line stays as the fallback for a scheduler that refuses the XML.
            if (!CreateFromXml())
                Run($"/Create /TN \"{TaskName}\" /TR \"\\\"{ExePath}\\\"\" /SC ONLOGON /RL HIGHEST /F");
        }
        else
            Run($"/Delete /TN \"{TaskName}\" /F");
        IsEnabled();   // remember what the scheduler really holds now
    }

    // (#172) schtasks /Create gives a task Windows' default conditions: start only on mains power,
    // stop when the laptop switches to battery, and - with no ExecutionTimeLimit element - stop
    // after 72 hours. None of that fits a tray app. The task is therefore written from the XML
    // below: the same logon trigger, the same principal (this user, interactive, highest
    // available) and the same action as the schtasks line produced, with both battery conditions
    // off and no time limit. The path and the user's SID come straight from the process, so a
    // non-ASCII path never passes through a console code page.
    private static bool NeedsConditionFix(string xml) =>
        xml.Contains("<DisallowStartIfOnBatteries>true", StringComparison.OrdinalIgnoreCase)
        || xml.Contains("<StopIfGoingOnBatteries>true", StringComparison.OrdinalIgnoreCase)
        || !xml.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", StringComparison.OrdinalIgnoreCase);

    private static bool CreateFromXml()
    {
        string? sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrEmpty(sid)) return false;
        string cmd = System.Security.SecurityElement.Escape(ExePath) ?? ExePath;
        string xml = $$"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Principals>
                <Principal id="Author">
                  <UserId>{{sid}}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
              </Settings>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                </LogonTrigger>
              </Triggers>
              <Actions Context="Author">
                <Exec>
                  <Command>{{cmd}}</Command>
                </Exec>
              </Actions>
            </Task>
            """;
        string file = Path.Combine(Path.GetTempPath(), $"ghostdeck-task-{Environment.ProcessId}.xml");
        try
        {
            File.WriteAllText(file, xml, System.Text.Encoding.Unicode);   // the declaration says UTF-16
            return Run($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F") == 0;
        }
        catch { return false; }
        finally { try { File.Delete(file); } catch { } }
    }
}
