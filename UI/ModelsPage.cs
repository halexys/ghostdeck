using System.Drawing.Drawing2D;

namespace GhostDeck;

// =====================================================================
//  Models — full list of recognised firmware IDs (from Devices.All)
// =====================================================================
/// <summary>
/// Read-only catalogue of every <see cref="DeviceProfile"/> the app knows, rendered live from
/// <see cref="Devices.All"/> so it never drifts from the code. A fixed header (title + detected
/// model + search box) sits above a scrolling table; the user's detected model is highlighted.
/// Mirrors docs/SUPPORTED_MODELS.md (same columns).
/// </summary>
public sealed class ModelsPage : ThemedPage
{
    private const int Pad = 28;
    // Rebuilt by OnDeviceDbChanged: the page is created once and cached, and it is pre-warmed
    // shortly after launch, so a database downloaded later would never show without this.
    private DeviceProfile? _detected;
    private List<DeviceProfile> _all;
    private List<DeviceProfile> _rows;

    private readonly Panel _scrollHost = new() { AutoScroll = true };
    private readonly Table _table;
    private readonly TextBox _search = new();
    private readonly Button _verify = new();
    private readonly Button _dbCheck = new();   // fetch the signed model database now
    private readonly Panel _footer = new();
    private readonly ToolTip _tip = new() { InitialDelay = 250, AutoPopDelay = 8000 };

    public ModelsPage(MainDeps d) : base(d)
    {
        AutoScroll = false;   // the inner _scrollHost scrolls the table; the header stays fixed
        _detected = Devices.Detect(d.Firmware());
        _all = BuildCatalogue();
        _rows = _all;

        _table = new Table(this);
        _scrollHost.Controls.Add(_table);
        Controls.Add(_scrollHost);

        _search.BorderStyle = BorderStyle.FixedSingle;
        _search.Font = new Font("Segoe UI", 10.5f);
        _search.Width = 300;
        _search.PlaceholderText = Lang.T("mdl_search");
        _search.TextChanged += (_, _) => ApplyFilter();
        Controls.Add(_search);
        _search.BringToFront();

        // Bottom footer bar (white): table legend on the left, "Verify my model" CTA on the right.
        _footer.Paint += PaintFooter;
        Controls.Add(_footer);
        Ui.StylePrimary(_verify);
        _verify.Text = Lang.T("models_verify_btn");
        _verify.AutoSize = false;
        _verify.Click += (_, _) => (FindForm() as MainForm)?.ShowReport(0);
        _tip.SetToolTip(_verify, Lang.T("models_verify_desc"));
        _footer.Controls.Add(_verify);

        // The catalogue is exactly what this button refreshes, so it belongs next to it.
        Ui.StyleGhost(_dbCheck);
        _dbCheck.Text = Lang.T("modeldb_check");
        _dbCheck.AutoSize = false;
        _dbCheck.Click += (_, _) =>
        {
            _dbCheck.Enabled = false;
            _dbCheck.Text = Lang.T("modeldb_checking");
            LayoutBits();
            D.CheckModelDbNow(code =>
            {
                if (IsDisposed || _dbCheck.IsDisposed) return;
                void Show()
                {
                    _dbCheck.Enabled = true;
                    _dbCheck.Text = Lang.T("modeldb_check");
                    _tip.SetToolTip(_dbCheck, code switch
                    {
                        > 0 => string.Format(Lang.T("modeldb_applied"), code),
                        -1  => Lang.T("modeldb_failed"),
                        -2  => Lang.T("modeldb_deferred"),
                        _   => Lang.T("modeldb_current"),
                    });
                    LayoutBits();
                }
                if (InvokeRequired) BeginInvoke(Show); else Show();
            });
        };
        _footer.Controls.Add(_dbCheck);

        ClientSizeChanged += (_, _) => LayoutBits();
    }

