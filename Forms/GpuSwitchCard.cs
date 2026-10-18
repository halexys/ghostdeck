namespace GhostDeck;

/// <summary>
/// Confirms a graphics-mode change (TECHNICAL.md §82): Restart now (accent) / Cancel, and
/// "On next restart" on the left. When a display hangs off the discrete card and the target is
/// Integrated, an amber block names it - that display goes dark after the switch.
/// <see cref="Choice"/> tells the caller what was picked (null = cancelled).
/// </summary>
public sealed class GpuSwitchCard : GhostCardForm
{
    public enum Pick { RestartNow, NextRestart }

    private readonly string _warning;
    public Pick? Choice { get; private set; }

    private GpuSwitchCard(string heading, string body, string warning)
        : base("//GPU", heading, body, Lang.T("gpu_restart"), Lang.T("gen_cancel"), () => { })
    {
        _warning = warning;
    }

    protected override int CardWidth => 540;

    protected override bool Acknowledge() { Choice = Pick.RestartNow; return true; }
    protected override string AuxLabel => Lang.T("gpu_next");
    protected override Color AuxColor => Ink;
    protected override void AuxClick() { Choice = Pick.NextRestart; Close(); }

    private static int Ce(float v) => (int)Math.Ceiling(v);

    protected override int MeasureContent(Graphics g, float k, int width)
    {
        if (_warning.Length == 0) return 0;
        using var f = new Font("Segoe UI", 13f * k, FontStyle.Regular, GraphicsUnit.Pixel);
        int textW = width - Ce(46 * k);
        return Ce(g.MeasureString(_warning, f, textW).Height) + Ce(22 * k);
    }

    protected override void PaintContent(Graphics g, float k, Rectangle area, Point origin)
    {
        if (_warning.Length == 0) return;
        using (var bg = new SolidBrush(Color.FromArgb(40, Amber)))
        using (var path = RoundPath(area, Ce(6 * k)))
        {
            g.FillPath(bg, path);
            using var edge = new Pen(Color.FromArgb(120, Amber), 1f);
            g.DrawPath(edge, path);
        }
        // warning triangle
        float s = 16 * k, tx = area.X + 12 * k, ty = area.Y + 11 * k;
        using (var pen = new Pen(Amber, Math.Max(1.2f, 1.6f * k)) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round })
        {
            g.DrawPolygon(pen, new[] { new PointF(tx + s / 2, ty), new PointF(tx + s, ty + s * .9f), new PointF(tx, ty + s * .9f) });
            g.DrawLine(pen, tx + s / 2, ty + s * .35f, tx + s / 2, ty + s * .58f);
            g.DrawLine(pen, tx + s / 2, ty + s * .72f, tx + s / 2, ty + s * .74f);
        }
        using var f = new Font("Segoe UI", 13f * k, FontStyle.Regular, GraphicsUnit.Pixel);
        using var b = new SolidBrush(Color.FromArgb(0xFF, 0xE2, 0xB0));
        g.DrawString(_warning, f, b, new RectangleF(area.X + 36 * k, area.Y + 10 * k, area.Width - 46 * k, area.Height - 12 * k));
    }

    /// <summary>Asks before switching to <paramref name="target"/>; null = cancelled.</summary>
    public static Pick? Ask(Form? owner, GpuMode target, IReadOnlyList<string> darkDisplays)
    {
        bool integrated = target == GpuMode.Integrated;
        string warning = integrated && darkDisplays.Count > 0
            ? string.Format(Lang.T("gpu_card_warn"), string.Join(", ", darkDisplays))
            : "";
        using var card = new GpuSwitchCard(
            Lang.T(integrated ? "gpu_card_title_int" : "gpu_card_title_hyb"),
            Lang.T(integrated ? "gpu_card_body_int" : "gpu_card_body_hyb"),
            warning);
        card.ShowOver(owner);
        return card.Choice;
    }
}
