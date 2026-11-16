// Dark theme for "Configurar AC4.exe", matching the compiler window
// (BuildForm): borderless window, header with the game art, a navigation
// sidebar instead of tabs, flat dark controls and an accent-blue primary
// button.  C# 5 / .NET Framework 4.x.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class Theme
{
    public static readonly Color Base = Color.FromArgb(8, 12, 20);
    public static readonly Color Surface = Color.FromArgb(12, 17, 27);
    public static readonly Color Nav = Color.FromArgb(9, 13, 21);
    public static readonly Color Field = Color.FromArgb(22, 30, 44);
    public static readonly Color FieldHover = Color.FromArgb(30, 41, 59);
    public static readonly Color Line = Color.FromArgb(40, 52, 70);
    public static readonly Color Ink = Color.FromArgb(236, 242, 250);
    public static readonly Color Muted = Color.FromArgb(150, 165, 185);
    public static readonly Color Accent = Color.FromArgb(76, 182, 255);
    public static readonly Color Accent2 = Color.FromArgb(132, 232, 255);
    public static readonly Color AccentDim = Color.FromArgb(28, 70, 110);

    public static readonly Font Body = new Font("Segoe UI", 9f);
    public static readonly Font Section = new Font("Segoe UI Semibold", 11f);
    public static readonly Font NavFont = new Font("Segoe UI Semibold", 10f);
    public static readonly Font ButtonFont = new Font("Segoe UI Semibold", 9.5f);
    public static readonly Font Label = new Font("Segoe UI Semibold", 8.5f);
    public static readonly Font Title = new Font("Segoe UI Semibold", 20f);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

    // dark scroll bars and drop-down lists (Windows 10 1809+; ignored elsewhere)
    static void DarkScroll(Control c)
    {
        EventHandler set = (o, e) => { try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { } };
        if (c.IsHandleCreated) set(c, EventArgs.Empty);
        else c.HandleCreated += set;
    }

    public static void Style(Control root)
    {
        foreach (Control c in root.Controls) Style(c);
        StyleOne(root);
    }

    static void StyleOne(Control c)
    {
        if (c is Form) return;
        if (c is Button)
        {
            var b = (Button)c;
            StyleButton(b, false);
            b.Padding = new Padding(6, 0, 6, 0);
            b.MinimumSize = new Size(b.Width, Math.Max(30, b.Height));
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowOnly;
            return;
        }
        if (c is DarkCheckBox) return;
        if (c is CheckBox)
        {
            var k = (CheckBox)c;
            k.FlatStyle = FlatStyle.Flat;
            k.FlatAppearance.BorderColor = Line;
            k.FlatAppearance.CheckedBackColor = Field;
            k.FlatAppearance.MouseOverBackColor = FieldHover;
            k.ForeColor = Ink;
            k.BackColor = Color.Transparent;
            k.Cursor = Cursors.Hand;
            return;
        }
        if (c is ComboBox) { StyleCombo((ComboBox)c); return; }
        if (c is CheckedListBox || c is ListBox)
        {
            var l = (ListBox)c;
            l.BackColor = Field; l.ForeColor = Ink; l.BorderStyle = BorderStyle.None;
            DarkScroll(c);
            return;
        }
        if (c is TextBox)
        {
            var t = (TextBox)c;
            t.BackColor = Field; t.ForeColor = Ink; t.BorderStyle = BorderStyle.FixedSingle;
            DarkScroll(c);
            return;
        }
        if (c is NumericUpDown)
        {
            var n = (NumericUpDown)c;
            n.BackColor = Field; n.ForeColor = Ink; n.BorderStyle = BorderStyle.FixedSingle;
            return;
        }
        if (c is TrackBar) { c.BackColor = Surface; return; }
        if (c is System.Windows.Forms.Label)
        {
            if (!"header".Equals(c.Tag) && !"muted".Equals(c.Tag)) c.ForeColor = Ink;
            c.BackColor = Color.Transparent;
            return;
        }
        if (c is SplitContainer)
        {
            var s = (SplitContainer)c;
            s.BackColor = Line;                    // the splitter
            s.Panel1.BackColor = s.Panel2.BackColor = Surface;
            return;
        }
        if (c is ScrollableControl)
        {
            if (!(c.Tag is string)) c.BackColor = Surface;
            c.ForeColor = Ink;
            if (((ScrollableControl)c).AutoScroll) DarkScroll(c);
        }
    }

    public static void StyleButton(Button b, bool primary)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.Font = ButtonFont;
        b.FlatAppearance.BorderSize = primary ? 0 : 1;
        b.FlatAppearance.BorderColor = Color.FromArgb(70, 90, 115);
        b.FlatAppearance.MouseOverBackColor = primary ? Accent2 : FieldHover;
        b.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(60, 150, 215) : Line;
        b.BackColor = primary ? Accent : Field;
        b.ForeColor = primary ? Color.FromArgb(6, 16, 28) : Ink;
        b.Cursor = Cursors.Hand;
        b.UseVisualStyleBackColor = false;
    }

    public static Button MakeButton(string text, bool primary, int minWidth)
    {
        var b = new Button();
        b.Text = text;
        StyleButton(b, primary);
        b.Height = 36;
        b.Width = Math.Max(minWidth, TextRenderer.MeasureText(text, ButtonFont).Width + 36);
        b.Margin = new Padding(8, 0, 0, 0);
        return b;
    }

    static void StyleCombo(ComboBox c)
    {
        c.FlatStyle = FlatStyle.Flat;
        c.BackColor = Field;
        c.ForeColor = Ink;
        if (c.DrawMode != DrawMode.Normal) return;     // styled already
        c.DrawMode = DrawMode.OwnerDrawFixed;
        c.ItemHeight = 20;
        c.DrawItem += (s, e) =>
        {
            if (e.Index < 0) return;
            bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0;
            bool sel = (e.State & DrawItemState.Selected) != 0 && !edit;
            using (var b = new SolidBrush(sel ? AccentDim : Field)) e.Graphics.FillRectangle(b, e.Bounds);
            var r = new Rectangle(e.Bounds.X + 5, e.Bounds.Y, e.Bounds.Width - 6, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, c.GetItemText(c.Items[e.Index]), c.Font, r, Ink,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
        DarkScroll(c);
    }

    // small dialogs (key capture, name prompt)
    public static void StyleDialog(Form f)
    {
        f.BackColor = Surface;
        f.ForeColor = Ink;
        f.Font = Body;
        Style(f);
        foreach (Control c in f.Controls)
            if (c is Button && (f.AcceptButton == c)) StyleButton((Button)c, true);
    }

    public static GraphicsPath Rounded(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void DrawSpaced(Graphics g, string text, Font f, Color c, float x, float y, float spacing)
    {
        using (var b = new SolidBrush(c))
            foreach (char ch in text)
            {
                string cs = ch.ToString();
                g.DrawString(cs, f, b, x, y, StringFormat.GenericTypographic);
                x += (ch == ' ' ? g.MeasureString("n", f, PointF.Empty, StringFormat.GenericTypographic).Width * 0.6f
                                : g.MeasureString(cs, f, PointF.Empty, StringFormat.GenericTypographic).Width) + spacing;
            }
    }
}

// Sidebar: one entry per settings page, the selected one with an accent bar.
class NavBar : Control
{
    public readonly List<string> Items = new List<string>();
    public event Action<int> Selected;
    int selected, hover = -1;
    const int Top0 = 18, ItemH = 42;

    public NavBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Nav;
        Cursor = Cursors.Hand;
    }

    public int SelectedIndex
    {
        get { return selected; }
        set { selected = value; Invalidate(); if (Selected != null) Selected(value); }
    }

    int HitTest(Point p)
    {
        int i = (p.Y - Top0) / ItemH;
        return p.Y >= Top0 && i >= 0 && i < Items.Count ? i : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = HitTest(e.Location);
        if (h != hover) { hover = h; Invalidate(); }
    }
    protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        int h = HitTest(e.Location);
        if (h >= 0 && h != selected) SelectedIndex = h;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        using (var pen = new Pen(Theme.Line)) g.DrawLine(pen, Width - 1, 0, Width - 1, Height);
        for (int i = 0; i < Items.Count; i++)
        {
            var r = new Rectangle(10, Top0 + i * ItemH, Width - 21, ItemH - 6);
            if (i == selected || i == hover)
                using (var path = Theme.Rounded(r, 6))
                using (var b = new SolidBrush(i == selected ? Theme.Field : Color.FromArgb(16, 23, 35)))
                    g.FillPath(b, path);
            if (i == selected)
                using (var b = new SolidBrush(Theme.Accent))
                using (var path = Theme.Rounded(new RectangleF(r.X, r.Y + 9, 3, r.Height - 18), 1.5f))
                    g.FillPath(b, path);
            TextRenderer.DrawText(g, Items[i], Theme.NavFont, new Rectangle(r.X + 16, r.Y, r.Width - 20, r.Height),
                                  i == selected ? Theme.Ink : Theme.Muted,
                                  TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }
}

// Check box drawn by hand: the flat WinForms one loses its tick on dark colours.
class DarkCheckBox : CheckBox
{
    bool hover;
    public DarkCheckBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Cursor = Cursors.Hand;
        Padding = new Padding(4, 0, 0, 0);
        MinimumSize = new Size(0, 24);
    }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
    public override Size GetPreferredSize(Size proposed)
    {
        var t = TextRenderer.MeasureText(Text, Font);
        return new Size(t.Width + 30, Math.Max(24, t.Height + 6));
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Color back = Parent != null ? Parent.BackColor : Theme.Surface;
        if (back.A < 255) back = Theme.Surface;
        g.Clear(back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var box = new RectangleF(1.5f, (Height - 16) / 2f, 16, 16);
        using (var path = Theme.Rounded(box, 3.5f))
        {
            using (var b = new SolidBrush(Checked ? Theme.Accent : hover ? Theme.FieldHover : Theme.Field)) g.FillPath(b, path);
            using (var pen = new Pen(Checked ? Theme.Accent : hover ? Theme.Muted : Color.FromArgb(70, 90, 115))) g.DrawPath(pen, path);
        }
        if (Checked)
            using (var pen = new Pen(Color.FromArgb(6, 16, 28), 2f))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                g.DrawLines(pen, new[] { new PointF(box.X + 4, box.Y + 8.5f), new PointF(box.X + 7, box.Y + 11.5f), new PointF(box.X + 12.5f, box.Y + 5) });
            }
        if (Focused && ShowFocusCues)
            using (var pen = new Pen(Theme.Accent2) { DashStyle = DashStyle.Dot })
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(26, 0, Width - 26, Height), Enabled ? Theme.Ink : Theme.Muted,
                              TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}

// Bottom bar with a hairline on top.
class FooterBar : FlowLayoutPanel
{
    public FooterBar()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Tag = "footer";
        BackColor = Theme.Base;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using (var pen = new Pen(Theme.Line)) e.Graphics.DrawLine(pen, 0, 0, Width, 0);
    }
}
