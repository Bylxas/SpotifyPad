using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SpotifyPad;

public enum AppTheme { Auto, Light, Dark }

/// <summary>Colors and styling for the light and dark design. Forms call <see cref="Apply"/> once after building their controls.</summary>
static class Theme
{
    public static bool Dark { get; private set; }

    public static Color Back { get; private set; }
    public static Color Card { get; private set; }
    public static Color Control { get; private set; }
    public static Color ControlHover { get; private set; }
    public static Color Border { get; private set; }
    public static Color Text { get; private set; }
    public static Color Subtle { get; private set; }
    public static Color Accent { get; private set; }
    public static Color AccentHover { get; private set; }
    public static Color OnAccent => Color.Black;
    public static Color Success { get; private set; }
    public static Color Warning { get; private set; }

    public static Font BaseFont { get; } = MakeFont(9.75f, FontStyle.Regular);
    public static Font TitleFont { get; } = MakeFont(15f, FontStyle.Bold);
    public static Font SectionFont { get; } = MakeFont(10.5f, FontStyle.Bold);

    /// <summary>Auto follows the Windows "app mode" setting.</summary>
    public static void Init(AppTheme theme)
    {
        Dark = theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => WindowsUsesDarkApps(),
        };

        if (Dark)
        {
            Back = Hex(0x1C1C1C); Card = Hex(0x303030); Control = Hex(0x3E3E3E); ControlHover = Hex(0x4C4C4C);
            Border = Hex(0x555555); Text = Hex(0xFFFFFF); Subtle = Hex(0xC4C4C4);
            Accent = Hex(0x1ED760); AccentHover = Hex(0x3BE477); Success = Hex(0x1ED760); Warning = Hex(0xF5A524);
        }
        else
        {
            Back = Hex(0xF3F3F3); Card = Hex(0xFFFFFF); Control = Hex(0xFBFBFB); ControlHover = Hex(0xEDEDED);
            Border = Hex(0xDCDCDC); Text = Hex(0x1B1B1B); Subtle = Hex(0x6B6B6B);
            Accent = Hex(0x1DB954); AccentHover = Hex(0x1ED760); Success = Hex(0x16803C); Warning = Hex(0xB35C00);
        }
    }

    static bool WindowsUsesDarkApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }

    static Color Hex(int rgb) => Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

    static Font MakeFont(float size, FontStyle style)
    {
        // Windows 11's UI font, with the classic one as fallback
        var font = new Font("Segoe UI Variable Text", size, style);
        return font.Name == "Segoe UI Variable Text" ? font : new Font("Segoe UI", size, style);
    }

    /// <summary>Styles a form and all its controls; call after the controls are added.</summary>
    public static void Apply(Form form)
    {
        form.Font = BaseFont;
        form.BackColor = Back;
        form.ForeColor = Text;
        form.HandleCreated += (_, _) => StyleTitleBar(form.Handle);
        StyleChildren(form);
    }

    static void StyleChildren(Control parent)
    {
        foreach (Control c in parent.Controls)
        {
            switch (c)
            {
                case Button b:
                    StyleButton(b, primary: b.Tag as string == Primary);
                    break;
                case ModernCheckBox:
                    // Paints itself from the theme colors
                    break;
                case CheckBox cb:
                    cb.FlatStyle = FlatStyle.Flat;
                    cb.FlatAppearance.BorderColor = Subtle;
                    cb.FlatAppearance.CheckedBackColor = Accent;
                    cb.FlatAppearance.MouseOverBackColor = parent.BackColor;
                    cb.Cursor = Cursors.Hand;
                    break;
                case ComboBox combo:
                    // Standard style keeps the native (themed) drop-down arrow, dark via DarkMode_CFD
                    combo.FlatStyle = FlatStyle.Standard;
                    combo.BackColor = Control;
                    combo.ForeColor = Text;
                    combo.HandleCreated += (_, _) => SetWindowTheme(combo.Handle, Dark ? "DarkMode_CFD" : "CFD", null);
                    break;
                case ListView list:
                    list.BackColor = Card;
                    list.ForeColor = Text;
                    list.BorderStyle = BorderStyle.None;
                    list.HandleCreated += (_, _) => StyleListView(list);
                    // The themed header text is too dark in dark mode, so the header is drawn here; rows stay native
                    list.OwnerDraw = true;
                    list.DrawItem += (_, e) => e.DrawDefault = true;
                    list.DrawSubItem += (_, e) => e.DrawDefault = true;
                    list.DrawColumnHeader += (_, e) => DrawHeader(e, list.Font);
                    break;
                case TextBoxBase box:
                    box.BackColor = Control;
                    box.ForeColor = Text;
                    box.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case NumericUpDown num:
                    num.BackColor = Control;
                    num.ForeColor = Text;
                    num.BorderStyle = BorderStyle.FixedSingle;
                    // The spin buttons are a child window of their own
                    num.HandleCreated += (_, _) =>
                    {
                        foreach (Control part in num.Controls)
                            SetWindowTheme(part.Handle, Dark ? "DarkMode_Explorer" : "Explorer", null);
                    };
                    break;
            }
            if (c.ContextMenuStrip != null) StyleMenu(c.ContextMenuStrip);
            StyleChildren(c);
        }
    }

    /// <summary>Tag for the one highlighted button of a window (Save, OK, Record).</summary>
    public const string Primary = "primary";

    static void StyleButton(Button b, bool primary)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.Cursor = Cursors.Hand;
        b.UseVisualStyleBackColor = false;
        if (primary)
        {
            b.BackColor = Accent;
            b.ForeColor = OnAccent;
            b.Font = new Font(BaseFont, FontStyle.Bold);
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = AccentHover;
            b.FlatAppearance.MouseDownBackColor = Accent;
        }
        else
        {
            b.BackColor = Control;
            b.ForeColor = Text;
            b.FlatAppearance.BorderColor = Border;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.MouseOverBackColor = ControlHover;
            b.FlatAppearance.MouseDownBackColor = Border;
        }
    }

    static void StyleListView(ListView list)
    {
        // Native dark scrollbars/selection, and a dark column header
        SetWindowTheme(list.Handle, Dark ? "DarkMode_Explorer" : "Explorer", null);
        var header = SendMessage(list.Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
        if (header != IntPtr.Zero) SetWindowTheme(header, Dark ? "DarkMode_ItemsView" : "ItemsView", null);
    }

    static void DrawHeader(DrawListViewColumnHeaderEventArgs e, Font font)
    {
        using (var back = new SolidBrush(Card)) e.Graphics.FillRectangle(back, e.Bounds);
        using (var line = new Pen(Border)) e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        var textRect = Rectangle.Inflate(e.Bounds, -6, 0);
        TextRenderer.DrawText(e.Graphics, e.Header?.Text, new Font(font, FontStyle.Bold), textRect, Subtle,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    static void StyleTitleBar(IntPtr hwnd)
    {
        int dark = Dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        // Windows 11: title bar in the window's background color for a seamless look (ignored on Windows 10)
        int caption = ColorTranslator.ToWin32(Back);
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
    }

    public static void StyleMenu(ToolStrip menu)
    {
        menu.Renderer = new MenuRenderer();
        menu.BackColor = Card;
        menu.ForeColor = Text;
        menu.Font = BaseFont;
    }

    sealed class MenuRenderer() : ToolStripProfessionalRenderer(new MenuColors())
    {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Text : Subtle;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Text;
            base.OnRenderArrow(e);
        }
    }

    sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Card;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => ControlHover;
        public override Color MenuItemSelected => ControlHover;
        public override Color MenuItemSelectedGradientBegin => ControlHover;
        public override Color MenuItemSelectedGradientEnd => ControlHover;
        public override Color MenuItemPressedGradientBegin => Control;
        public override Color MenuItemPressedGradientEnd => Control;
        public override Color ImageMarginGradientBegin => Card;
        public override Color ImageMarginGradientMiddle => Card;
        public override Color ImageMarginGradientEnd => Card;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Card;
        public override Color CheckBackground => Control;
        public override Color CheckSelectedBackground => ControlHover;
        public override Color CheckPressedBackground => ControlHover;
    }

    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    const int DWMWA_CAPTION_COLOR = 35;
    const int LVM_GETHEADER = 0x101F;

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string? app, string? idList);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
}

