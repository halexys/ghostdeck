using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace GhostDeck;

// =====================================================================
//  Settings -> Gaming -> Crosshair (roadmap #118, discussion #247)
//  Two cards: the crosshair itself (switch, shape tiles, own picture, preview, the anti-cheat
//  note) and its look (presets, colour, sliders, position, click effect, aim-hide, share code,
//  export). Every edit redraws the preview and the window at once; the file is saved a moment
//  later, so a slider drag does not write settings.json on every pixel.
// =====================================================================
public sealed partial class SettingsPage
{
    private SegControl? _gamingSeg;               // Gaming: overlay | crosshair
    private CardSection? _chCard, _chLookCard;
    private ToggleSwitch? _chToggle;
    private CrosshairPreview? _chPreview;
    private CrosshairKindPicker? _chPicker;
    private Action? _syncChPreset;                // re-points the preset list at the live look
    private bool _chSyncing;
    private System.Windows.Forms.Timer? _chSaveTimer;
    private Bitmap? _chImage;                     // the custom picture, for the tiles and the preview
    private DateTime _chImageStamp = DateTime.MinValue;
    private bool _chImageLoaded;

    private int GamingSub => Math.Clamp(D.Settings.SettingsGamingSub, 0, 1);

    private static readonly string[] KindKeys =
    {
        "ch_kind_cross", "ch_kind_gapcross", "ch_kind_gapcrossdot", "ch_kind_dot", "ch_kind_circle", "ch_kind_circledot",
        "ch_kind_t", "ch_kind_x", "ch_kind_chevron", "ch_kind_squaredot", "ch_kind_reticle", "ch_kind_image",
    };

    private void BuildCrosshairCards()
    {
        _chCard = BuildChMain();
        _chLookCard = BuildChLook();
        _gLeft[SubGaming].Add(_chCard);
        _gRight[SubGaming].Add(_chLookCard);
    }

    private void RebuildCrosshairCards()
    {
        if (_chCard != null) _chCard = RebuildCard(_chCard, BuildChMain);
        if (_chLookCard != null) _chLookCard = RebuildCard(_chLookCard, BuildChLook);
    }

    // The window was toggled elsewhere (tray, hotkey, brick, scene): follow it.
    private void SyncCrosshairCards()
    {
        if (_chToggle != null) _chToggle.Checked = D.CrosshairOn();
        _syncChPreset?.Invoke();
    }

    /// <summary>After any edit of the live look: redraw here and on the screen, save a little later.</summary>
    private void ChChanged()
    {
        D.Settings.Crosshair.Normalize();
        _chPreview?.Invalidate();
        _chPicker?.Invalidate();
        D.ApplyCrosshairSettings();
        _syncChPreset?.Invoke();
        _chSaveTimer ??= new System.Windows.Forms.Timer { Interval = 400 };
        _chSaveTimer.Stop();
        _chSaveTimer.Tick -= ChSaveTick;
        _chSaveTimer.Tick += ChSaveTick;
        _chSaveTimer.Start();
    }

    private void ChSaveTick(object? s, EventArgs e) { _chSaveTimer?.Stop(); D.SaveSettings(); }

    /// <summary>The stored custom picture (cached; reloaded when the file changes). Null = none.</summary>
    private Bitmap? ChImage()
    {
        DateTime stamp = DateTime.MinValue;
        try { if (File.Exists(CrosshairPainter.ImagePath)) stamp = File.GetLastWriteTimeUtc(CrosshairPainter.ImagePath); } catch { }
        if (_chImageLoaded && stamp == _chImageStamp) return _chImage;
        _chImage?.Dispose();
        _chImage = CrosshairPainter.LoadImage();
        _chImageStamp = stamp; _chImageLoaded = true;
        return _chImage;
    }

    private void ChMessage(string title, string body)
    {
        using var dlg = new GhostCardForm("//CROSSHAIR", title, body, Lang.T("gen_ok"), "", () => { });
        dlg.ShowOver(FindForm());
    }

