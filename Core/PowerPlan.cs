using System.Runtime.InteropServices;
using System.Text;

namespace GhostDeck;

/// <summary>
/// Windows power-plan control for the "Windows power" card (discussion #141; roadmap #109 + #36):
/// the processor turbo-boost setting (PERFBOOSTMODE) of a power plan, and the Windows power
/// mode (the "Power mode" slider in Settings). Everything here is user-mode powrprof API;
/// nothing touches the EC, so it works on machines the EC side does not support.
///
/// Two properties shape the whole design. First, a power-plan write is PERSISTENT until
/// something changes it back - unlike the app's volatile EC writes - so turbo is only ever
/// disabled after snapshotting the previous AC/DC values PER PLAN GUID (writing plan A's old
/// values into plan B would be wrong), and a snapshot is taken only on a non-zero -> 0
/// transition so an original can never be overwritten with zeros. Second, the newer exports
/// (user-configured power mode, effective-mode notifications) exist only on Windows 11, so
/// they are resolved dynamically and every caller degrades gracefully when they are absent.
/// </summary>
public static class PowerPlan
{
    // ---------------- GUIDs ----------------
    private static Guid _subProcessor = new("54533251-82be-4824-96c1-47b60b740d00");   // SUB_PROCESSOR
    private static Guid _perfBoost = new("be337238-0d82-4146-a960-4f3749d470c7");      // PERFBOOSTMODE
    private static Guid _perfAutonomous = new("8baa4a8a-14c6-4451-8e8b-14bdbd197537"); // PERFAUTONOMOUS (read only)

    // The three documented user-configured power modes (PowerSetUserConfiguredACPowerMode).
    public static readonly Guid ModeBestEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    public static readonly Guid ModeBalanced = Guid.Empty;   // GUID_POWER_MODE_NONE
    public static readonly Guid ModeBestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    private const uint AttribHide = 1;       // POWER_ATTRIBUTE_HIDE
    private const uint AttribShowAoac = 2;   // POWER_ATTRIBUTE_SHOW_AOAC: shown on Modern Standby machines too