/// <summary>A rounded "card" section with an optional title, holding one content control.</summary>
sealed class Card : Panel
{
    const int Radius = 10;

    public Card(string? title, Control content, bool fill = false)
    {
        DoubleBuffered = true;
        BackColor = Theme.Card;
        Padding = new Padding(16, 12, 16, 14);
        Margin = new Padding(0, 0, 0, 12);
        Dock = DockStyle.Fill;
        if (!fill)
        {
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
        }

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = !fill, BackColor = Theme.Card, Margin = new Padding(0) };
        if (title != null)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label { Text = title, Font = Theme.SectionFont, AutoSize = true, Margin = new Padding(0, 0, 0, 8) });
        }
        layout.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
        content.Dock = DockStyle.Fill;
        layout.Controls.Add(content);
        Controls.Add(layout);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Corners show the parent's color; the card itself is a rounded rectangle with a hairline border
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Back);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), Radius);
        using var fill = new SolidBrush(Theme.Card);
        using var pen = new Pen(Theme.Border);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(pen, path);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }

    internal static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>Slim rounded bar for the live volume.</summary>
sealed class VolumeBar : Control
{
    int value;

    public VolumeBar()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        Size = new Size(240, 10);
    }

    /// <summary>0–100, or null when unknown (drawn as an empty track).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int? Value
    {
        get => value < 0 ? null : value;
        set
        {
            int v = value is int x ? Math.Clamp(x, 0, 100) : -1;
            if (v == this.value) return;
            this.value = v;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int radius = Math.Max(1, Height / 2 - 1);
        var track = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Card.RoundedRect(track, radius))
        using (var brush = new SolidBrush(Theme.Border))
            g.FillPath(brush, path);
        if (value > 0)
        {
            int w = Math.Max(Height, (int)(track.Width * value / 100f));
            using var path = Card.RoundedRect(new Rectangle(0, 0, w, Height - 1), radius);
            using var brush = new SolidBrush(Theme.Accent);
            g.FillPath(brush, path);
        }
    }
}