    private static Label Note(string text, string tag) => new()
    {
        Text = text, AutoSize = true, MaximumSize = new Size(360, 0), Font = new Font("Segoe UI", 9f), Tag = tag,
    };

    private static FlowLayoutPanel Flow(params Control[] items)
    {
        var f = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty, Padding = Padding.Empty, WrapContents = true };
        foreach (var c in items) { c.Margin = new Padding(0, 0, 8, 6); f.Controls.Add(c); }
        return f;
    }

    private static Button Ghost(string text)
    {
        var b = new Button { Text = text, AutoSize = true, Padding = new Padding(10, 4, 10, 4) };
        Ui.StyleGhost(b);
        return b;
    }

    // ---------------- card 1: the crosshair ----------------
    private CardSection BuildChMain()
    {
        var st = D.Settings.Crosshair;
        var card = new CardSection(Lang.T("ch_title"), "");   // MDL2 ring-with-dot glyph (the closest to a reticle)
        _chToggle = Toggle(D.CrosshairOn(), v => { D.SetCrosshair(v); RefreshTiles(); });
        card.AddRow(Lang.T("ch_enable"), _chToggle);
        card.AddRow(null, Note(Lang.T("ch_desc"), "muted"));

        _chPicker = new CrosshairKindPicker(this) { Name = "wp-wide" };
        card.AddRow(null, _chPicker);

        // the user's own picture: one at a time, copied into the settings folder as PNG
        var load = Ghost(Lang.T("ch_image_load"));
        load.Click += (_, _) => ImportCrosshairImage();
        var clear = Ghost(Lang.T("ch_image_clear"));
        clear.Visible = CrosshairPainter.HasImage;
        clear.Click += (_, _) =>
        {
            CrosshairPainter.DeleteImage();
            if (st.KindId == CrosshairKind.Image) st.Kind = (int)CrosshairKind.Cross;
            D.SaveSettings();
            ChChanged();
            RebuildCrosshairCards();
        };
        card.AddRow(null, Flow(load, clear));
        string info = Lang.T("ch_image_none");
        if (ChImage() is { } img) info = string.Format(Lang.T("ch_image_info"), img.Width, img.Height);
        card.AddRow(null, Note(info, "muted"));

        _chPreview = new CrosshairPreview(this) { Name = "wp-wide" };
        card.AddRow(null, _chPreview);
        card.AddRow(null, Note(Lang.T("ch_fullscreen_note"), "muted"));
        card.AddRow(null, Note(Lang.T("ch_anticheat"), "warn"));
        return card;
    }

    /// <summary>Picks a PNG/BMP/JPG, checks the limits, stores it and switches the shape to it.</summary>
    private void ImportCrosshairImage()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "PNG, BMP, JPEG (*.png;*.bmp;*.jpg;*.jpeg)|*.png;*.bmp;*.jpg;*.jpeg|PNG (*.png)|*.png",
            Title = Lang.T("ch_image_load"),
        };
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        string? err = CrosshairPainter.ImportImage(dlg.FileName, out _);
        if (err != null)
        {
            ChMessage(Lang.T("ch_image_bad_title"),
                err == "size" ? string.Format(Lang.T("ch_image_too_big"), CrosshairPainter.MaxImagePx, CrosshairPainter.MaxImageBytes / 1024 / 1024)
                              : Lang.T("ch_image_unreadable"));
            return;
        }
        D.Settings.Crosshair.Kind = (int)CrosshairKind.Image;
        D.SaveSettings();
        ChChanged();
        RebuildCrosshairCards();
    }

    // ---------------- card 2: the look ----------------
    private CardSection BuildChLook()
    {
        var st = D.Settings.Crosshair;
        var card = new CardSection(Lang.T("ch_look"), "");   // MDL2 colour glyph

        // presets: "(current look)" + one entry per saved preset; picking one loads it
        var presets = D.Settings.CrosshairPresets;
        var items = new List<string> { Lang.T("ch_preset_custom") };
        items.AddRange(presets.Select(p => p.Name));
        int PresetIndex() { for (int i = 0; i < presets.Count; i++) if (presets[i].LooksLike(st)) return i + 1; return 0; }
        var combo = Combo(items.ToArray(), PresetIndex());
        var del = Ghost(Lang.T("ch_preset_delete"));
        _syncChPreset = () =>
        {
            int i = PresetIndex();
            _chSyncing = true;
            if (combo.SelectedIndex != i) combo.SelectedIndex = i;
            _chSyncing = false;
            del.Enabled = i > 0;
        };
        del.Enabled = PresetIndex() > 0;
        combo.SelectedIndexChanged += (_, _) =>
        {
            if (_chSyncing) return;
            int i = combo.SelectedIndex;
            if (i <= 0 || i > presets.Count) return;
            st.CopyFrom(presets[i - 1]);
            D.SaveSettings();
            D.ApplyCrosshairSettings();
            RebuildCrosshairCards();
        };
        card.AddRow(Lang.T("ch_preset"), combo);
        var save = Ghost(Lang.T("ch_preset_save"));
        save.Click += (_, _) =>
        {
            string? name = InputDialog.Ask(FindForm(), Lang.T("ch_preset_save"), Lang.T("ch_preset_name"), "",
                n => D.Settings.FindCrosshairPreset(n) != null ? Lang.T("ch_preset_taken") : null, "//CROSSHAIR");
            if (string.IsNullOrWhiteSpace(name)) return;
            var p = st.Clone(); p.Name = name.Trim();
            presets.Add(p);
            D.SaveSettings();
            RebuildCrosshairCards();
        };
        del.Click += (_, _) =>
        {
            int i = combo.SelectedIndex;
            if (i <= 0 || i > presets.Count) return;
            string name = presets[i - 1].Name;
            presets.RemoveAt(i - 1);
            foreach (var sc in D.Settings.Scenes)
                if (sc.CrosshairPreset is { } cp && cp.Equals(name, StringComparison.OrdinalIgnoreCase)) sc.CrosshairPreset = null;
            D.SaveSettings();
            RebuildCrosshairCards();
        };
        card.AddRow(null, Flow(save, del));

        card.AddRow(null, ColorRow((Lang.T("ch_color"), () => st.Color, v => { st.Color = v; ChChanged(); })));

        Slider Sl(int min, int max, int val, int step, string suffix, Action<int> set)
        {
            var s = new Slider(min, max, val, step, suffix) { Width = 300 };
            s.ValueChanged += v => { set(v); ChChanged(); };
            return s;
        }
        card.AddRow(Lang.T("ch_size"), Sl(CrosshairStyle.MinSize, CrosshairStyle.MaxSize, st.Size, 1, "", v => st.Size = v));
        card.AddRow(Lang.T("ch_thickness"), Sl(1, CrosshairStyle.MaxThickness, st.Thickness, 1, "", v => st.Thickness = v));
        card.AddRow(Lang.T("ch_outline"), Sl(0, CrosshairStyle.MaxOutline, st.Outline, 1, "", v => st.Outline = v));
        card.AddRow(Lang.T("ch_opacity"), Sl(CrosshairStyle.MinOpacity, 100, st.Opacity, 1, "%", v => st.Opacity = v));
        card.AddRow(Lang.T("ch_softness"), Sl(0, CrosshairStyle.MaxSoftness, st.Softness, 1, "", v => st.Softness = v));
        card.AddRow(Lang.T("ch_rotation"), Sl(0, 359, st.Rotation, 1, "°", v => st.Rotation = v));
        card.AddRow(Lang.T("ch_offset_x"), Sl(-CrosshairStyle.MaxOffset, CrosshairStyle.MaxOffset, st.OffsetX, 1, "", v => st.OffsetX = v));
        card.AddRow(Lang.T("ch_offset_y"), Sl(-CrosshairStyle.MaxOffset, CrosshairStyle.MaxOffset, st.OffsetY, 1, "", v => st.OffsetY = v));

        var pos = Combo(new[] { Lang.T("ch_pos_center"), Lang.T("ch_pos_cursor") }, st.FollowCursor ? 1 : 0);
        pos.SelectedIndexChanged += (_, _) => { st.FollowCursor = pos.SelectedIndex == 1; ChChanged(); };
        card.AddRow(Lang.T("ch_position"), pos);

        var fx = Combo(new[] { Lang.T("ch_fx_none"), Lang.T("ch_fx_spread"), Lang.T("ch_fx_pulse"), Lang.T("ch_fx_flash") }, st.ClickFx);
        fx.SelectedIndexChanged += (_, _) => { st.ClickFx = Math.Max(0, fx.SelectedIndex); ChChanged(); };
        card.AddRow(Lang.T("ch_click"), fx);

        var ads = Combo(new[] { Lang.T("ch_ads_none"), Lang.T("ch_ads_hold"), Lang.T("ch_ads_toggle") }, st.Ads);
        ads.SelectedIndexChanged += (_, _) => { st.Ads = Math.Max(0, ads.SelectedIndex); ChChanged(); };
        card.AddRow(Lang.T("ch_ads"), ads);
        card.AddRow(null, Note(Lang.T("ch_hook_note"), "muted"));

        // share code + export + defaults
        var copy = Ghost(Lang.T("ch_code_copy"));
        copy.Click += (_, _) =>
        {
            if (!Ui.CopyText(st.Encode())) return;
            string was = copy.Text;
            copy.Text = Lang.T("ch_code_copied");
            var t = new System.Windows.Forms.Timer { Interval = 1500 };
            t.Tick += (_, _) => { t.Stop(); t.Dispose(); if (!copy.IsDisposed) copy.Text = was; };
            t.Start();
        };
        var paste = Ghost(Lang.T("ch_code_paste"));
        paste.Click += (_, _) =>
        {
            string initial = "";
            try { string clip = Clipboard.GetText(); if (CrosshairStyle.Decode(clip) != null) initial = clip.Trim(); } catch { }
            string? code = InputDialog.Ask(FindForm(), Lang.T("ch_code_paste"), Lang.T("ch_code_label"), initial,
                t => CrosshairStyle.Decode(t) == null ? Lang.T("ch_code_bad") : null, "//CROSSHAIR");
            if (code == null || CrosshairStyle.Decode(code) is not { } decoded) return;
            st.CopyFrom(decoded);
            D.SaveSettings();
            D.ApplyCrosshairSettings();
            RebuildCrosshairCards();
        };
        var png = Ghost(Lang.T("ch_export_png"));
        png.Click += (_, _) =>
        {
            using var dlg = new SaveFileDialog { Filter = "PNG (*.png)|*.png", FileName = "ghostdeck-crosshair.png" };
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            try
            {
                using var bmp = CrosshairPainter.Render(st, st.KindId == CrosshairKind.Image ? ChImage() : null, 1f);
                bmp.Save(dlg.FileName, ImageFormat.Png);
            }
            catch (Exception ex) { ChMessage(Lang.T("ch_export_png"), string.Format(Lang.T("bk_err"), ex.Message)); }
        };
        var svg = Ghost(Lang.T("ch_export_svg"));
        svg.Enabled = st.KindId != CrosshairKind.Image;   // the picture has no vector form
        svg.Click += (_, _) =>
        {
            if (st.ToSvg() is not { } text) return;
            using var dlg = new SaveFileDialog { Filter = "SVG (*.svg)|*.svg", FileName = "ghostdeck-crosshair.svg" };
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            try { File.WriteAllText(dlg.FileName, text); }
            catch (Exception ex) { ChMessage(Lang.T("ch_export_svg"), string.Format(Lang.T("bk_err"), ex.Message)); }
        };
        var reset = Ghost(Lang.T("set_defaults"));
        reset.Click += (_, _) =>
        {
            st.CopyFrom(new CrosshairStyle());
            D.SaveSettings();
            D.ApplyCrosshairSettings();
            RebuildCrosshairCards();
        };
        card.AddRow(null, Flow(copy, paste, png, svg, reset));
        if (st.KindId == CrosshairKind.Image) card.AddRow(null, Note(Lang.T("ch_code_noimage"), "muted"));
        return card;
    }

    /// <summary>
    /// The shape tiles: one per built-in kind plus the custom picture, each drawn with the live
    /// colour by the same painter as the screen. Wraps to the card's width; the selected tile
    /// carries the accent frame (same look as the app-icon tiles).
    /// </summary>
    private sealed class CrosshairKindPicker : Control
    {
        private readonly SettingsPage _page;
        private readonly int _cellW, _cellH, _gap, _icon;
        private int _hover = -1, _cols = 1;

        public CrosshairKindPicker(SettingsPage page)
        {
            _page = page;
            DoubleBuffered = true; ResizeRedraw = true; Cursor = Cursors.Hand;
            float k = DeviceDpi / 96f;
            _cellW = (int)(84 * k); _cellH = (int)(100 * k); _gap = (int)(8 * k); _icon = (int)(40 * k);
            Width = 4 * _cellW + 3 * _gap;
            Fit();
        }

        private void Fit()
        {
            _cols = Math.Max(1, (Width + _gap) / (_cellW + _gap));
            int rows = (KindKeys.Length + _cols - 1) / _cols;
            int h = rows * (_cellH + _gap) - _gap;
            if (Height != h) Height = h;
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Fit(); }

        private Rectangle Cell(int i) => new(i % _cols * (_cellW + _gap), i / _cols * (_cellH + _gap), _cellW, _cellH);
        private int HitTest(Point p) { for (int i = 0; i < KindKeys.Length; i++) if (Cell(i).Contains(p)) return i; return -1; }

        protected override void OnMouseMove(MouseEventArgs e) { int h = HitTest(e.Location); if (h != _hover) { _hover = h; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int i = HitTest(e.Location);
            if (i < 0) return;
            var st = _page.D.Settings.Crosshair;
            if (i == (int)CrosshairKind.Image && _page.ChImage() == null) { _page.ImportCrosshairImage(); return; }
            if (i == st.Kind) return;
            st.Kind = i;
            _page.ChChanged();
            // the look card's SVG button and the picture note follow the kind
            _page.RebuildCrosshairCards();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Theme.Card);
            var live = _page.D.Settings.Crosshair;
            float k = DeviceDpi / 96f;
            using var lf = new Font("Segoe UI", 8.5f);
            // a tile shows the kind in the live colour at a tile-sized look: thin, no blur, upright
            var tile = live.Clone();
            tile.Size = 28; tile.Thickness = Math.Clamp(live.Thickness, 1, 3); tile.Outline = Math.Min(live.Outline, 1);
            tile.Softness = 0; tile.Rotation = 0; tile.Opacity = 100; tile.ClickFx = 0;
            var custom = _page.ChImage();
            for (int i = 0; i < KindKeys.Length; i++)
            {
                var c = Cell(i);
                bool sel = live.Kind == i;
                using (var path = Theme.RoundRect(new RectangleF(c.X + 1f, c.Y + 1f, c.Width - 2, c.Height - 2), 8))
                {
                    if (sel) { using var b = new SolidBrush(Theme.AccentSoft); g.FillPath(b, path); }
                    using var pen = new Pen(sel ? Theme.Accent : _hover == i ? Theme.BorderStrong : Theme.Border, sel ? 2f : 1f);
                    g.DrawPath(pen, path);
                }
                int iy = c.Y + (int)(8 * k);
                var box = new Rectangle(c.X + (c.Width - _icon) / 2, iy, _icon, _icon);
                // a dark backing so a light crosshair reads in the light theme too
                using (var bb = new SolidBrush(Color.FromArgb(0x16, 0x1B, 0x26)))
                using (var bp = Theme.RoundRect(new RectangleF(box.X, box.Y, box.Width, box.Height), 6))
                    g.FillPath(bb, bp);
                if (i == (int)CrosshairKind.Image && custom == null)
                {
                    // no picture yet: a dashed frame and a plus, the click opens the file picker
                    using var dp = new Pen(Theme.Muted, 1f) { DashStyle = DashStyle.Dash };
                    g.DrawRectangle(dp, box.X + 6, box.Y + 6, box.Width - 12, box.Height - 12);
                    using var gf = new Font("Segoe MDL2 Assets", 10f);
                    Ui.CenterGlyph(g, "", gf, Theme.Muted, box);
                }
                else
                {
                    tile.Kind = i;
                    using var bmp = CrosshairPainter.Render(tile, i == (int)CrosshairKind.Image ? custom : null, k);
                    float sc = Math.Min(1f, (box.Width - 4f) / Math.Max(bmp.Width, bmp.Height));
                    float w = bmp.Width * sc, h = bmp.Height * sc;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(bmp, new RectangleF(box.X + (box.Width - w) / 2f, box.Y + (box.Height - h) / 2f, w, h));
                }
                TextRenderer.DrawText(g, Lang.T(KindKeys[i]), lf,
                    new Rectangle(c.X + 3, box.Bottom + (int)(5 * k), c.Width - 6, c.Bottom - box.Bottom - (int)(7 * k)),
                    sel ? Theme.Text : Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }
    }

    /// <summary>
    /// The live look at real size on a dark and a light half, so the colour and the rim can be
    /// judged against both kinds of game scene. A crosshair too large for the box is scaled to fit.
    /// </summary>
    private sealed class CrosshairPreview : Control
    {
        private readonly SettingsPage _page;

        public CrosshairPreview(SettingsPage page)
        {
            _page = page;
            DoubleBuffered = true; ResizeRedraw = true;
            float k = DeviceDpi / 96f;
            Width = (int)(400 * k);
            Height = (int)(176 * k);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Parent?.BackColor ?? Theme.Card);
            float k = DeviceDpi / 96f;
            int half = Width / 2;
            var dark = new Rectangle(0, 0, half, Height);
            var light = new Rectangle(half, 0, Width - half, Height);
            using (var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 8))
            {
                g.SetClip(path);
                using (var b = new SolidBrush(Color.FromArgb(0x0B, 0x0E, 0x14))) g.FillRectangle(b, dark);
                using (var b = new SolidBrush(Color.FromArgb(0xE9, 0xEE, 0xF5))) g.FillRectangle(b, light);
                // a faint grid stands in for the scene, so the rim's job is visible
                using (var gp = new Pen(Color.FromArgb(28, 255, 255, 255)))
                    for (int x = (int)(24 * k); x < half; x += (int)(24 * k)) g.DrawLine(gp, x, 0, x, Height);
                using (var gp = new Pen(Color.FromArgb(18, 0, 0, 0)))
                    for (int x = half + (int)(24 * k); x < Width; x += (int)(24 * k)) g.DrawLine(gp, x, 0, x, Height);
                g.ResetClip();
                using var pen = new Pen(Theme.Border);
                g.DrawPath(pen, path);
            }
            var st = _page.D.Settings.Crosshair;
            using var bmp = CrosshairPainter.Render(st, st.KindId == CrosshairKind.Image ? _page.ChImage() : null, k);
            float fit = Math.Min(1f, Math.Min((half - 16f * k) / bmp.Width, (Height - 16f * k) / bmp.Height));
            float w = bmp.Width * fit, h = bmp.Height * fit;
            g.DrawImage(bmp, new RectangleF(dark.X + (dark.Width - w) / 2f, (Height - h) / 2f, w, h));
            g.DrawImage(bmp, new RectangleF(light.X + (light.Width - w) / 2f, (Height - h) / 2f, w, h));
            if (fit < 1f)
            {
                using var f = new Font("Segoe UI", 8f);
                TextRenderer.DrawText(g, string.Format(Lang.T("ch_preview_scaled"), (int)Math.Round(fit * 100)), f,
                    new Point((int)(8 * k), Height - f.Height - (int)(6 * k)), Color.FromArgb(0x98, 0xA0, 0xAE), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
        }
    }
}
