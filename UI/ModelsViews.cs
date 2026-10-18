using System.Drawing.Drawing2D;

namespace GhostDeck;

/// <summary>
/// Base of the Models tab views: one drawn surface per sub-tab, hosted in the page's scrolling
/// panel (WinForms moves the child, so TextRenderer is fine here). A view lays itself out and
/// paints in the same Render pass (g == null = measure only), registers click zones while
/// painting, and reads everything from ModelsPage, which rebuilds its data on every database
/// change.
/// </summary>
internal abstract class ModelsView : Control
{
    protected readonly ModelsPage P;
    private readonly List<(Rectangle R, Action A, string? Tip)> _zones = new();
    private string? _tipShown;

    protected ModelsView(ModelsPage p)
    {
        P = p;
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Theme.Surface;
    }

    protected int S(float v) => (int)Math.Ceiling(v * DeviceDpi / 96f);
    protected const int Pad = ModelsPage.Pad;

    public int ContentHeight(int width) => Render(null, width, Rectangle.Empty);

    /// <summary>Lay out and (when g != null) paint; returns the content height.</summary>
    protected abstract int Render(Graphics? g, int width, Rectangle clip);

    protected void Zone(Graphics? g, Rectangle r, Action a, string? tip = null) { if (g != null) _zones.Add((r, a, tip)); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Surface);
        _zones.Clear();
        Render(g, Width, e.ClipRectangle);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = _zones.LastOrDefault(z => z.R.Contains(e.Location));
        Cursor = hit.A != null ? Cursors.Hand : Cursors.Default;
        string? tip = hit.A != null ? hit.Tip : null;
        if (tip != _tipShown) { _tipShown = tip; P.Tip.SetToolTip(this, tip ?? ""); }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        for (int i = _zones.Count - 1; i >= 0; i--)
            if (_zones[i].R.Contains(e.Location)) { _zones[i].A(); return; }
    }

    // ---------- drawing helpers (all no-ops when g == null) ----------
    protected static void T(Graphics? g, string text, Font f, Rectangle r, Color c, TextFormatFlags flags)
    {
        if (g != null) TextRenderer.DrawText(g, text, f, r, c, flags);
    }

    /// <summary>Wrapped text at (x, y) in width w; returns its height.</summary>
    protected static int Para(Graphics? g, string text, Font f, int x, int y, int w, Color c)
    {
        var (t, h) = ModelsText.Fit(text, f, w);
        T(g, t, f, new Rectangle(x, y, w, h + 2), c, ModelsText.Wrap);
        return h;
    }

    protected int CardHeadH() => Math.Max(S(26), ModelsPage.FCardTitle.Height) + S(16) + S(12);

    /// <summary>Card background + Settings-style icon box and title; returns the content top.</summary>
    protected int Card(Graphics? g, Rectangle r, string glyph, string title, string? sub = null)
    {
        int isz = S(26);
        int top = r.Y + S(16);
        if (g != null)
        {
            using (var b = new SolidBrush(Theme.Card))
            using (var path = Theme.RoundRect(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), 10))
            {
                g.FillPath(b, path);
                using var pen = new Pen(Theme.Border);
                g.DrawPath(pen, path);
            }
            var ir = new Rectangle(r.X + S(18), top, isz, isz);
            using (var ap = new Pen(Theme.Accent, 1.7f))
            using (var ip = Theme.RoundRect(new RectangleF(ir.X + 0.5f, ir.Y + 0.5f, ir.Width - 1, ir.Height - 1), 6))
                g.DrawPath(ap, ip);
            Ui.CenterGlyph(g, glyph, ModelsPage.FGlyph, Theme.Accent, ir);
        }
        int tx = r.X + S(18) + isz + S(10);
        int tw = r.Right - S(18) - tx;
        if (sub == null)
            T(g, title.ToUpperInvariant(), ModelsPage.FCardTitle, new Rectangle(tx, top, tw, isz), Theme.Accent, ModelsText.Line);
        else
        {
            int h1 = ModelsPage.FCardTitle.Height;
            T(g, title.ToUpperInvariant(), ModelsPage.FCardTitle, new Rectangle(tx, top - S(3), tw, h1), Theme.Accent, ModelsText.Line);
            T(g, sub, ModelsPage.FSmall, new Rectangle(tx, top - S(3) + h1, tw, ModelsPage.FSmall.Height + 2), Theme.Faint, ModelsText.Line);
        }
        return top + isz + S(12);
    }

    /// <summary>A drawn button; returns its width.</summary>
    protected int Button(Graphics? g, int x, int y, string text, bool primary, Action click)
    {
        var f = ModelsPage.FBold;
        int w = ModelsText.NaturalW(text, f) + S(24), h = ButtonH();
        var r = new Rectangle(x, y, w, h);
        if (g != null)
        {
            using var path = Theme.RoundRect(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), 6);
            if (primary) { using var b = new SolidBrush(Theme.AccentFill); g.FillPath(b, path); }
            else { using var pen = new Pen(Theme.BorderStrong); g.DrawPath(pen, path); }
            T(g, text, f, r, primary ? Theme.AccentText : Theme.Text,
              TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
        Zone(g, r, click);
        return w;
    }
    protected int ButtonH() => ModelsPage.FBold.Height + S(14);

    /// <summary>Rounded chip; returns its width. on = filled accent.</summary>
    protected int Chip(Graphics? g, int x, int y, string text, Color fg, bool on, bool filled = false, Color? textColor = null)
    {
        var f = ModelsPage.FSmall;
        int w = ModelsText.NaturalW(text, f) + S(16), h = ChipH();
        if (g != null)
        {
            var r = new RectangleF(x + 0.5f, y + 0.5f, w - 1, h - 1);
            using var path = Theme.RoundRect(r, h / 2f - 1);
            if (filled) { using var b = new SolidBrush(Theme.AccentFill); g.FillPath(b, path); }
            else if (on) { using var b = new SolidBrush(Color.FromArgb(26, fg)); g.FillPath(b, path); }
            using var pen = new Pen(filled ? Theme.AccentFill : on ? Color.FromArgb(170, fg) : Theme.Border);
            g.DrawPath(pen, path);
            T(g, text, f, Rectangle.Round(r), textColor ?? (filled ? Theme.AccentText : on ? fg : Theme.Muted),
              TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
        return w;
    }
    protected int ChipH() => ModelsPage.FSmall.Height + S(8);

    protected static string Short(DeviceProfile d) => d.Name.StartsWith("MSI ", StringComparison.OrdinalIgnoreCase) ? d.Name[4..] : d.Name;
    protected static string IssueNo(string url)
    {
        int i = url.LastIndexOf('/');
        return i >= 0 && i + 1 < url.Length && url[(i + 1)..].All(char.IsDigit) ? "#" + url[(i + 1)..] : "";
    }

    /// <summary>Two columns when the view is wide enough, else one; returns (x, w) per column.</summary>
    protected (int x1, int w1, int x2, int w2, bool two) Columns(int width)
    {
        int inner = width - Pad * 2, gap = S(16);
        if (inner >= S(780)) { int cw = (inner - gap) / 2; return (Pad, cw, Pad + cw + gap, inner - cw - gap, true); }
        return (Pad, inner, Pad, inner, false);
    }

    /// <summary>One timeline line ("date  text") used by Start and Community.</summary>
    protected int Event(Graphics? g, ModelsStats.TimelineEvent ev, int x, int y, int w)
    {
        int dw = ModelsText.NaturalW("00.00.0000", ModelsPage.FMono) + S(4);
        T(g, ModelsStats.Day(ev.Day), ModelsPage.FSmall, new Rectangle(x, y, dw, ModelsPage.FCell.Height), Theme.Faint,
          TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        int tx = x + dw + S(8), tw = w - dw - S(8);
        string text = ev.Model == null
            ? string.Format(Lang.T("mdl_ev_batch"), ev.BatchCount)
            : string.Format(Lang.T(ev.Tested ? "mdl_ev_tested" : "mdl_ev_added"), Short(ev.Model));
        var dot = ev.Model == null ? Theme.Faint : ev.Tested ? Theme.Green : Theme.Accent;
        if (g != null)
        {
            using var b = new SolidBrush(dot);
            int ds = S(8);
            g.FillEllipse(b, tx - S(6) - ds / 2, y + ModelsText.LineH(ModelsPage.FCell) / 2 - ds / 2, ds, ds);
        }
        tx += S(6); tw -= S(6);
        int h = Para(g, text, ModelsPage.FCell, tx, y, tw, Theme.Text);
        if (ev.Tested && ev.Credit.Length > 0)
            h += Para(g, "@" + string.Join(", @", ModelsStats.Logins(ev.Credit)), ModelsPage.FSmall, tx, y + h, tw, Theme.Accent);
        if (ev.Model != null) Zone(g, new Rectangle(x, y, w, h), () => P.ShowListFor(ev.Model.FirmwarePrefixes[0], ev.Model));
        return h;
    }
}

// =====================================================================
//  Start: your laptop, closest to tested | numbers, growth, recent
// =====================================================================
internal sealed class ModelsStartView : ModelsView
{
    public ModelsStartView(ModelsPage p) : base(p) { }

    private const string BackupGuide = "https://github.com/wygodad/ghostdeck/blob/main/docs/BACKUP-PATH.md";

    protected override int Render(Graphics? g, int width, Rectangle clip)
    {
        var (x1, w1, x2, w2, two) = Columns(width);
        int gap = S(16);
        int y1 = S(4), y2 = S(4);
        y1 += YouCard(g, x1, y1, w1) + gap;
        if (!two) y2 = y1;
        y2 += NumbersCard(g, x2, y2, w2) + gap;
        if (!two) y1 = y2;
        y2 += GrowthCard(g, x2, y2, w2) + gap;
        if (!two) y1 = y2;
        y1 += CandidatesCard(g, x1, y1, w1) + gap;
        if (!two) y2 = y1;
        y2 += RecentCard(g, x2, y2, w2) + gap;
        return Math.Max(y1, y2) + S(8);
    }

    // Measure first (g == null), then paint at the measured height: a card's frame needs its height.
    private int Measured(Graphics? g, int x, int y, int w, Func<Graphics?, Rectangle, int> body)
    {
        int h = body(null, new Rectangle(x, y, w, 0));
        if (g != null) body(g, new Rectangle(x, y, w, h));
        return h;
    }

    private int YouCard(Graphics? g, int x, int y, int w) => Measured(g, x, y, w, (gg, r) =>
    {
        var d = P.Detected;
        int cy = Card(gg, r, "", Lang.T("mdl_card_you"));
        int ix = r.X + S(18), iw = r.Width - S(36);
        if (d == null)
        {
            cy += Para(gg, Lang.T("mdl_notin_title"), ModelsPage.FName, ix, cy, iw, Theme.Amber) + S(6);
            cy += Para(gg, string.Format(Lang.T("mdl_notin_body"), string.IsNullOrEmpty(P.Firmware) ? "?" : P.Firmware),
                       ModelsPage.FCell, ix, cy, iw, Theme.Muted) + S(12);
            int bx = ix;
            bx += Button(gg, bx, cy, Lang.T("mdl_btn_report"), true, P.Verify) + S(10);
            if (bx + S(200) > ix + iw) { bx = ix; cy += ButtonH() + S(8); }
            Button(gg, bx, cy, Lang.T("mdl_btn_backup"), false, () => ModelsPage.OpenUrl(BackupGuide));
            cy += ButtonH() + S(16);
            return cy - r.Y;
        }
        bool tested = d.Tier == Tier.Tested;
        cy += Para(gg, Short(d), ModelsPage.FName, ix, cy, iw, Theme.Text) + S(2);
        cy += Para(gg, string.Join(", ", d.FirmwarePrefixes) + "   ·   " + (d.ShiftMode == 0xF2 ? "G1" : "G2"),
                   ModelsPage.FSmall, ix, cy, iw, Theme.Faint) + S(8);
        // status chip
        Chip(gg, ix, cy, Lang.T(tested ? "tier_tested" : "tier_experimental"), tested ? Theme.Green : Theme.Amber, true);
        cy += ChipH() + S(10);
        // what works: one chip per feature, lit when the database has it
        var feats = new List<(string, bool, Color)>
        {
            (Lang.T("mdl_f_profiles"), true, Theme.Green),
            (Lang.T("mdl_f_curve"), d.FanCurve != null, d.FanCurve is { Verified: true } ? Theme.Green : Theme.Accent),
            (Lang.T("mdl_f_rpm"), ModelsStats.HasRpm(d), Theme.Green),
            (Lang.T("mdl_f_sb"), ModelsStats.HasSuperBattery(d), Theme.Green),
        };
        if (d.FourthMode != null) feats.Add((Lang.T("mdl_f_apex"), true, Theme.Green));
        if (Devices.BlockPathOf(d) != null) feats.Add((Lang.T("mdl_f_backup"), true, Theme.Green));
        int fx = ix;
        foreach (var (name, on, col) in feats)
        {
            string label = (on ? "✓ " : "– ") + name;
            int cw = ModelsText.NaturalW(label, ModelsPage.FSmall) + S(16);
            if (fx + cw > ix + iw) { fx = ix; cy += ChipH() + S(6); }
            fx += Chip(gg, fx, cy, label, col, on) + S(6);
        }
        cy += ChipH() + S(10);
        if (d.Credit.Length > 0)
        {
            string who = Lang.T("mdl_tested_by") + ": @" + string.Join(", @", ModelsStats.Logins(d.Credit));
            string no = IssueNo(d.CreditUrl);
            if (no.Length > 0) who += "   ·   " + string.Format(Lang.T("mdl_issue"), no);
            int h = Para(gg, who, ModelsPage.FSmall, ix, cy, iw, Theme.Accent);
            Zone(gg, new Rectangle(ix, cy, iw, h), () => ModelsPage.OpenUrl(d.CreditUrl));
            cy += h + S(8);
        }
        if (!tested)
        {
            cy += Para(gg, Lang.T("mdl_exp_body"), ModelsPage.FSmall, ix, cy, iw, Theme.Muted) + S(10);
            Button(gg, ix, cy, Lang.T("models_verify_btn"), true, P.Verify);
            cy += ButtonH() + S(6);
        }
        return cy - r.Y + S(10);
    });

    private int NumbersCard(Graphics? g, int x, int y, int w) => Measured(g, x, y, w, (gg, r) =>
    {
        var s = P.Stats;
        int cy = Card(gg, r, "", Lang.T("mdl_card_numbers"), Lang.T("mdl_card_numbers_sub"));
        var tiles = new (int n, string label, Color c, Action go)[]
        {
            (s.Total, Lang.T("mdl_n_models"), Theme.Text, () => P.ShowListFor("")),
            (s.Tested, Lang.T("mdl_n_tested"), Theme.Green, () => P.ShowListFor("", null, "t")),
            (s.Experimental, Lang.T("mdl_n_exp"), Theme.Amber, () => P.ShowListFor("", null, "e")),
            (s.Curve, Lang.T("mdl_n_curve"), Theme.Text, () => P.ShowTab(ModelsPage.TabFeatures)),
            (s.Rpm, Lang.T("mdl_n_rpm"), Theme.Text, () => P.ShowTab(ModelsPage.TabFeatures)),
            (s.People.Count, Lang.T("mdl_n_people"), Theme.Accent, () => P.ShowTab(ModelsPage.TabCommunity)),
        };
        int ix = r.X + S(18), iw = r.Width - S(36), gap = S(10);
        int cols = iw >= S(420) ? 3 : 2;
        int tw = (iw - gap * (cols - 1)) / cols;
        // tile height = number + the tallest label in its row
        int rowY = cy;
        for (int i = 0; i < tiles.Length; i += cols)
        {
            int th = 0;
            for (int k = i; k < Math.Min(i + cols, tiles.Length); k++)
                th = Math.Max(th, ModelsText.Fit(tiles[k].label, ModelsPage.FSmall, tw - S(20)).H);
            int h = S(10) + ModelsText.LineH(ModelsPage.FBig) + th + S(10);
            for (int k = i; k < Math.Min(i + cols, tiles.Length); k++)
            {
                var tr = new Rectangle(ix + (k - i) * (tw + gap), rowY, tw, h);
                if (gg != null)
                {
                    using var path = Theme.RoundRect(new RectangleF(tr.X + 0.5f, tr.Y + 0.5f, tr.Width - 1, tr.Height - 1), 7);
                    using var b = new SolidBrush(Theme.Surface); gg.FillPath(b, path);
                    using var pen = new Pen(Theme.Border); gg.DrawPath(pen, path);
                }
                T(gg, tiles[k].n.ToString(), ModelsPage.FBig, new Rectangle(tr.X + S(10), tr.Y + S(8), tw - S(20), ModelsText.LineH(ModelsPage.FBig)),
                  tiles[k].c, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix);
                Para(gg, tiles[k].label, ModelsPage.FSmall, tr.X + S(10), tr.Y + S(10) + ModelsText.LineH(ModelsPage.FBig), tw - S(20), Theme.Muted);
                var go = tiles[k].go;
                Zone(gg, tr, go);
            }
            rowY += h + gap;
        }
        return rowY - gap + S(18) - r.Y;
    });

    private int GrowthCard(Graphics? g, int x, int y, int w) => Measured(g, x, y, w, (gg, r) =>
    {
        var s = P.Stats;
        int cy = Card(gg, r, "", Lang.T("mdl_card_growth"),
                      string.Format(Lang.T("mdl_growth_sub"), s.Growth.Count > 0 ? ModelsStats.Day(s.Growth[0].Day) : "-"));
        int ix = r.X + S(18), iw = r.Width - S(36);
        int ch = S(170);
        if (gg != null && s.Growth.Count > 1) DrawChart(gg, new Rectangle(ix, cy, iw, ch), s);
        cy += ch + S(8);
        // legend
        int lx = ix;
        lx += LegendItem(gg, lx, cy, Theme.Green, string.Format(Lang.T("mdl_growth_tested"), s.Tested), false) + S(18);
        LegendItem(gg, lx, cy, Theme.Accent, string.Format(Lang.T("mdl_growth_total"), s.Total), true);
        cy += ModelsPage.FSmall.Height + S(16);
        return cy - r.Y;
    });

    private int LegendItem(Graphics? g, int x, int y, Color c, string text, bool dashed)
    {
        int lw = S(18), h = ModelsPage.FSmall.Height;
        if (g != null)
        {
            using var pen = new Pen(c, S(2)) { DashStyle = dashed ? DashStyle.Dash : DashStyle.Solid };
            g.DrawLine(pen, x, y + h / 2, x + lw, y + h / 2);
        }
        T(g, text, ModelsPage.FSmall, new Rectangle(x + lw + S(6), y, ModelsText.NaturalW(text, ModelsPage.FSmall) + 4, h), Theme.Muted,
          TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        return lw + S(6) + ModelsText.NaturalW(text, ModelsPage.FSmall);
    }

    private void DrawChart(Graphics g, Rectangle area, ModelsStats s)
    {
        var f = ModelsPage.FSmall;
        int axisW = ModelsText.NaturalW("000", f) + S(6), axisH = f.Height + S(4);
        var plot = new Rectangle(area.X + axisW, area.Y + S(4), area.Width - axisW - S(8), area.Height - axisH - S(4));
        DateTime t0 = s.Growth[0].Day, t1 = DateTime.Today > s.Growth[^1].Day ? DateTime.Today : s.Growth[^1].Day;
        double span = Math.Max(1, (t1 - t0).TotalDays);
        int max = Math.Max(10, s.Growth.Max(p => p.Total));
        int step = max <= 50 ? 10 : max <= 100 ? 25 : 50;
        int top = (max + step - 1) / step * step;
        float X(DateTime d) => plot.X + (float)((d - t0).TotalDays / span) * plot.Width;
        float Y(int v) => plot.Bottom - v / (float)top * plot.Height;

        using (var grid = new Pen(Theme.Border))
            for (int v = 0; v <= top; v += step)
            {
                g.DrawLine(grid, plot.X, Y(v), plot.Right, Y(v));
                TextRenderer.DrawText(g, v.ToString(), f, new Rectangle(area.X, (int)Y(v) - f.Height / 2, axisW - S(6), f.Height), Theme.Faint,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        // month ticks
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        try { culture = System.Globalization.CultureInfo.GetCultureInfo(Lang.CurrentCode); } catch { }
        for (var m = new DateTime(t0.Year, t0.Month, 1).AddMonths(1); m <= t1; m = m.AddMonths(1))
        {
            float mx = X(m);
            string lab = m.ToString("MMM", culture);
            int lw = ModelsText.NaturalW(lab, f);
            TextRenderer.DrawText(g, lab, f, new Rectangle((int)mx - lw / 2 - 2, plot.Bottom + S(4), lw + 4, f.Height), Theme.Faint,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPrefix);
        }
        // step lines: recognised (dashed accent) and tested (green with a soft area)
        PointF[] Steps(Func<(DateTime Day, int Total, int Tested), int> pick)
        {
            var pts = new List<PointF>();
            int last = 0;
            foreach (var p in s.Growth)
            {
                float px = X(p.Day);
                if (pts.Count > 0) pts.Add(new PointF(px, Y(last)));
                last = pick(p);
                pts.Add(new PointF(px, Y(last)));
            }
            pts.Add(new PointF(plot.Right, Y(last)));
            return pts.ToArray();
        }
        var tested = Steps(p => p.Tested);
        using (var area2 = new GraphicsPath())
        {
            area2.AddLines(tested);
            area2.AddLine(plot.Right, plot.Bottom, tested[0].X, plot.Bottom);
            area2.CloseFigure();
            using var fill = new SolidBrush(Color.FromArgb(34, Theme.Green));
            g.FillPath(fill, area2);
        }
        using (var pen = new Pen(Theme.Accent, S(2)) { DashStyle = DashStyle.Dash }) g.DrawLines(pen, Steps(p => p.Total));
        using (var pen = new Pen(Theme.Green, S(2))) g.DrawLines(pen, tested);
        var end = tested[^1];
        using (var b = new SolidBrush(Theme.Green)) g.FillEllipse(b, end.X - S(4), end.Y - S(4), S(8), S(8));
    }

    private int CandidatesCard(Graphics? g, int x, int y, int w) => Measured(g, x, y, w, (gg, r) =>
    {
        var list = P.Stats.Candidates;
        int cy = Card(gg, r, "", Lang.T("mdl_card_close"), Lang.T("mdl_card_close_sub"));
        int ix = r.X + S(18), iw = r.Width - S(36);
        if (list.Count == 0) { cy += Para(gg, "—", ModelsPage.FCell, ix, cy, iw, Theme.Muted) + S(16); return cy - r.Y; }
        foreach (var (m, why) in list)
        {
            int top = cy;
            int h = Para(gg, Short(m), ModelsPage.FCell, ix + S(10), cy + S(8), iw - S(20), Theme.Text);
            h += Para(gg, why + "   ·   " + Lang.T("mdl_close_need"), ModelsPage.FSmall, ix + S(10), cy + S(8) + h, iw - S(20), Theme.Faint);
            var rr = new Rectangle(ix, top, iw, h + S(16));
            if (gg != null)
            {
                using var path = Theme.RoundRect(new RectangleF(rr.X + 0.5f, rr.Y + 0.5f, rr.Width - 1, rr.Height - 1), 7);
                using var pen = new Pen(Theme.Border); gg.DrawPath(pen, path);
            }
            Zone(gg, rr, () => P.ShowListFor(m.FirmwarePrefixes[0], m));
            cy += rr.Height + S(8);
        }
        return cy - r.Y + S(10);
    });

    private int RecentCard(Graphics? g, int x, int y, int w) => Measured(g, x, y, w, (gg, r) =>
    {
        int cy = Card(gg, r, "", Lang.T("mdl_card_recent"));
        int ix = r.X + S(18), iw = r.Width - S(36);
        foreach (var ev in P.Stats.Timeline.Take(5))
            cy += Event(gg, ev, ix, cy, iw) + S(8);
        string more = Lang.T("mdl_all_timeline");
        int mh = ModelsPage.FSmall.Height + 2;
        T(gg, more, ModelsPage.FSmall, new Rectangle(ix, cy, iw, mh), Theme.Accent, ModelsText.Line);
        Zone(gg, new Rectangle(ix, cy, ModelsText.NaturalW(more, ModelsPage.FSmall), mh), () => P.ShowTab(ModelsPage.TabCommunity));
        cy += mh + S(16);
        return cy - r.Y;
    });
}

// =====================================================================
//  Features: coverage tiles + dot matrix
// =====================================================================
internal sealed class ModelsFeaturesView : ModelsView
{
    public ModelsFeaturesView(ModelsPage p) : base(p) { }

    private static string[] Cols() => new[]
    {
        Lang.T("mdl_f_profiles"), Lang.T("mdl_f_curve"), Lang.T("mdl_f_rpm"), Lang.T("mdl_f_sb"),
        Lang.T("mdl_f_apex"), Lang.T("mdl_f_backup"),
    };

    protected override int Render(Graphics? g, int width, Rectangle clip)
    {
        var s = P.Stats;
        int x = Pad, w = width - Pad * 2, gap = S(10);
        int y = S(4);
        // coverage tiles
        var tiles = new (int n, string label)[]
        {
            (s.Total, Lang.T("mdl_f_profiles")), (s.Curve, Lang.T("mdl_f_curve")), (s.CurveVerified, Lang.T("mdl_f_curve_ok")),
            (s.SuperBattery, Lang.T("mdl_f_sb")), (s.Rpm, Lang.T("mdl_f_rpm")), (s.Apex, Lang.T("mdl_f_apex")),
            (s.Backup, Lang.T("mdl_f_backup")),
        };
        int cols = w >= S(900) ? 7 : w >= S(560) ? 4 : 2;
        int tw = (w - gap * (cols - 1)) / cols;
        for (int i = 0; i < tiles.Length; i += cols)
        {
            int lh = 0;
            for (int k = i; k < Math.Min(i + cols, tiles.Length); k++) lh = Math.Max(lh, ModelsText.Fit(tiles[k].label, ModelsPage.FSmall, tw - S(20)).H);
            int th = S(10) + ModelsText.LineH(ModelsPage.FBig) + lh + S(8) + S(6) + S(10);
            for (int k = i; k < Math.Min(i + cols, tiles.Length); k++)
            {
                var r = new Rectangle(x + (k - i) * (tw + gap), y, tw, th);
                if (g != null)
                {
                    using var path = Theme.RoundRect(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), 8);
                    using var b = new SolidBrush(Theme.Card); g.FillPath(b, path);
                    using var pen = new Pen(Theme.Border); g.DrawPath(pen, path);
                    // coverage bar
                    var bar = new Rectangle(r.X + S(10), r.Bottom - S(14), r.Width - S(20), S(5));
                    using var bb = new SolidBrush(Theme.Surface); g.FillRectangle(bb, bar);
                    using var fb = new SolidBrush(Theme.AccentFill);
                    g.FillRectangle(fb, bar.X, bar.Y, (int)(bar.Width * (s.Total == 0 ? 0 : tiles[k].n / (float)s.Total)), bar.Height);
                }
                T(g, tiles[k].n.ToString(), ModelsPage.FBig, new Rectangle(r.X + S(10), r.Y + S(8), tw - S(20), ModelsText.LineH(ModelsPage.FBig)),
                  Theme.Text, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix);
                Para(g, tiles[k].label, ModelsPage.FSmall, r.X + S(10), r.Y + S(10) + ModelsText.LineH(ModelsPage.FBig), tw - S(20), Theme.Muted);
            }
            y += th + gap;
        }
        y += S(8);

        // matrix: model name (wraps) + six dot columns
        var rows = P.Rows;
        var heads = Cols();
        // dot columns as wide as their longest WORD (headers wrap onto two lines), the name gets the rest
        int dotW = Math.Max(S(70), heads.SelectMany(h => h.Split(' ')).Max(word => ModelsText.NaturalW(word, ModelsPage.FHead)) + S(16));
        int nameW = w - S(36) - dotW * heads.Length;
        if (nameW < S(220)) { dotW = Math.Max(S(44), (w - S(36) - S(220)) / heads.Length); nameW = w - S(36) - dotW * heads.Length; }
        int headH = ModelsText.LineH(ModelsPage.FHead) * 2 + S(14);
        int tableTop = y;
        int lineH = ModelsText.LineH(ModelsPage.FCell), rowPad = S(7);
        // measure rows
        var hs = new int[rows.Count];
        var names = new string[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            var (t, h) = ModelsText.Fit(Short(rows[i]), ModelsPage.FCell, nameW);
            names[i] = t; hs[i] = Math.Max(lineH, h) + rowPad * 2;
        }
        int tableH = headH + hs.Sum() + S(10);
        if (g != null)
        {
            Ui.FillCard(g, new RectangleF(x, tableTop, w, tableH));
            int hx = x + S(18) + nameW;
            T(g, Lang.T("st_model"), ModelsPage.FHead, new Rectangle(x + S(18), tableTop, nameW, headH), Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            for (int c = 0; c < heads.Length; c++)
                T(g, heads[c], ModelsPage.FHead, new Rectangle(hx + c * dotW, tableTop + S(4), dotW, headH - S(8)), Theme.Muted,
                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            using var pen = new Pen(Theme.Border);
            g.DrawLine(pen, x + S(14), tableTop + headH, x + w - S(14), tableTop + headH);
        }
        int ry = tableTop + headH;
        for (int i = 0; i < rows.Count; i++)
        {
            var m = rows[i];
            int h = hs[i];
            if (g != null && ry < clip.Bottom && ry + h > clip.Top)
            {
                if (ReferenceEquals(m, P.Detected)) { using var b = new SolidBrush(Theme.AccentSoft); g.FillRectangle(b, x + 8, ry, w - 16, h); }
                else if (i % 2 == 1) { using var b = new SolidBrush(Theme.RowAlt); g.FillRectangle(b, x + 8, ry, w - 16, h); }
                T(g, names[i], ModelsPage.FCell, new Rectangle(x + S(18), ry + rowPad, nameW, h - rowPad), Theme.Text, ModelsText.Wrap);
                var states = new[]
                {
                    1,
                    m.FanCurve is { Verified: true } ? 1 : m.FanCurve != null ? 2 : 0,
                    ModelsStats.HasRpm(m) ? 1 : 0,
                    ModelsStats.HasSuperBattery(m) ? 1 : 0,
                    m.FourthMode != null ? 1 : 0,
                    Devices.BlockPathOf(m) != null ? 1 : 0,
                };
                int hx = x + S(18) + nameW, ds = S(12);
                int cyDot = ry + rowPad + lineH / 2;
                for (int c = 0; c < states.Length; c++)
                {
                    var dr = new RectangleF(hx + c * dotW + dotW / 2f - ds / 2f, cyDot - ds / 2f, ds, ds);
                    if (states[c] == 1) { using var b = new SolidBrush(Theme.Green); g.FillEllipse(b, dr); }
                    else if (states[c] == 2)
                    {
                        using var pen = new Pen(Theme.Accent, 1.6f); g.DrawEllipse(pen, dr);
                        using var b = new SolidBrush(Theme.Accent); g.FillEllipse(b, RectangleF.Inflate(dr, -ds * 0.3f, -ds * 0.3f));
                    }
                    else { using var pen = new Pen(Theme.BorderStrong, 1.4f); g.DrawEllipse(pen, dr); }
                }
            }
            var mm = m;
            Zone(g, new Rectangle(x + 8, ry, w - 16, h), () => P.ShowListFor(mm.FirmwarePrefixes[0], mm));
            ry += h;
        }
        if (rows.Count == 0) T(g, "—", ModelsPage.FCell, new Rectangle(x + S(18), ry, w, lineH + rowPad * 2), Theme.Muted, ModelsText.Line);
        return tableTop + tableH + S(14);
    }
}

// =====================================================================
//  Series: tested share per series
// =====================================================================
internal sealed class ModelsSeriesView : ModelsView
{
    public ModelsSeriesView(ModelsPage p) : base(p) { }

    protected override int Render(Graphics? g, int width, Rectangle clip)
    {
        int x = Pad, w = width - Pad * 2, gap = S(12);
        int y = S(4);
        y += Para(g, Lang.T("mdl_series_intro"), ModelsPage.FCell, x, y, w, Theme.Muted) + S(14);
        int min = S(210);
        int cols = Math.Max(1, (w + gap) / (min + gap));
        int cw = (w - gap * (cols - 1)) / cols;
        int ch = S(14) + ModelsText.LineH(ModelsPage.FBold) + S(8) + S(7) + S(8) + ModelsText.LineH(ModelsPage.FSmall) + S(14);
        var list = P.Stats.Series;
        for (int i = 0; i < list.Count; i++)
        {
            var (name, tested, total) = list[i];
            var r = new Rectangle(x + i % cols * (cw + gap), y + i / cols * (ch + gap), cw, ch);
            if (g != null && r.Bottom >= clip.Top && r.Top <= clip.Bottom)
            {
                using (var path = Theme.RoundRect(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), 8))
                {
                    using var b = new SolidBrush(Theme.Card); g.FillPath(b, path);
                    using var pen = new Pen(Theme.Border); g.DrawPath(pen, path);
                }
                int iy = r.Y + S(14);
                T(g, name, ModelsPage.FBold, new Rectangle(r.X + S(14), iy, cw - S(28), ModelsText.LineH(ModelsPage.FBold)), Theme.Text, ModelsText.Line);
                iy += ModelsText.LineH(ModelsPage.FBold) + S(8);
                var bar = new Rectangle(r.X + S(14), iy, cw - S(28), S(7));
                using (var bb = new SolidBrush(Theme.Surface)) g.FillRectangle(bb, bar);
                using (var fb = new SolidBrush(Theme.Green)) g.FillRectangle(fb, bar.X, bar.Y, (int)(bar.Width * tested / (float)Math.Max(1, total)), bar.Height);
                iy += S(7) + S(8);
                T(g, string.Format(Lang.T("mdl_series_count"), tested, total), ModelsPage.FSmall,
                  new Rectangle(r.X + S(14), iy, cw - S(28), ModelsText.LineH(ModelsPage.FSmall)), Theme.Muted, ModelsText.Line);
            }
            string q = name;
            Zone(g, r, () => P.ShowListFor(q));
        }
        int rowsN = (list.Count + cols - 1) / cols;
        return y + rowsN * (ch + gap) + S(8);
    }
}

// =====================================================================
//  Community: thanks wall + full timeline
// =====================================================================
internal sealed class ModelsCommunityView : ModelsView
{
    public ModelsCommunityView(ModelsPage p) : base(p) { }

    protected override int Render(Graphics? g, int width, Rectangle clip)
    {
        var (x1, w1, x2, w2, two) = Columns(width);
        int y1 = S(4);
        int h1 = Wall(null, x1, y1, w1, Rectangle.Empty);
        if (g != null) Wall(g, x1, y1, w1, new Rectangle(x1, y1, w1, h1));
        int y2 = two ? S(4) : y1 + h1 + S(16);
        int h2 = Timeline(null, x2, y2, w2, Rectangle.Empty);
        if (g != null) Timeline(g, x2, y2, w2, new Rectangle(x2, y2, w2, h2));
        return Math.Max(y1 + h1, y2 + h2) + S(16);
    }

    private int Wall(Graphics? g, int x, int y, int w, Rectangle frame)
    {
        var s = P.Stats;
        var r = frame.IsEmpty ? new Rectangle(x, y, w, 0) : frame;
        int cy = Card(g, r, "", Lang.T("mdl_card_people"), string.Format(Lang.T("mdl_people_sub"), s.People.Count, s.CreditedModels));
        int ix = r.X + S(18), iw = r.Width - S(36), cx = ix;
        foreach (var (login, n, _) in s.People)
        {
            string label = "@" + login + (n > 1 ? "  ×" + n : "");
            int cw = ModelsText.NaturalW(label, ModelsPage.FSmall) + S(16);
            if (cx + cw > ix + iw && cx > ix) { cx = ix; cy += ChipH() + S(6); }
            Chip(g, cx, cy, label, Theme.Accent, n > 1, false, Theme.Accent);
            string who = login;
            Zone(g, new Rectangle(cx, cy, cw, ChipH()), () => ModelsPage.OpenUrl("https://github.com/" + Uri.EscapeDataString(who)), "github.com/" + who);
            cx += cw + S(6);
        }
        cy += ChipH() + S(18);
        return cy - r.Y;
    }

    private int Timeline(Graphics? g, int x, int y, int w, Rectangle frame)
    {
        var r = frame.IsEmpty ? new Rectangle(x, y, w, 0) : frame;
        int cy = Card(g, r, "", Lang.T("mdl_card_timeline"), Lang.T("mdl_timeline_sub"));
        int ix = r.X + S(18), iw = r.Width - S(36);
        foreach (var ev in P.Stats.Timeline)
            cy += Event(g, ev, ix, cy, iw) + S(8);
        return cy - r.Y + S(10);
    }
}