    // ---------------- documented imports (present since Windows 7) ----------------
    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerReadPossibleValue(IntPtr root, ref Guid sub, ref Guid setting, ref uint type, uint index, byte[]? buffer, ref uint size);
    [DllImport("powrprof.dll")] private static extern uint PowerReadPossibleFriendlyName(IntPtr root, ref Guid sub, ref Guid setting, uint index, byte[]? buffer, ref uint size);
    [DllImport("powrprof.dll")] private static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr sub, IntPtr setting, byte[]? buffer, ref uint size);
    [DllImport("powrprof.dll")] private static extern uint PowerReadSettingAttributes(ref Guid sub, ref Guid setting);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteSettingAttributes(ref Guid sub, ref Guid setting, uint attributes);
    [DllImport("powrprof.dll")] private static extern bool GetPwrCapabilities(byte[] caps);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr mem);

    // ---------------- Windows 11 exports, resolved dynamically ----------------
    private delegate uint SetModeFn(ref Guid mode);
    private delegate uint GetModeFn(out Guid mode);
    private delegate uint OverlayFn(Guid mode);
    private delegate void EffectiveCb(int mode, IntPtr ctx);
    private delegate int RegisterEffectiveFn(uint version, EffectiveCb callback, IntPtr ctx, out IntPtr handle);

    private static readonly SetModeFn? _setAcMode = Resolve<SetModeFn>("PowerSetUserConfiguredACPowerMode");
    private static readonly SetModeFn? _setDcMode = Resolve<SetModeFn>("PowerSetUserConfiguredDCPowerMode");
    private static readonly GetModeFn? _getAcMode = Resolve<GetModeFn>("PowerGetUserConfiguredACPowerMode");
    private static readonly GetModeFn? _getDcMode = Resolve<GetModeFn>("PowerGetUserConfiguredDCPowerMode");
    private static readonly OverlayFn? _setOverlay = Resolve<OverlayFn>("PowerSetActiveOverlayScheme");
    private static readonly RegisterEffectiveFn? _registerEffective = Resolve<RegisterEffectiveFn>("PowerRegisterForEffectivePowerModeNotifications");

    private static T? Resolve<T>(string name) where T : class
    {
        try
        {
            if (!NativeLibrary.TryLoad("powrprof.dll", out var lib)) return null;
            return NativeLibrary.TryGetExport(lib, name, out var p)
                ? Marshal.GetDelegateForFunctionPointer<T>(p) : null;
        }
        catch { return null; }
    }

    /// <summary>The official user-configured power-mode API (Windows 11).</summary>
    public static bool UserModeApiPresent => _setAcMode != null && _setDcMode != null;

    // ---------------- schemes ----------------

    public static bool TryGetActiveScheme(out Guid scheme)
    {
        scheme = Guid.Empty;
        IntPtr p = IntPtr.Zero;
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out p) != 0 || p == IntPtr.Zero) return false;
            scheme = Marshal.PtrToStructure<Guid>(p);
            return true;
        }
        catch { return false; }
        finally { if (p != IntPtr.Zero) LocalFree(p); }
    }

    /// <summary>Localized plan name from Windows; "" when unavailable.</summary>
    public static string SchemeName(Guid scheme)
    {
        try
        {
            uint size = 0;
            PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, null, ref size);
            if (size == 0 || size > 4096) return "";
            var buf = new byte[size];
            if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buf, ref size) != 0) return "";
            return Encoding.Unicode.GetString(buf).TrimEnd('\0');
        }
        catch { return ""; }
    }

    /// <summary>A plan the boost setting can be read from still exists (deleted custom plans fail here).</summary>
    public static bool SchemeExists(Guid scheme) => TryReadBoost(scheme, out _, out _);

    // ---------------- turbo boost (PERFBOOSTMODE) ----------------

    public static bool TryReadBoost(Guid scheme, out uint ac, out uint dc)
    {
        ac = dc = 0;
        try
        {
            return PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref _subProcessor, ref _perfBoost, out ac) == 0
                && PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref _subProcessor, ref _perfBoost, out dc) == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// "Processor performance autonomous mode" of a plan, read only (#117 help card and the test
    /// reports): 1 = Windows lets the processor pick its own clock, which is where Microsoft says
    /// the boost modes 1-4 all behave as Enabled. Microsoft notes that the platform may ignore the
    /// setting, so it describes what Windows asks for, not what the processor does.
    /// </summary>
    public static bool TryReadAutonomous(Guid scheme, out uint ac, out uint dc)
    {
        ac = dc = 0;
        try
        {
            return PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref _subProcessor, ref _perfAutonomous, out ac) == 0
                && PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref _subProcessor, ref _perfAutonomous, out dc) == 0;
        }
        catch { return false; }
    }

    /// <summary>Autonomous mode is on for both power sources of the active plan.</summary>
    public static bool AutonomousOn(Guid? activeScheme = null)
    {
        if (_autonomousPreview is { } preview) return preview;
        Guid scheme;
        if (activeScheme is { } known) scheme = known;
        else if (!TryGetActiveScheme(out scheme)) return false;
        return TryReadAutonomous(scheme, out uint ac, out uint dc) && ac == 1 && dc == 1;
    }

    // Set only by the render harness (reflection) to draw the "autonomous mode off" look on a
    // machine that has it on; never assigned by the app.
    private static bool? _autonomousPreview = null;

    // ---------------- what a turbo mode really does (#117) ----------------
    // The card, its lists, By profile, the editor and the help card show every mode with the
    // icon and colour of its group, so seven names that mostly behave alike read as the few
    // behaviours they are. Groups follow Microsoft's tables: 0 is the one mode that lowers power;
    // 1 and 3 are plain turbo; 2 and 4 are plain turbo too while autonomous mode is on, and only
    // otherwise a group of their own (Aggressive is described apart only on non-autonomous CPPC);
    // 5 and 6 ask for more than the guaranteed level.

    public enum BoostGroup { Less, Turbo, TurboMax, AskMore, Unknown }

    public static BoostGroup GroupOf(uint value, bool autonomous) => value switch
    {
        0 => BoostGroup.Less,
        1 or 3 => BoostGroup.Turbo,
        2 or 4 => autonomous ? BoostGroup.Turbo : BoostGroup.TurboMax,
        5 or 6 => BoostGroup.AskMore,
        _ => BoostGroup.Unknown,
    };

    public static Color GroupColor(BoostGroup g) => g switch
    {
        BoostGroup.Less => Theme.Green,
        BoostGroup.Turbo => Theme.Accent,
        BoostGroup.TurboMax => Theme.Amber,
        BoostGroup.AskMore => Theme.Violet,
        _ => Theme.Muted,
    };

    public static string GroupLabel(BoostGroup g) => Lang.T(g switch
    {
        BoostGroup.Less => "pw_bg_less",
        BoostGroup.Turbo => "pw_bg_turbo",
        BoostGroup.TurboMax => "pw_bg_max",
        BoostGroup.AskMore => "pw_bg_more",
        _ => "pw_bg_unknown",
    });

    /// <summary>The one-line description under a list on the card; the full ones stay in the help card.</summary>
    public static string ShortDescription(uint value, bool autonomous) => value switch
    {
        0 => Lang.T("pw_bmshort_less"),
        1 => Lang.T("pw_bmshort_on"),
        2 or 3 or 4 when autonomous => string.Format(Lang.T("pw_bmshort_like"), BoostName(1)),
        2 => Lang.T("pw_bmshort_max"),
        3 => string.Format(Lang.T("pw_bmshort_like_ms"), BoostName(1)),
        4 => string.Format(Lang.T("pw_bmshort_like_ms"), BoostName(2)),
        5 or 6 => Lang.T("pw_bmshort_more"),
        _ => Lang.T("pw_bm_unknown"),
    };

    /// <summary>Writes both values to the given plan; refreshes only when that plan is active.</summary>
    public static bool WriteBoost(Guid scheme, uint ac, uint dc)
    {
        try
        {
            if (PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref _subProcessor, ref _perfBoost, ac) != 0) return false;
            if (PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref _subProcessor, ref _perfBoost, dc) != 0) return false;
            // The documented apply step - but only for the plan that is actually active. Restoring
            // values into other (inactive) plans must NEVER switch the machine onto them.
            if (TryGetActiveScheme(out var active) && active == scheme)
                PowerSetActiveScheme(IntPtr.Zero, ref scheme);
            return true;
        }
        catch { return false; }
    }

    public sealed record BoostMode(uint Value, string Name);

    /// <summary>
    /// The boost modes this Windows actually offers, enumerated dynamically (no hardcoded
    /// 0..6 - the list differs between builds). Names come localized from Windows itself.
    /// </summary>
    public static List<BoostMode> BoostModes() => _boostCache ??= EnumerateBoostModes();

    // The enumeration costs ~5 ms (21 powrprof calls) and the card asks for names about ten
    // times per refresh; the list cannot change while the app runs, so it is read once.
    private static List<BoostMode>? _boostCache;

    private static List<BoostMode> EnumerateBoostModes()
    {
        var list = new List<BoostMode>();
        try
        {
            for (uint i = 0; i < 16; i++)
            {
                uint type = 0, vSize = 8;
                var vBuf = new byte[8];
                if (PowerReadPossibleValue(IntPtr.Zero, ref _subProcessor, ref _perfBoost, ref type, i, vBuf, ref vSize) != 0)
                    break;
                uint val = BitConverter.ToUInt32(vBuf, 0);
                string name = val.ToString();
                uint nSize = 0;
                PowerReadPossibleFriendlyName(IntPtr.Zero, ref _subProcessor, ref _perfBoost, i, null, ref nSize);
                if (nSize is > 0 and <= 4096)
                {
                    var nBuf = new byte[nSize];
                    if (PowerReadPossibleFriendlyName(IntPtr.Zero, ref _subProcessor, ref _perfBoost, i, nBuf, ref nSize) == 0)
                        name = Encoding.Unicode.GetString(nBuf).TrimEnd('\0');
                }
                list.Add(new BoostMode(val, name));
            }
        }
        catch { }
        return list;
    }

    public static string BoostName(uint value) =>
        BoostModes().FirstOrDefault(m => m.Value == value)?.Name ?? value.ToString();

    /// <summary>
    /// The app's own fallback for "turn turbo on with no snapshot", validated against the
    /// enumeration: Aggressive (2) if this Windows offers it, else Enabled (1), else the
    /// first non-zero mode. Null = nothing sensible exists; write nothing.
    /// This is GHOSTDECK's default, not "the system default" - OEMs ship different ones.
    /// </summary>
    public static BoostMode? FallbackBoost()
    {
        var modes = BoostModes();
        return modes.FirstOrDefault(m => m.Value == 2)
            ?? modes.FirstOrDefault(m => m.Value == 1)
            ?? modes.FirstOrDefault(m => m.Value != 0);
    }

    // ---------------- turbo operations shared by the Settings card and the CLI ----------------
    // Return "" on success or an English machine-readable reason (CLI output stays English by
    // design; the card wraps failures in its own localized text).
    //
    // Two memories per plan GUID, both in AppSettings and both machine state:
    // - TurboSnapshots: the modes the main switch brings back. Taken once, before a write that
    //   turns a boosting source to 0; while turbo is off, a mode picked in the card's list for one
    //   source updates that side, so "on" returns to what the user chose last.
    // - TurboOriginals (#117): the pair the plan had before GhostDeck's FIRST boost write of any
    //   kind. "Restore Windows settings" writes it back. Since modes can now be picked from a list,
    //   a non-zero value can be replaced by another non-zero one, which a snapshot never records.
    //   Dropped as soon as the plan is back to exactly that pair with nothing else held.

    private static string Key(Guid scheme) => scheme.ToString("D").ToLowerInvariant();

    private static void KeepOriginal(AppSettings s, string key, uint ac, uint dc)
    {
        if (!s.TurboOriginals.ContainsKey(key)) s.TurboOriginals[key] = new[] { (int)ac, (int)dc };
    }

    private static void Tidy(AppSettings s, string key, uint ac, uint dc)
    {
        if (s.TurboSnapshots.TryGetValue(key, out var snap) && snap is { Length: 2 } && snap[0] == (int)ac && snap[1] == (int)dc)
            s.TurboSnapshots.Remove(key);   // fully back to what the switch would restore
        if (!s.TurboSnapshots.ContainsKey(key) && s.TurboOriginals.TryGetValue(key, out var o)
            && o is { Length: 2 } && o[0] == (int)ac && o[1] == (int)dc)
            s.TurboOriginals.Remove(key);   // nothing GhostDeck changed is left in this plan
    }

    public static string TurboOff(AppSettings s)
    {
        if (!TryGetActiveScheme(out var scheme)) return "cannot read the active power plan";
        if (!TryReadBoost(scheme, out uint ac, out uint dc)) return "cannot read the boost setting";
        if (ac == 0 && dc == 0) return "";   // already off; nothing to snapshot (never snapshot zeros)
        string key = Key(scheme);
        // Snapshot BEFORE the write, per plan GUID, only on a non-zero -> 0 transition. A source
        // that is already 0 keeps what an earlier snapshot holds for it (one side switched off
        // from the list, then the whole switch), so nothing chosen is lost.
        s.TurboSnapshots[key] = s.TurboSnapshots.TryGetValue(key, out var old) && old is { Length: 2 }
            ? new[] { ac != 0 ? (int)ac : old[0], dc != 0 ? (int)dc : old[1] }
            : new[] { (int)ac, (int)dc };
        KeepOriginal(s, key, ac, dc);
        s.Save();
        return WriteBoost(scheme, 0, 0) ? "" : "Windows refused the boost write";
    }

    public static string TurboOn(AppSettings s)
    {
        if (!TryGetActiveScheme(out var scheme)) return "cannot read the active power plan";
        if (!TryReadBoost(scheme, out uint a0, out uint d0)) return "cannot read the boost setting";
        string key = Key(scheme);
        uint ac, dc;
        if (s.TurboSnapshots.TryGetValue(key, out var snap) && snap is { Length: 2 })
        {
            ac = (uint)snap[0]; dc = (uint)snap[1];
        }
        else if (FallbackBoost() is { } fb) { ac = dc = fb.Value; }
        else return "this Windows enumerates no usable boost mode";
        KeepOriginal(s, key, a0, d0);
        if (!WriteBoost(scheme, ac, dc)) { s.Save(); return "Windows refused the boost write"; }
        s.TurboSnapshots.Remove(key);   // consumed only after a successful write
        Tidy(s, key, ac, dc);
        s.Save();
        return "";
    }

    /// <summary>
    /// Sets the turbo mode of one or both sources in the active plan (#117): the card's two lists
    /// and the per-profile mapping. A negative value leaves that source as it is. Same snapshot
    /// rules as the main switch: the pair is captured once before a boosting source goes to 0,
    /// and while a snapshot exists a non-zero pick replaces that side of it, so the switch later
    /// brings back what was chosen. <paramref name="changed"/> is false when nothing was written.
    /// </summary>
    public static string TurboSetModes(AppSettings s, int acMode, int dcMode, out bool changed)
    {
        changed = false;
        if (!TryGetActiveScheme(out var scheme)) return "cannot read the active power plan";
        if (!TryReadBoost(scheme, out uint ac, out uint dc)) return "cannot read the boost setting";
        uint nAc = acMode >= 0 ? (uint)acMode : ac, nDc = dcMode >= 0 ? (uint)dcMode : dc;
        if (nAc == ac && nDc == dc) return "";
        string key = Key(scheme);
        bool haveSnap = s.TurboSnapshots.TryGetValue(key, out var snap) && snap is { Length: 2 };
        if (!haveSnap && ((ac != 0 && nAc == 0) || (dc != 0 && nDc == 0)))
            s.TurboSnapshots[key] = new[] { (int)(nAc != 0 ? nAc : ac), (int)(nDc != 0 ? nDc : dc) };
        else if (haveSnap)
        {
            if (nAc != ac) snap![0] = (int)(nAc != 0 ? nAc : ac);   // a pick, or the mode being switched off
            if (nDc != dc) snap![1] = (int)(nDc != 0 ? nDc : dc);
        }
        KeepOriginal(s, key, ac, dc);
        if (!WriteBoost(scheme, nAc, nDc)) { s.Save(); return "Windows refused the boost write"; }
        changed = true;
        Tidy(s, key, nAc, nDc);
        s.Save();
        return "";
    }

    // ---------------- turbo per profile (#117) ----------------
    // AppSettings.TurboMap: profile key -> [plugged in, battery]; -1 = "no change". Only profiles
    // with at least one source set are stored, so an empty map is the default: nothing happens.

    public static (int Ac, int Dc) TurboFor(AppSettings s, ProfileId id) =>
        s.TurboMap.TryGetValue(id.ToString(), out var v) && v is { Length: 2 } ? (v[0], v[1]) : (-1, -1);

    /// <summary>
    /// Applied on a profile switch, like the power-mode sync and never enforced in the background:
    /// a later change on the card stands until the next switch. A mode this Windows does not list
    /// (a settings file from another machine) is skipped. Returns null when nothing was due.
    /// </summary>
    public static string? ApplyProfileTurbo(AppSettings s, ProfileId id)
    {
        var (a, d) = TurboFor(s, id);
        var modes = BoostModes();
        if (a >= 0 && !modes.Any(m => m.Value == a)) a = -1;
        if (d >= 0 && !modes.Any(m => m.Value == d)) d = -1;
        if (a < 0 && d < 0) return null;
        string err = TurboSetModes(s, a, d, out bool changed);
        return err.Length > 0 ? err : changed ? "" : null;
    }

    /// <summary>Lang key of the plain description of a boost mode; formats take the Windows names of modes 1 and 2.</summary>
    public static string BoostDescription(uint value) => value switch
    {
        0 => Lang.T("pw_bm_0"),
        1 => Lang.T("pw_bm_1"),
        2 => string.Format(Lang.T("pw_bm_2"), BoostName(1)),
        3 => string.Format(Lang.T("pw_bm_3"), BoostName(1)),
        4 => string.Format(Lang.T("pw_bm_4"), BoostName(2)),
        5 => Lang.T("pw_bm_5"),
        6 => Lang.T("pw_bm_6"),
        _ => Lang.T("pw_bm_unknown"),
    };

    public static string TurboStatus()
    {
        if (!TryGetActiveScheme(out var scheme) || !TryReadBoost(scheme, out uint ac, out uint dc))
            return "boost state unavailable";
        string state = ac == 0 && dc == 0 ? "off" : ac != 0 && dc != 0 ? "on" : "mixed";
        return $"turbo boost: {state} (AC {BoostName(ac)}, battery {BoostName(dc)})";
    }

    /// <summary>What "Restore Windows settings" would write per plan: the original pair, else the snapshot.</summary>
    public static List<(Guid Scheme, int[] Pair)> RestoreTargets(AppSettings s)
    {
        var list = new List<(Guid, int[])>();
        foreach (var key in s.TurboOriginals.Keys.Union(s.TurboSnapshots.Keys).ToArray())
        {
            int[]? pair = s.TurboOriginals.TryGetValue(key, out var o) && o is { Length: 2 } ? o
                : s.TurboSnapshots.TryGetValue(key, out var sn) && sn is { Length: 2 } ? sn : null;
            if (pair != null && Guid.TryParse(key, out var g)) list.Add((g, pair));
        }
        return list;
    }

    /// <summary>
    /// "Restore Windows settings": puts every plan GhostDeck changed back to its original pair
    /// (or, for a plan changed only by an older version, its snapshot) and reports what happened.
    /// Never activates another plan - values are written per GUID and the active plan is
    /// refreshed once at the end. A deleted plan drops its stale memory; a failed write keeps it
    /// so the user can retry.
    /// </summary>
    public static (int restored, int missing, int failed) RestoreAll(AppSettings s)
    {
        int restored = 0, missing = 0, failed = 0;
        foreach (var key in s.TurboOriginals.Keys.Union(s.TurboSnapshots.Keys).ToArray())
        {
            int[]? pair = s.TurboOriginals.TryGetValue(key, out var o) && o is { Length: 2 } ? o
                : s.TurboSnapshots.TryGetValue(key, out var sn) && sn is { Length: 2 } ? sn : null;
            void Drop() { s.TurboOriginals.Remove(key); s.TurboSnapshots.Remove(key); }
            if (pair == null || !Guid.TryParse(key, out var scheme)) { Drop(); continue; }
            if (!SchemeExists(scheme)) { Drop(); missing++; continue; }
            if (WriteBoost(scheme, (uint)pair[0], (uint)pair[1])) { Drop(); restored++; }
            else failed++;
        }
        s.Save();
        return (restored, missing, failed);
    }

    /// <summary>
    /// One English line for the test reports (#236, #230): the active plan, the turbo mode on
    /// both sources (number and Windows' own name) and the power mode. A run with turbo off
    /// measures a different machine, and without this line the reader can only guess.
    /// </summary>
    public static string ReportLine()
    {
        var parts = new List<string>();
        if (TryGetActiveScheme(out var scheme))
        {
            parts.Add($"plan \"{SchemeName(scheme)}\"");
            parts.Add(TryReadBoost(scheme, out uint ac, out uint dc)
                ? $"CPU turbo boost AC {ac} ({BoostName(ac)}), battery {dc} ({BoostName(dc)})"
                : "CPU turbo boost unreadable");
            parts.Add(TryReadAutonomous(scheme, out uint pa, out uint pd)
                ? $"autonomous mode AC {pa}, battery {pd}"
                : "autonomous mode unreadable");
        }
        else parts.Add("active plan unreadable");
        if (TryGetUserPowerMode(out var mAc, out var mDc))
        {
            string mode = mAc == mDc ? ModeText(mAc) : $"AC {ModeText(mAc)}, battery {ModeText(mDc)}";
            if (EffectiveText(EffectiveMode) is { Length: > 0 } eff) mode += $" (effective: {eff})";
            parts.Add("power mode " + mode);
        }
        return string.Join("; ", parts);
    }

    private static string ModeText(Guid m) => ModeGroup(m) switch
    {
        0 => "best efficiency", 1 => "balanced", 2 => "best performance", _ => m.ToString("D"),
    };

    private static string EffectiveText(int mode) => mode switch
    {
        0 => "battery saver", 1 => "better battery", 2 => "balanced", 3 => "high performance",
        4 => "max performance", 5 => "game mode", 6 => "mixed reality", _ => "",
    };

    // ---------------- the hidden-in-Control-Panel attribute ----------------

    public static uint? ReadBoostAttributes()
    {
        try { return PowerReadSettingAttributes(ref _subProcessor, ref _perfBoost); }
        catch { return null; }
    }

    /// <summary>
    /// Modern Standby (S0 low-power idle, "AoAc" in SYSTEM_POWER_CAPABILITIES). On such a
    /// machine Power Options lists a processor setting only when it carries SHOW_AOAC, so
    /// clearing HIDE alone leaves it invisible (owner's GE78HX, 2026-10-20: attributes 0 =
    /// still hidden, 2 = shown). Read once; it cannot change while the app runs.
    /// </summary>
    public static bool ModernStandby => _aoac ??= ReadAoAc();
    private static bool? _aoac;

    private static bool ReadAoAc()
    {
        try
        {
            var caps = new byte[128];   // SYSTEM_POWER_CAPABILITIES is 76 bytes; AoAc is the BOOLEAN at offset 20
            return GetPwrCapabilities(caps) && caps[20] != 0;
        }
        catch { return false; }
    }

    /// <summary>Hidden the way Power Options decides it: the HIDE flag, or Modern Standby without SHOW_AOAC.</summary>
    public static bool HiddenInControlPanel()
    {
        uint a = ReadBoostAttributes() ?? AttribHide;
        return (a & AttribHide) != 0 || (ModernStandby && (a & AttribShowAoac) == 0);
    }

    /// <summary>
    /// Reveal/re-hide PERFBOOSTMODE in the Windows power options UI. Revealing stores the full
    /// original DWORD in settings, clears HIDE and, on Modern Standby, sets SHOW_AOAC;
    /// re-hiding writes the exact original back, so no other attribute bit is ever lost.
    /// </summary>
    public static bool SetRevealed(AppSettings s, bool reveal)
    {
        var attrs = ReadBoostAttributes();
        if (attrs == null) return false;
        try
        {
            if (reveal)
            {
                if (s.BoostAttrOriginal < 0) { s.BoostAttrOriginal = attrs.Value; s.Save(); }
                uint shown = attrs.Value & ~AttribHide;
                if (ModernStandby) shown |= AttribShowAoac;
                return PowerWriteSettingAttributes(ref _subProcessor, ref _perfBoost, shown) == 0;
            }
            uint back = s.BoostAttrOriginal >= 0 ? (uint)s.BoostAttrOriginal : attrs.Value | AttribHide;
            bool ok = PowerWriteSettingAttributes(ref _subProcessor, ref _perfBoost, back) == 0;
            if (ok) { s.BoostAttrOriginal = -1; s.Save(); }
            return ok;
        }
        catch { return false; }
    }

    // ---------------- Windows power mode (#36) ----------------

    /// <summary>Sets the user-configured power mode for BOTH power sources (the "vote";
    /// Windows may temporarily override it - see the effective watch below).</summary>
    public static bool TrySetPowerMode(Guid mode)
    {
        try
        {
            if (_setAcMode != null && _setDcMode != null)
            {
                var m = mode;
                return _setAcMode(ref m) == 0 & _setDcMode(ref m) == 0;
            }
            // Pre-Win11 fallback: the (undocumented, long-lived) overlay call.
            return _setOverlay != null && _setOverlay(mode) == 0;
        }
        catch { return false; }
    }

    public static bool TryGetUserPowerMode(out Guid ac, out Guid dc)
    {
        ac = dc = Guid.Empty;
        try { return _getAcMode != null && _getDcMode != null && _getAcMode(out ac) == 0 && _getDcMode(out dc) == 0; }
        catch { return false; }
    }

    /// <summary>
    /// Remember the user's own power mode ONCE, before GhostDeck's first mode write (Auto or
    /// a manual segment pick). "Restore Windows settings" puts it back - without this, Auto
    /// could leave the slider on best efficiency after restoring (owner report, 2026-10-04).
    /// Only a clean, both-sources-agree read is stored; an empty value means "never changed".
    /// </summary>
    public static void RememberModeIfFirst(AppSettings s)
    {
        if (s.PowerModePrev.Length > 0) return;
        if (!TryGetUserPowerMode(out var ac, out var dc) || ac != dc) return;
        s.PowerModePrev = ac.ToString("D");
        s.Save();
    }

    // Profile -> mode mapping. Groups: 0 = best efficiency, 1 = balanced, 2 = best
    // performance. The defaults below apply unless the owner edited the map on the card
    // (AppSettings.PowerModeMap holds only the rows that differ).
    public static int DefaultModeGroup(ProfileId id) => id switch
    {
        ProfileId.Silent or ProfileId.SuperBattery => 0,
        ProfileId.Extreme => 2,
        _ => 1,
    };

    public static int ModeGroupFor(AppSettings s, ProfileId id) =>
        s.PowerModeMap.TryGetValue(id.ToString(), out int g) && g is >= 0 and <= 2 ? g : DefaultModeGroup(id);

    public static Guid GroupMode(int group) =>
        group == 0 ? ModeBestEfficiency : group == 2 ? ModeBestPerformance : ModeBalanced;

    public static Guid ModeForProfile(AppSettings s, ProfileId id) => GroupMode(ModeGroupFor(s, id));

    /// <summary>
    /// Coarse equivalence groups (0 = efficiency, 1 = balanced, 2 = performance, -1 = unknown)
    /// so the requested mode and the RICHER effective enum can be compared for the card's
    /// "Windows is temporarily applying X" note. Never a 1:1 name comparison: MaxPerformance
    /// under a Best-Performance request is agreement, not an override.
    /// </summary>
    public static int ModeGroup(Guid m) =>
        m == ModeBestEfficiency ? 0 : m == ModeBalanced ? 1 : m == ModeBestPerformance ? 2 : -1;

    public static int EffectiveGroup(int mode) => mode switch
    {
        0 or 1 => 0,          // battery saver, better battery
        2 => 1,               // balanced
        3 or 4 or 5 => 2,     // high performance, max performance, game mode
        _ => -1,              // mixed reality / unknown: never claim an override
    };

    /// <summary>Lang key for one of the three requested modes.</summary>
    public static string ModeKey(Guid mode) =>
        mode == ModeBestEfficiency ? "pwm_req_eff" : mode == ModeBestPerformance ? "pwm_req_perf" : "pwm_req_bal";

    // ---------------- effective power mode (documented callback, Win10 1809+) ----------------

    /// <summary>-1 until the first callback (or when the API is missing).</summary>
    public static int EffectiveMode { get; private set; } = -1;
    public static event Action? EffectiveModeChanged;

    private static EffectiveCb? _effectiveCb;   // rooted so the GC never collects the thunk
    private static bool _watchStarted;

    public static void EnsureEffectiveWatch()
    {
        if (_watchStarted || _registerEffective == null) return;
        _watchStarted = true;
        try
        {
            _effectiveCb = (mode, _) =>
            {
                EffectiveMode = mode;
                try { EffectiveModeChanged?.Invoke(); } catch { }
            };
            // EFFECTIVE_POWER_MODE_V2 = 2; the callback also fires once right away with the
            // current state. The registration lives for the whole process - no unregister.
            _registerEffective(2, _effectiveCb, IntPtr.Zero, out _);
        }
        catch { }
    }

    /// <summary>Lang key for an EFFECTIVE_POWER_MODE value; "" when unknown.
    /// NOT the same value space as the three requested modes - never compare 1:1.</summary>
    public static string EffectiveKey(int mode) => mode switch
    {
        0 => "pwm_eff_saver", 1 => "pwm_eff_better", 2 => "pwm_eff_bal", 3 => "pwm_eff_high",
        4 => "pwm_eff_max", 5 => "pwm_eff_game", 6 => "pwm_eff_mr", _ => "",
    };
}