    private void PaintFooter(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Surface);
        using (var pen = new Pen(Theme.Border)) g.DrawLine(pen, 0, 0, _footer.Width, 0);
        int legendRight = _dbCheck.Left - 20;
        TextRenderer.DrawText(g, Lang.T("mdl_legend"), FLegend,
            new Rectangle(Pad, 10, Math.Max(60, legendRight - Pad), _footer.Height - 16), Theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak);
    }

    private static List<DeviceProfile> BuildCatalogue() =>
        Devices.All
            .OrderBy(x => x.Tier == Tier.Tested ? 0 : (x.ShiftMode == 0xF2 ? 2 : 1))
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public override void OnEnter()
    {
        D.PollModelDb();               // debounced; the tab is where people look for this
        StyleSearch(); LayoutBits(); Invalidate(); _table.Invalidate();
    }

    public override void OnDeviceDbChanged()
    {
        _detected = Devices.Detect(D.Firmware());
        _all = BuildCatalogue();
        ApplyFilter();                 // keeps whatever is typed in the search box
        LayoutBits(); Invalidate(); _table.Invalidate();
    }
    public override void ApplyTheme()
    {
        base.ApplyTheme();
        _scrollHost.BackColor = Theme.Surface;
        _table.BackColor = Theme.Surface;
        _footer.BackColor = Theme.Surface;
        StyleSearch();
        Ui.StylePrimary(_verify);
        Invalidate(); _table.Invalidate(); _footer.Invalidate();
    }

    private void StyleSearch() { _search.BackColor = Theme.Card; _search.ForeColor = Theme.Text; }

    internal DeviceProfile? Detected => _detected;
    internal IReadOnlyList<DeviceProfile> Rows => _rows;
    internal ToolTip Tip => _tip;

    private void ApplyFilter()
    {
        string q = _search.Text.Trim();
        _rows = string.IsNullOrEmpty(q)
            ? _all
            : _all.Where(x =>
                x.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                x.FirmwarePrefixes.Any(f => f.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
        LayoutTable();
        _table.Invalidate();
    }

    // ---- fonts / metrics ----
    private static readonly Font FTitle = new("Segoe UI", 18f, FontStyle.Bold);
    private static readonly Font FSub = new("Segoe UI", 11f, FontStyle.Bold);
    private static readonly Font FIntro = new("Segoe UI", 10f);
    internal static readonly Font FHead = new("Segoe UI", 9.5f, FontStyle.Bold);
    internal static readonly Font FCell = new("Segoe UI", 10.5f);
    internal static readonly Font FMono = new("Consolas", 10.5f);
    internal static readonly Font FLegend = new("Segoe UI", 9f);
    internal static readonly Font FIcon = new("Segoe UI Symbol", 10.5f, FontStyle.Bold);

    internal const int HeadH = 34, RowH = 32;
    // Model and Thanks get the widest columns: both wrap onto extra lines instead of ending in "..."
    internal static readonly float[] Lefts = { 0f, .25f, .35f, .43f, .53f, .67f, .72f, .83f };
    internal const int MaxLines = 6;   // safety cap only: the longest name and the six-person credit fit in fewer

    private int HeaderBandH() => 24 + FTitle.Height + 14 + FSub.Height + 10 + FIntro.Height * 2 + 18;

    private void LayoutBits()
    {
        int hb = HeaderBandH();
        // search box: top-right of the fixed header band
        _search.Location = new Point(Math.Max(Pad, ClientSize.Width - Pad - _search.Width), 26);

        // Fixed bottom footer: legend (left) + Verify CTA (right). The table scrolls between header and footer.
        int footerH = Math.Max(66, FLegend.Height * 3 + 26);
        _footer.SetBounds(0, ClientSize.Height - footerH, ClientSize.Width, footerH);
        int vw = TextRenderer.MeasureText(_verify.Text, _verify.Font).Width + 44, vh = 42;
        _verify.SetBounds(_footer.Width - Pad - vw, (footerH - vh) / 2, vw, vh);
        int dw = TextRenderer.MeasureText(_dbCheck.Text, _dbCheck.Font).Width + 32;
        _dbCheck.SetBounds(_verify.Left - 12 - dw, (footerH - vh) / 2, dw, vh);

        _scrollHost.SetBounds(0, hb, ClientSize.Width, Math.Max(0, ClientSize.Height - hb - footerH));
        _footer.Invalidate();
        LayoutTable();
    }

    private void LayoutTable()
    {
        int w = _scrollHost.ClientSize.Width;
        _table.Width = w;
        _table.Height = Math.Max(_table.ContentHeight(w), _scrollHost.ClientSize.Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int avail = ClientSize.Width - Pad * 2;

        TextRenderer.DrawText(g, Lang.T("tab_models"), FTitle, new Point(Pad, 24), Theme.Text);

        int y = 24 + FTitle.Height + 14;
        if (_detected is { } det)
        {
            string sub = Lang.T("mdl_you") + ":  " + det.Name + "   ·   " + D.Firmware();
            TextRenderer.DrawText(g, sub, FSub, new Rectangle(Pad, y, avail - _search.Width - 20, FSub.Height), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        else
        {
            string sub = string.Format(Lang.T("mdl_unknown"), string.IsNullOrEmpty(D.Firmware()) ? "?" : D.Firmware());
            TextRenderer.DrawText(g, sub, FSub, new Point(Pad, y), Color.FromArgb(0xB0, 0x4A, 0x3A));
        }
        y += FSub.Height + 10;
        // tested count injected live from Devices.All - the old text hardcoded "1 tested" and went stale
        string intro = string.Format(Lang.T("mdl_intro"), _all.Count(m => m.Tier == Tier.Tested));
        TextRenderer.DrawText(g, intro, FIntro, new Rectangle(Pad, y, avail, FIntro.Height * 2 + 4),
            Theme.Muted, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.WordEllipsis);
    }

    protected override void Dispose(bool disposing) { if (disposing) _tip.Dispose(); base.Dispose(disposing); }

    // =================================================================
    //  Inner scrolling table
    // =================================================================
    private sealed class Table : Control
    {
        private readonly ModelsPage _p;
        private Rectangle _sbHeaderRect;
        private Rectangle _familyHeaderRect;
        private readonly List<(Rectangle rect, string url)> _creditRects = new();

        // Per-row heights for the width they were measured at: a row grows (up to MaxLines) when
        // the name, the firmware list or the thank-you list needs more than one line.
        private int[] _rowH = Array.Empty<int>();
        private int _rowHWidth = -1;
        private IReadOnlyList<DeviceProfile>? _rowHFor;
        private string _rowHLang = "";

        // Wrapping cells: word break, "&" printed as-is (a credit reads "A & B"), and an ellipsis
        // only on the last allowed line when even MaxLines are not enough.
        private const TextFormatFlags CellFlags = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak
            | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        private const TextFormatFlags MeasureFlags = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak
            | TextFormatFlags.NoPrefix;

        public Table(ModelsPage p) { _p = p; DoubleBuffered = true; ResizeRedraw = true; BackColor = Theme.Surface; }

        private int S(int v) => (int)Math.Ceiling(v * DeviceDpi / 96f);
        private int HeadHS => Math.Max(S(HeadH), FHead.Height + S(16));
        // single-line row height and the top inset that centres one line of text in it
        private int BaseRowH => Math.Max(S(RowH), FCell.Height + S(14));
        private int TextTop => Math.Max(S(4), (BaseRowH - LineH(FCell)) / 2);

        private static string[] Headers() => new[]
        {
            Lang.T("st_model"), Lang.T("st_firmware"), Lang.T("mdl_c_family"),
            Lang.T("mdl_c_status"), Lang.T("mdl_c_curve"), Lang.T("mdl_c_sb"), Lang.T("mdl_c_rpm"),
            Lang.T("mdl_c_thanks"),
        };

        // Family and SB hold two letters or a tick, so their width comes from the header plus its
        // help dot (long in ru/de/fr). Status holds one of two words that must never break mid-word
        // ("Eksperymentalny"), so it is as wide as the longer one. The remaining columns share what
        // is left by their Lefts weight.
        private static bool FixedCol(int i) => i == 2 || i == 3 || i == 5;
        private int[] Columns(int width)
        {
            int avail = width - Pad * 2, inner = avail - 36;
            int n = Lefts.Length;
            var head = Headers();
            var w = new float[n];
            float fixedW = 0, weightSum = 0;
            for (int i = 0; i < n; i++)
            {
                if (i == 2 || i == 5) w[i] = NaturalW(head[i], FHead) + S(30) + 10;
                else if (i == 3)
                    w[i] = Math.Max(NaturalW(head[i], FHead),
                        Math.Max(NaturalW(Lang.T("tier_tested"), FCell), NaturalW(Lang.T("tier_experimental"), FCell))) + 10;
                if (FixedCol(i)) fixedW += w[i];
                else weightSum += (i + 1 < n ? Lefts[i + 1] : 1f) - Lefts[i];
            }
            float rest = Math.Max(0, inner - fixedW);
            for (int i = 0; i < n; i++)
                if (!FixedCol(i)) w[i] = rest * ((i + 1 < n ? Lefts[i + 1] : 1f) - Lefts[i]) / weightSum;
            int[] cx = new int[n + 1];
            float x = Pad + 18;
            for (int i = 0; i < n; i++) { cx[i] = (int)x; x += w[i]; }
            cx[n] = Pad + avail - 14;
            return cx;
        }

        // A single word wider than its column (a long GitHub login) is split onto the next line
        // instead of ending in "...": word wrapping alone never breaks inside a word.
        private static readonly Dictionary<(string, int), string> BrokenCache = new();
        private static string BreakLongWords(string text, Font font, int w)
        {
            // most cells fit on one line: one unconstrained measure settles them
            if (w <= 0 || NaturalW(text, font) <= w) return text;
            // FCell and FMono never carry the same text, so text + width is a sufficient key
            if (BrokenCache.TryGetValue((text, w), out var done)) return done;
            return BrokenCache[(text, w)] = BreakLongWordsCore(text, font, w);
        }
        private static string BreakLongWordsCore(string text, Font font, int w)
        {
            var words = text.Split(' ');
            for (int k = 0; k < words.Length; k++)
            {
                string word = words[k];
                if (NaturalW(word, font) <= w) continue;
                var sb = new System.Text.StringBuilder();
                int start = 0;
                while (start < word.Length)
                {
                    int len = 1;
                    while (start + len < word.Length
                           && TextRenderer.MeasureText(word.Substring(start, len + 1), font, Size.Empty, MeasureFlags).Width <= w) len++;
                    // prefer to break right after a hyphen ("world-" / "spec"), never leave a lone
                    // trailing comma for the next line
                    if (start + len < word.Length)
                    {
                        int hy = word.LastIndexOf('-', start + len - 1, len - 1);
                        if (hy > start) len = hy - start + 1;
                        else if (start + len == word.Length - 1 && word[^1] == ',' && len > 1) len--;
                    }
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(word, start, len);
                    start += len;
                }
                words[k] = sb.ToString();
            }
            return string.Join(" ", words);
        }
        private static int ColW(int[] cx, int c) => cx[c + 1] - cx[c] - 10;

        private sealed record RowText(string Name, string Fw, string Family, string Status, Color StatusCol,
            string Curve, Color CurveCol, bool Sb, string Rpm, bool HasRpm, string Credit);

        private static RowText Texts(DeviceProfile m)
        {
            bool tested = m.Tier == Tier.Tested;
            string curve; Color curveCol;
            if (m.FanCurve is { } fc)
            {
                // Same distinction as docs/SUPPORTED_MODELS.md: a verified curve on a model
                // still awaiting its profile checks is editable only after the opt-in.
                if (fc.Verified) { curve = "✓ " + Lang.T(tested ? "mdl_curve_edit" : "mdl_curve_vopt"); curveCol = Theme.Green; }
                else { curve = "◉ " + Lang.T("mdl_curve_prev"); curveCol = Theme.Accent; }
            }
            else { curve = "—"; curveCol = Theme.Muted; }

            bool sb = m.Recipes.TryGetValue(ProfileId.SuperBattery, out var sr) && sr.Any(x => x.val == 0x0F);
            // spaces around "/" let a two-fan pair wrap instead of being cut
            string rpm = m.CpuRpmAddr16 != 0
                ? (m.GpuRpmAddr16 == 0 ? $"✓ 0x{m.CpuRpmAddr16:X2}:{m.CpuRpmAddr16 + 1:X2}"   // wide (16-bit) pairs
                                       : $"✓ 0x{m.CpuRpmAddr16:X2}:{m.CpuRpmAddr16 + 1:X2} / {m.GpuRpmAddr16:X2}:{m.GpuRpmAddr16 + 1:X2}")
                : m.CpuRpmAddr == 0 ? "—"
                : m.GpuRpmAddr == 0 ? $"✓ 0x{m.CpuRpmAddr:X2}"   // single-tach boards (iGPU)
                : $"✓ 0x{m.CpuRpmAddr:X2} / 0x{m.GpuRpmAddr:X2}";
            bool hasRpm = m.CpuRpmAddr != 0 || m.CpuRpmAddr16 != 0;
            return new RowText(m.Name, string.Join(", ", m.FirmwarePrefixes), m.ShiftMode == 0xF2 ? "G1" : "G2",
                Lang.T(tested ? "tier_tested" : "tier_experimental"), tested ? Theme.Green : Theme.Amber,
                curve, curveCol, sb, rpm, hasRpm, m.Credit.Length > 0 ? "@" + m.Credit : "—");
        }

        // One line as TextRenderer lays it out (taller than Font.Height), the unit for the line cap.
        // One-line width of a text does not depend on the column width, so it is measured once per
        // text and font; a resize then only measures the cells that really wrap.
        // Keyed by font REFERENCE: Font.GetHashCode/Equals read the family name through GDI+ on
        // every call, which cost more than the measure being cached (the fonts here are static).
        private static readonly Dictionary<Font, Dictionary<string, int>> NaturalWCache = new(ReferenceEqualityComparer.Instance);
        private static readonly Dictionary<Font, Dictionary<string, int>> WordWCache = new(ReferenceEqualityComparer.Instance);
        private static int CachedW(Dictionary<Font, Dictionary<string, int>> cache, string text, Font font, TextFormatFlags flags)
        {
            if (!cache.TryGetValue(font, out var d)) cache[font] = d = new();
            if (!d.TryGetValue(text, out int w)) d[text] = w = TextRenderer.MeasureText(text, font, Size.Empty, flags).Width;
            return w;
        }
        // SingleLine, not MeasureFlags: with WordBreak and no width, MeasureText wraps at every
        // space and returns the widest WORD instead of the whole line
        private static int NaturalW(string text, Font font) =>
            CachedW(NaturalWCache, text, font, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

        private static readonly Dictionary<Font, int> LineHCache = new(ReferenceEqualityComparer.Instance);
        private static int LineH(Font font)
        {
            if (!LineHCache.TryGetValue(font, out int h))
                LineHCache[font] = h = TextRenderer.MeasureText("Ag", font, Size.Empty, MeasureFlags).Height;
            return h;
        }

        // Height of an already broken text wrapped into a column, capped at MaxLines lines of that font.
        private static int TextH(string text, Font font, int w)
        {
            // one line needs no wrapped measure (the common case for every column)
            if (text.IndexOf('\n') < 0 && NaturalW(text, font) <= w) return LineH(font);
            return Math.Min(Lines(text, font, w), MaxLines) * LineH(font);
        }

        // Line count of a word-wrapped text, from cached word widths instead of a GDI measure per
        // cell and width: a window resize re-wraps ~160 rows, and a wrapped MeasureText each time
        // made every resize step several times slower. Greedy like DrawText's word break, against
        // a width 3 px narrower than the cell so the count never comes out below what is drawn.
        private static int Lines(string text, Font font, int w)
        {
            int space = SpaceW(font), avail = w - PadW(font) - 3, lines = 0;
            foreach (var seg in text.Split('\n'))
            {
                int cur = -1;
                foreach (var word in seg.Split(' '))
                {
                    if (word.Length == 0) continue;
                    int ww = WordW(word, font);
                    if (cur < 0) { cur = ww; lines++; }
                    else if (cur + space + ww <= avail) cur += space + ww;
                    else { cur = ww; lines++; }
                }
                if (cur < 0) lines++;   // empty segment still takes a line
            }
            return Math.Max(1, lines);
        }

        private const TextFormatFlags TightFlags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        private static int WordW(string word, Font font) => CachedW(WordWCache, word, font, TightFlags);
        private static int SpaceW(Font font) => WordW("a a", font) - WordW("aa", font);
        // the left+right padding DrawText keeps inside the cell (MeasureFlags has no NoPadding)
        private static int PadW(Font font) => NaturalW("a", font) - WordW("a", font);

        // The wrapping cells of each row with long words already broken for the current widths:
        // Name, Firmware, Status, Curve, RPM, Thanks. Colours are not cached (they follow the theme).
        private string[][] _cells = Array.Empty<string[]>();
        private int[] _creditW = Array.Empty<int>();   // width of the wrapped thank-you text = its click area
        private static readonly int[] WrapCols = { 0, 1, 3, 4, 6, 7 };
        private static Font CellFont(int k, RowText t) => k == 1 || (k == 4 && t.HasRpm) ? FMono : FCell;

        private int[] RowHeights(int width)
        {
            var rows = _p.Rows;
            if (width == _rowHWidth && ReferenceEquals(rows, _rowHFor) && _rowH.Length == rows.Count
                && _rowHLang == Lang.CurrentCode) return _rowH;
            int[] cx = Columns(width);
            int baseH = BaseRowH, top = TextTop;
            var hs = new int[rows.Count];
            var cells = new string[rows.Count][];
            var credW = new int[rows.Count];
            for (int i = 0; i < rows.Count; i++)
            {
                var t = Texts(rows[i]);
                string[] raw = { t.Name, t.Fw, t.Status, t.Curve, t.Rpm, t.Credit };
                var row = cells[i] = new string[raw.Length];
                int need = 0;
                for (int k = 0; k < raw.Length; k++)
                {
                    var font = CellFont(k, t);
                    int w = ColW(cx, WrapCols[k]);
                    row[k] = BreakLongWords(raw[k], font, w);
                    need = Math.Max(need, TextH(row[k], font, w));
                }
                hs[i] = Math.Max(baseH, need + top * 2);
                int cw = ColW(cx, 7);
                // a wrapped list fills the column, so the whole column is its click area
                credW[i] = row[5].IndexOf('\n') < 0 && NaturalW(row[5], FCell) <= cw ? NaturalW(row[5], FCell) : cw;
            }
            _rowH = hs; _cells = cells; _creditW = credW; _rowHWidth = width; _rowHFor = rows; _rowHLang = Lang.CurrentCode;
            return hs;
        }

        public int ContentHeight(int width) => HeadHS + RowHeights(width).Sum() + 10 + 14;   // legend moved to the page footer

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Cursor = _sbHeaderRect.Contains(e.Location) || _familyHeaderRect.Contains(e.Location)
                     || _creditRects.Any(c => c.rect.Contains(e.Location))
                ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (_sbHeaderRect.Contains(e.Location))
            {
                HelpPopup.Toggle(this, _sbHeaderRect, Lang.T("mdl_sb_tip"), this);
                return;
            }
            if (_familyHeaderRect.Contains(e.Location))
            {
                HelpPopup.Toggle(this, _familyHeaderRect, Lang.T("mdl_family_tip"), this);
                return;
            }
            foreach (var (rect, url) in _creditRects)
                if (rect.Contains(e.Location))
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
                    return;
                }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Surface);
            int width = Width;
            int avail = width - Pad * 2;
            int[] cx = Columns(width);
            int[] rowH = RowHeights(width);
            int headH = HeadHS, top = TextTop;

            int rows = _p.Rows.Count;
            int tableH = headH + (rows == 0 ? BaseRowH : rowH.Sum()) + 10;
            Ui.FillCard(g, new RectangleF(Pad, 0, avail, tableH));

            var headers = Headers();
            _creditRects.Clear();
            for (int c = 0; c < headers.Length; c++)
                TextRenderer.DrawText(g, headers[c], FHead, new Rectangle(cx[c], S(8), ColW(cx, c), headH - S(8)), Theme.Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            // Two columns need a sentence of explanation, so they get the app's standard help dot:
            // click to open the bubble, click again (or anywhere) to dismiss. Family is in there
            // because G1 / G2 is msi-ec's grouping, not an MSI designation - worth saying out loud
            // next to a column that otherwise reads like something the vendor prints on the box.
            int dot = S(18), dotY = S(8) + (headH - S(8) - dot) / 2;
            int famTextW = TextRenderer.MeasureText(headers[2], FHead).Width;
            HelpDot.Render(g, new RectangleF(cx[2] + famTextW + S(6), dotY, dot, dot));
            _familyHeaderRect = new Rectangle(cx[2], 0, famTextW + S(30), headH);

            int sbTextW = TextRenderer.MeasureText(headers[5], FHead).Width;
            HelpDot.Render(g, new RectangleF(cx[5] + sbTextW + S(6), dotY, dot, dot));
            _sbHeaderRect = new Rectangle(cx[5], 0, sbTextW + S(30), headH);

            using (var pen = new Pen(Theme.Border)) g.DrawLine(pen, cx[0], headH, Pad + avail - 14, headH);

            int ry = headH;
            for (int i = 0; i < rows; i++)
            {
                var m = _p.Rows[i];
                int h = rowH[i];
                // zebra striping so the eye keeps its row across eight columns; the detected
                // model keeps its accent wash and bar on top of that
                if (ReferenceEquals(m, _p.Detected))
                {
                    using var b = new SolidBrush(Theme.AccentSoft);
                    g.FillRectangle(b, Pad + 8, ry, avail - 16, h);
                    using var bar = new SolidBrush(Theme.Accent);
                    g.FillRectangle(bar, Pad + 8, ry, S(4), h);
                }
                else if (i % 2 == 1)
                {
                    using var b = new SolidBrush(Theme.RowAlt);
                    g.FillRectangle(b, Pad + 8, ry, avail - 16, h);
                }

                // Thank-you column: the reporter's GitHub login(s), clickable -> their issue. The
                // click area is kept for every row, also those outside this paint's clip.
                if (m.Credit.Length > 0 && m.CreditUrl.Length > 0)
                    _creditRects.Add((new Rectangle(cx[7], ry, _creditW[i], h), m.CreditUrl));

                // only rows inside the area being repainted are drawn (157 rows, a screenful visible)
                if (ry < e.ClipRectangle.Bottom && ry + h > e.ClipRectangle.Top)
                {
                    var t = Texts(m);
                    var c = _cells[i];
                    Cell(g, cx, 0, ry, top, h, c[0], FCell, Theme.Text);
                    Cell(g, cx, 1, ry, top, h, c[1], FMono, Theme.Text);
                    Cell(g, cx, 2, ry, top, h, t.Family, FCell, Theme.Muted);
                    Cell(g, cx, 3, ry, top, h, c[2], FCell, t.StatusCol);
                    Cell(g, cx, 4, ry, top, h, c[3], FCell, t.CurveCol);
                    Cell(g, cx, 5, ry, top, h, t.Sb ? "✓" : "—", FCell, t.Sb ? Theme.Text : Theme.Muted);
                    Cell(g, cx, 6, ry, top, h, c[4], t.HasRpm ? FMono : FCell, t.HasRpm ? Theme.Text : Theme.Muted);
                    Cell(g, cx, 7, ry, top, h, c[5], FCell, m.Credit.Length > 0 ? Theme.Accent : Theme.Muted);
                }

                ry += h;
            }

            if (rows == 0)
                TextRenderer.DrawText(g, "—", FCell, new Rectangle(cx[0], headH, avail, BaseRowH), Theme.Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }

        private static void Cell(Graphics g, int[] cx, int c, int ry, int top, int h, string text, Font font, Color color)
        {
            int w = ColW(cx, c);
            // cap the box at MaxLines lines so the ellipsis lands on the last allowed line; a
            // shorter font (the monospaced firmware and RPM cells) is centred on the first line
            int lh = LineH(font), dy = Math.Max(0, (LineH(FCell) - lh) / 2);
            int boxH = Math.Min(h - top - dy, lh * MaxLines);
            TextRenderer.DrawText(g, text, font, new Rectangle(cx[c], ry + top + dy, w, boxH), color, CellFlags);
        }
    }
}
