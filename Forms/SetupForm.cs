using System.Diagnostics;

namespace GhostDeck;

/// <summary>
/// The installer window (Core/Setup.cs, TECHNICAL §86): start → installing → done, or, when this
/// setup is already installed, reinstall / update / uninstall. Fixed dark palette - it runs before
/// any settings exist, so it does not follow the app theme. Spanish only: this is the fork owner's
/// personal installer, not a shipped component of the app.
/// </summary>
public sealed class SetupForm : Form
{
    private static readonly Color Bg = Color.FromArgb(0x0A, 0x0D, 0x14);
    private static readonly Color Ink = Color.FromArgb(0xF3, 0xF7, 0xFF);
    private static readonly Color Muted = Color.FromArgb(0xA4, 0xAD, 0xBD);
    private static readonly Color Faint = Color.FromArgb(0x77, 0x82, 0x96);
    private static readonly Color Cyan = Color.FromArgb(0x3D, 0xE3, 0xFF);
    private static readonly Color Blue = Color.FromArgb(0x3C, 0x7D, 0xFF);
    private static readonly Color Green = Color.FromArgb(0x61, 0xE7, 0xA4);
    private static readonly Color Amber = Color.FromArgb(0xFF, 0xC1, 0x5D);
    private static readonly Color Pink = Color.FromArgb(0xFF, 0x7F, 0xA8);

    private static readonly string[] StepNames =
    {
        "Cerrar GhostDeck si está abierto",
        "Copiar GhostDeck.exe",
        "Componente de MSI",
        "Crear la tarea de inicio",
        "Acceso directo y desinstalador",
    };