/// <summary>
/// Check box drawn in the theme colors, either as a box (green with a check mark when on)
/// or as a toggle switch (green track with the knob on the right when on).
/// </summary>
sealed class ModernCheckBox : CheckBox
{
    const int BoxSize = 18, SwitchWidth = 40, SwitchHeight = 22, Gap = 10;
    bool hover;

    public ModernCheckBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        AutoSize = true;
    }

    /// <summary>Draw as a toggle switch instead of a check box.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Switch { get; set; }

    int IndicatorWidth => Switch ? SwitchWidth : BoxSize;
    int IndicatorHeight => Switch ? SwitchHeight : BoxSize;

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPrefix);
        return new Size(IndicatorWidth + Gap + text.Width + 2, Math.Max(text.Height, IndicatorHeight) + 6);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int cy = Height / 2;

        if (Switch)
        {
            var track = new Rectangle(1, cy - SwitchHeight / 2, SwitchWidth - 2, SwitchHeight - 1);
            using var path = Card.RoundedRect(track, track.Height / 2);
            if (Checked)
            {
                using var fill = new SolidBrush(hover ? Theme.AccentHover : Theme.Accent);
                g.FillPath(fill, path);
            }
            else
            {
                using var fill = new SolidBrush(Theme.Control);
                using var pen = new Pen(hover ? Theme.Text : Theme.Subtle, 1.5f);
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }
            int knob = SwitchHeight - 9;
            int kx = Checked ? track.Right - knob - 5 : track.X + 5;
            using var knobBrush = new SolidBrush(Checked ? Theme.OnAccent : Theme.Subtle);
            g.FillEllipse(knobBrush, kx, cy - knob / 2f - 0.5f, knob, knob);
        }
        else
        {
            var box = new Rectangle(1, cy - BoxSize / 2, BoxSize - 2, BoxSize - 2);
            using var path = Card.RoundedRect(box, 4);
            if (Checked)
            {
                using var fill = new SolidBrush(hover ? Theme.AccentHover : Theme.Accent);
                using var check = new Pen(Theme.OnAccent, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                g.FillPath(fill, path);
                g.DrawLines(check, [new PointF(box.X + 4, cy), new PointF(box.X + 7.5f, cy + 3.5f), new PointF(box.Right - 4, cy - 4)]);
            }
            else
            {
                using var pen = new Pen(hover ? Theme.Text : Theme.Subtle, 1.5f);
                g.DrawPath(pen, path);
            }
        }

        var textRect = new Rectangle(IndicatorWidth + Gap, 0, Width - IndicatorWidth - Gap, Height);
        TextRenderer.DrawText(g, Text, Font, textRect, Enabled ? ForeColor : Theme.Subtle,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        hover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnCheckedChanged(EventArgs e)
    {
        base.OnCheckedChanged(e);
        Invalidate();
    }
}
