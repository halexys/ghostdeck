namespace GhostDeck;

/// <summary>
/// Everything the Models tab shows besides the table, computed in one pass from the database
/// in effect (Devices.All). Rebuilt whenever the database changes, so every number, the chart,
/// the timeline, the series and the thank-you wall follow a model change with no hand edits.
/// </summary>
internal sealed class ModelsStats
{
    public int Total, Tested, Experimental, G1, G2, Curve, CurveVerified, Rpm, SuperBattery, Apex, Backup, CreditedModels;
    public readonly List<(string Name, int Models, string Url)> People = new();
    public readonly List<(string Name, int Tested, int Total)> Series = new();
    public readonly List<(DeviceProfile Model, string Reason)> Candidates = new();
    public readonly List<TimelineEvent> Timeline = new();          // newest first
    public readonly List<(DateTime Day, int Total, int Tested)> Growth = new();

    public sealed record TimelineEvent(DateTime Day, bool Tested, DeviceProfile? Model, int BatchCount, string Credit);

    public static string SeriesOf(DeviceProfile d)
    {
        string n = d.Name.StartsWith("MSI ", StringComparison.OrdinalIgnoreCase) ? d.Name[4..] : d.Name;
        int cut = n.IndexOfAny(new[] { ' ', '/' });
        return cut > 0 ? n[..cut] : n;
    }

    public static bool HasSuperBattery(DeviceProfile d) =>
        d.Recipes.TryGetValue(ProfileId.SuperBattery, out var r) && r.Any(x => x.val == 0x0F);
    public static bool HasRpm(DeviceProfile d) => d.CpuRpmAddr != 0 || d.CpuRpmAddr16 != 0;

    /// <summary>"A, B & C" -> logins, in order.</summary>
    public static List<string> Logins(string credit) =>
        credit.Split(new[] { ',', '&' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public static ModelsStats Build(IReadOnlyList<DeviceProfile> all)
    {
        var s = new ModelsStats { Total = all.Count };
        var people = new Dictionary<string, (int n, string url)>(StringComparer.OrdinalIgnoreCase);
        var series = new Dictionary<string, (int t, int n)>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in all)
        {
            bool tested = d.Tier == Tier.Tested;
            if (tested) s.Tested++; else s.Experimental++;
            if (d.ShiftMode == 0xF2) s.G1++; else s.G2++;
            if (d.FanCurve is { } fc) { s.Curve++; if (fc.Verified) s.CurveVerified++; }
            if (HasRpm(d)) s.Rpm++;
            if (HasSuperBattery(d)) s.SuperBattery++;
            if (d.FourthMode != null) s.Apex++;
            if (Devices.BlockPathOf(d) != null) s.Backup++;
            if (d.Credit.Length > 0) s.CreditedModels++;
            foreach (var login in Logins(d.Credit))
                people[login] = people.TryGetValue(login, out var p) ? (p.n + 1, p.url) : (1, d.CreditUrl);
            string ser = SeriesOf(d);
            series[ser] = series.TryGetValue(ser, out var c) ? (c.t + (tested ? 1 : 0), c.n + 1) : (tested ? 1 : 0, 1);

            if (!tested)
            {
                string? why =
                    d.FanCurve is { Verified: true } ? Lang.T("mdl_r_curve")
                    : Devices.BlockPathOf(d) != null ? Lang.T("mdl_r_backup")
                    : d.Credit.Length > 0 ? Lang.T("mdl_r_credit")
                    : d.FourthMode != null ? Lang.T("mdl_r_apex")
                    : null;
                if (why != null) s.Candidates.Add((d, why));
            }
        }
        s.People.AddRange(people.OrderByDescending(p => p.Value.n).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                                .Select(p => (p.Key, p.Value.n, p.Value.url)));
        s.Series.AddRange(series.OrderByDescending(p => p.Value.t).ThenByDescending(p => p.Value.n).ThenBy(p => p.Key)
                                .Select(p => (p.Key, p.Value.t, p.Value.n)));

        // Timeline: one event per promotion; additions on a day with more than three new models
        // (the initial import of the register maps) collapse into one line.
        var added = all.Select(d => (d, day: ModelHistory.Date(d.Added))).Where(x => x.day != null).GroupBy(x => x.day!.Value);
        foreach (var g in added)
        {
            var list = g.ToList();
            if (list.Count > 3) s.Timeline.Add(new TimelineEvent(g.Key, false, null, list.Count, ""));
            else foreach (var x in list) s.Timeline.Add(new TimelineEvent(g.Key, false, x.d, 0, ""));
        }
        foreach (var d in all)
            if (ModelHistory.Date(d.TestedOn) is { } day)
                s.Timeline.Add(new TimelineEvent(day, true, d, 0, d.Credit));
        s.Timeline.Sort((a, b) => b.Day != a.Day ? b.Day.CompareTo(a.Day) : b.Tested.CompareTo(a.Tested));

        // Growth: cumulative counts on every day something changed (dates parsed once, one sweep).
        var adds = all.Select(d => ModelHistory.Date(d.Added)).Where(x => x != null).Select(x => x!.Value).OrderBy(x => x).ToList();
        var tests = all.Select(d => ModelHistory.Date(d.TestedOn)).Where(x => x != null).Select(x => x!.Value).OrderBy(x => x).ToList();
        int ia = 0, it = 0;
        foreach (var day in adds.Concat(tests).Distinct().OrderBy(x => x))
        {
            while (ia < adds.Count && adds[ia] <= day) ia++;
            while (it < tests.Count && tests[it] <= day) it++;
            s.Growth.Add((day, ia, it));
        }
        return s;
    }

    /// <summary>Short date in the UI language (falls back to ISO).</summary>
    public static string Day(DateTime d)
    {
        try { return d.ToString("d", System.Globalization.CultureInfo.GetCultureInfo(Lang.CurrentCode)); }
        catch { return d.ToString("yyyy-MM-dd"); }
    }
}