    private readonly FlowLayoutPanel _body = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill, Padding = new Padding(24, 20, 24, 0), BackColor = Bg };
    private readonly FlowLayoutPanel _buttons = new() { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, Height = 64, Padding = new Padding(16, 12, 16, 12), BackColor = Bg };
    private readonly bool _uninstallLaunch;

    public SetupForm(bool uninstallLaunch)
    {
        _uninstallLaunch = uninstallLaunch;
        Text = "Instalar GhostDeck";
        Icon = TrayIconFactory.AppIcon();
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(580, 470);
        BackColor = Bg;
        ForeColor = Ink;
        Font = new Font("Segoe UI", 10f);
        Controls.Add(_body);
        Controls.Add(_buttons);
        if (uninstallLaunch) ShowUninstallConfirm();
        else if (Setup.InstalledVersion() is { } v) ShowInstalled(v);
        else ShowStart();
    }

    // ---------------- pages ----------------

    private void ShowStart()
    {
        Reset();
        var v = Setup.ThisVersion;
        Title($"GhostDeck {v.Major}.{v.Minor}.{Math.Max(0, v.Build)}", "Deck");
        Subtitle(DeviceLine());
        Gap(6);
        Check(true, $"Copiar la app a {Setup.InstallDir}");
        switch (Setup.PlanSchema())
        {
            case Setup.SchemaPlan.Present: Check(true, "Componente de MSI (msiapcfg.dll): ya está en Windows, no se toca"); break;
            case Setup.SchemaPlan.WillInstall: Item("!", Amber, "Componente de MSI (msiapcfg.dll): se instalará en SysWOW64 y se registrará en Windows", Amber); break;
            default: Item("!", Amber, "Componente de MSI: falta en Windows y este instalador no lo trae. Instala MSI Center una vez.", Amber); break;
        }
        Check(true, "Acceso directo en el menú Inicio");
        Gap(6);
        var auto = new CheckBox { Text = "Arrancar GhostDeck al iniciar sesión", Checked = true, AutoSize = true, ForeColor = Ink, Margin = new Padding(0, 6, 0, 0) };
        _body.Controls.Add(auto);
        Button("Instalar", primary: true, () => RunInstall(auto.Checked));
        Button("Cancelar", primary: false, Close);
    }

    private void ShowInstalled(Version installed)
    {
        Reset();
        var mine = Setup.ThisVersion;
        string Mine() => $"{mine.Major}.{mine.Minor}.{Math.Max(0, mine.Build)}";
        bool older = Norm(installed) < Norm(mine);
        Title($"GhostDeck {installed.Major}.{installed.Minor}.{Math.Max(0, installed.Build)} ya está instalado", null);
        Para($"Este instalador trae la versión {Mine()}. Puedes {(older ? "actualizar" : "reinstalarla")} (tus ajustes, escenas y curvas se conservan) o desinstalar GhostDeck.");
        Button(older ? $"Actualizar a {Mine()}" : "Reinstalar", primary: true, () => RunInstall(Autostart.IsEnabled()));
        Button("Cancelar", primary: false, Close);
        Button("Desinstalar…", primary: false, ShowUninstallConfirm, Pink);
    }

    private void ShowUninstallConfirm()
    {
        Reset();
        Text = "Desinstalar GhostDeck";
        Title("¿Desinstalar GhostDeck?", null);
        Para("Se quitan la app de Program Files, la tarea de inicio, el acceso directo y la entrada de Aplicaciones. Se quedan el componente de MSI (otras herramientas de MSI pueden usarlo) y tus ajustes en %AppData%\\GhostDeck.");
        Button("Desinstalar", primary: true, RunUninstall, Color.FromArgb(0xD6, 0x1F, 0x69));
        Button("Cancelar", primary: false, () => { if (_uninstallLaunch) Close(); else ShowInstalled(Setup.InstalledVersion() ?? Setup.ThisVersion); });
    }

    private async void RunInstall(bool autostart)
    {
        Reset();
        Text = "Instalar GhostDeck";
        Title("Instalando…", null);
        var bar = new ProgressStrip { Width = 520, Height = 8, Margin = new Padding(0, 8, 0, 12) };
        _body.Controls.Add(bar);
        var rows = StepNames.Select(n => Item("○", Faint, n, Faint)).ToArray();
        void Mark(int i)
        {
            for (int k = 0; k < rows.Length; k++)
            {
                var (glyph, text) = rows[k];
                if (k < i) { glyph.Text = "✓"; glyph.ForeColor = Green; text.ForeColor = Ink; }
                else if (k == i) { glyph.Text = "●"; glyph.ForeColor = Cyan; text.ForeColor = Cyan; }
            }
            bar.Value = (i + 0.5f) / rows.Length;
        }
        try
        {
            var result = await Task.Run(() => Setup.Install(autostart, i => BeginInvoke(() => Mark(i))));
            Mark(rows.Length);
            ShowDone(result);
        }
        catch (Exception ex)
        {
            Para("");
            Para("No se pudo completar: " + ex.Message, Pink);
            Para("Lo que se había copiado se ha deshecho.", Muted);
            Button("Cerrar", primary: true, Close);
        }
    }

    private void ShowDone(Setup.Result r)
    {
        Reset();
        Title("✓  GhostDeck está instalado", null, Green);
        Para(r.ProfileNote is { } p
            ? $"Se abrirá en la bandeja del sistema cada vez que inicies sesión. La interfaz de MSI responde: perfil actual {p}."
            : "Se abrirá en la bandeja del sistema cada vez que inicies sesión.");
        if (r.NeedsReboot) Warn("El componente de MSI se instaló, pero Windows aún no lo ha activado. Reinicia el equipo para activarlo.");
        else if (Setup.PlanSchema() == Setup.SchemaPlan.Missing) Warn("Falta el componente de MSI: hasta que instales MSI Center una vez, GhostDeck solo podrá leer, no cambiar perfiles.");
        Button("Abrir GhostDeck", primary: true, () =>
        {
            try { Process.Start(new ProcessStartInfo(Setup.InstalledExe) { UseShellExecute = true, WorkingDirectory = Setup.InstallDir }); } catch { }
            Close();
        });
        Button("Cerrar", primary: false, Close);
    }

    private async void RunUninstall()
    {
        Reset();
        Title("Desinstalando…", null);
        try
        {
            await Task.Run(Setup.Uninstall);
            Reset();
            Title("✓  GhostDeck se ha desinstalado", null, Green);
            Para("Tus ajustes siguen en %AppData%\\GhostDeck por si vuelves a instalarlo.");
            Button("Cerrar", primary: true, Close);
        }
        catch (Exception ex)
        {
            Para("No se pudo completar: " + ex.Message, Pink);
            Button("Cerrar", primary: true, Close);
        }
    }

    // ---------------- building blocks ----------------

    private static Version Norm(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    private static string DeviceLine()
    {
        try
        {
            string fw = Ec.ReadFirmware();
            if (fw.Length == 0) return "Interfaz de MSI no disponible todavía";
            return Devices.Detect(fw) is { } d ? $"{d.Name} · {fw}" : $"Modelo no reconocido · {fw}";
        }
        catch { return "Interfaz de MSI no disponible todavía"; }
    }

    private void Reset()
    {
        SuspendLayout();
        _body.Controls.Clear();
        _buttons.Controls.Clear();
        ResumeLayout();
    }

    private void Title(string text, string? cyanTail, Color? color = null)
    {
        if (cyanTail != null && text.Contains("Ghost" + cyanTail))
        {
            // the wordmark: "Ghost" white, "Deck" cyan, then the rest of the line
            int at = text.IndexOf("Ghost" + cyanTail, StringComparison.Ordinal);
            _body.Controls.Add(new Wordmark(text[..(at + 5)], cyanTail, text[(at + 5 + cyanTail.Length)..]) { Margin = new Padding(0, 0, 0, 2) });
        }
        else _body.Controls.Add(L(text, 15f, FontStyle.Bold, color ?? Ink, new Padding(0, 0, 0, 4)));
    }

    private void Subtitle(string text) => _body.Controls.Add(L(text, 10f, FontStyle.Regular, Muted, new Padding(0, 2, 0, 4)));
    private void Gap(int h) => _body.Controls.Add(new Panel { Height = h, Width = 1, Margin = new Padding(0), BackColor = Bg });

    private void Para(string text, Color? color = null)
    {
        var l = L(text, 10f, FontStyle.Regular, color ?? Muted, new Padding(0, 4, 0, 4));
        l.MaximumSize = new Size(530, 0);
        _body.Controls.Add(l);
    }

    private void Warn(string text)
    {
        var p = new Label
        {
            Text = "!   " + text, AutoSize = true, MaximumSize = new Size(530, 0), Padding = new Padding(10, 8, 10, 8),
            Margin = new Padding(0, 10, 0, 0), BackColor = Color.FromArgb(0x24, 0x1C, 0x0B), ForeColor = Color.FromArgb(0xFF, 0xE2, 0xB0),
            Font = new Font("Segoe UI", 9.5f),
        };
        _body.Controls.Add(p);
    }

    private void Check(bool ok, string text) => Item(ok ? "✓" : "○", ok ? Green : Faint, text, Ink);

    private (Label Glyph, Label Text) Item(string glyph, Color glyphColor, string text, Color textColor)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 4), BackColor = Bg };
        var g = L(glyph, 10f, FontStyle.Bold, glyphColor, new Padding(0, 0, 6, 0));
        g.AutoSize = false; g.Size = new Size(22, 22);
        var t = L(text, 10f, FontStyle.Regular, textColor, new Padding(0));
        t.MaximumSize = new Size(500, 0);
        row.Controls.Add(g);
        row.Controls.Add(t);
        _body.Controls.Add(row);
        return (g, t);
    }

    private static Label L(string text, float size, FontStyle style, Color color, Padding margin) => new()
    {
        Text = text, AutoSize = true, ForeColor = color, BackColor = Bg, Margin = margin,
        Font = new Font("Segoe UI", size, style),
    };

    private void Button(string text, bool primary, Action click, Color? fill = null)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, Padding = new Padding(10, 4, 10, 4), FlatStyle = FlatStyle.Flat,
            BackColor = primary ? fill ?? Blue : Bg, ForeColor = primary ? Color.White : fill ?? Ink,
            Font = new Font("Segoe UI", 10f, primary ? FontStyle.Bold : FontStyle.Regular),
            Margin = new Padding(8, 0, 0, 0), Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderColor = primary ? b.BackColor : fill ?? Color.FromArgb(0x32, 0x3E, 0x58);
        b.FlatAppearance.BorderSize = primary ? 0 : 1;
        b.Click += (_, _) => click();
        _buttons.Controls.Add(b);
        if (primary) AcceptButton = b;
    }

    /// <summary>"Ghost" + cyan "Deck" + the rest, painted as one run (separate labels leave gaps).</summary>
    private sealed class Wordmark : Control
    {
        private readonly string _a, _b, _c;
        private readonly Font _f = new("Segoe UI", 16f, FontStyle.Bold);
        private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        public Wordmark(string a, string b, string c)
        {
            _a = a; _b = b; _c = c; DoubleBuffered = true; BackColor = Bg;
            var sz = TextRenderer.MeasureText(a + b + c, _f, Size.Empty, Flags);
            Size = new Size(sz.Width + 8, sz.Height + 4);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            int x = 0;
            foreach (var (s, col) in new[] { (_a, Ink), (_b, Cyan), (_c, Ink) })
            {
                TextRenderer.DrawText(e.Graphics, s, _f, new Point(x, 0), col, Flags);
                x += TextRenderer.MeasureText(e.Graphics, s, _f, Size.Empty, Flags).Width;
            }
        }
    }

    /// <summary>A flat progress bar in the installer's palette (the stock one cannot be recoloured).</summary>
    private sealed class ProgressStrip : Control
    {
        private float _v;
        public float Value { get => _v; set { _v = Math.Clamp(value, 0, 1); Invalidate(); } }
        public ProgressStrip() { DoubleBuffered = true; }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.FromArgb(0x1A, 0x22, 0x32));
            using var b = new SolidBrush(Blue);
            e.Graphics.FillRectangle(b, 0, 0, Width * _v, Height);
        }
    }

    /// <summary>Entry from Program.Main: runs the installer elevated.</summary>
    public static int Run(string[] args)
    {
        if (!Elevation.IsElevated) return Elevation.Relaunch(args, wait: false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SetupForm(args is ["--uninstall", ..]));
        return 0;
    }
}
