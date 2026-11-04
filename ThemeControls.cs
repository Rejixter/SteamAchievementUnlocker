using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SteamAchievementUnlocker;

internal static class NativeTheme
{
    private sealed class State { public bool Dark; }
    private static readonly ConditionalWeakTable<Control, State> States = new();
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr hwnd, string? app, string? id);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    public static void Apply(Control control, bool dark)
    {
        var state = States.GetValue(control, c =>
        {
            var created = new State(); c.HandleCreated += (_, _) => Update(c, created.Dark); return created;
        });
        state.Dark = dark;
        if (control.IsHandleCreated) Update(control, dark);
    }
    private static void Update(Control control, bool dark)
    {
        SetWindowTheme(control.Handle, dark ? "DarkMode_Explorer" : null, null);
        if (control is Form)
        {
            int value = dark ? 1 : 0; DwmSetWindowAttribute(control.Handle, 20, ref value, sizeof(int));
            int caption = dark ? 0 : -1, text = dark ? 0xFFFFFF : -1;
            DwmSetWindowAttribute(control.Handle, 35, ref caption, sizeof(int)); DwmSetWindowAttribute(control.Handle, 36, ref text, sizeof(int));
        }
    }
}

internal sealed class ThemeTabControl : TabControl
{
    public bool Dark { get; set; }
    public ThemeTabControl()
    { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
    protected override void OnPaint(PaintEventArgs e)
    {
        Color background = Dark ? Color.Black : Color.FromArgb(246, 247, 251);
        e.Graphics.Clear(background);
        using var border = new Pen(Dark ? Color.FromArgb(55, 55, 55) : Color.Silver);
        var body = DisplayRectangle; body.Inflate(1, 1); e.Graphics.DrawRectangle(border, body);
        for (int i = 0; i < TabCount; i++)
        {
            var rect = GetTabRect(i);
            using var fill = new SolidBrush(i == SelectedIndex ? (Dark ? Color.FromArgb(23, 23, 23) : Color.White) : background);
            e.Graphics.FillRectangle(fill, rect); e.Graphics.DrawRectangle(border, rect);
            TextRenderer.DrawText(e.Graphics, TabPages[i].Text, Font, rect, ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (i == SelectedIndex) { using var accent = new Pen(Color.FromArgb(120, 110, 240), 2); e.Graphics.DrawLine(accent, rect.Left + 2, rect.Bottom - 2, rect.Right - 2, rect.Bottom - 2); }
            if (Focused && i == SelectedIndex) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(rect, -3, -3), ForeColor, BackColor);
        }
    }
}

internal sealed class ThemeComboBox : ComboBox
{
    public bool Dark { get; set; }
    public ThemeComboBox() { DrawMode = DrawMode.OwnerDrawFixed; FlatStyle = FlatStyle.Flat; }
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using var brush = new SolidBrush(selected ? (Dark ? Color.FromArgb(40, 40, 40) : Color.FromArgb(225, 228, 250)) : BackColor);
        e.Graphics.FillRectangle(brush, e.Bounds);
        TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, e.Bounds, ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        bool print = m.Msg is 0x0317 or 0x0318;
        if ((m.Msg != 0x000F && !print) || !Dark || !IsHandleCreated) return;
        using var g = print && m.WParam != IntPtr.Zero ? Graphics.FromHdc(m.WParam) : Graphics.FromHwnd(Handle); using var fill = new SolidBrush(BackColor); g.FillRectangle(fill, ClientRectangle);
        var text = new Rectangle(4, 0, Math.Max(1, Width - 25), Height);
        TextRenderer.DrawText(g, Text, Font, text, Enabled ? ForeColor : Color.Gray, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        using var border = new Pen(Color.FromArgb(70, 70, 70)); g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        using var arrow = new SolidBrush(Enabled ? ForeColor : Color.Gray); int x = Width - 13, y = Height / 2;
        g.FillPolygon(arrow, new[] { new Point(x - 4, y - 2), new Point(x + 4, y - 2), new Point(x, y + 2) });
    }
}

internal sealed class ThemeProgressBar : ProgressBar
{
    public ThemeProgressBar() { SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        int width = Maximum > Minimum ? (int)((long)ClientSize.Width * (Value - Minimum) / (Maximum - Minimum)) : 0;
        using var fill = new SolidBrush(Color.FromArgb(120, 110, 240)); e.Graphics.FillRectangle(fill, 0, 0, width, ClientSize.Height);
    }
}

internal sealed class ThemeTextBox : TextBox
{
    public bool Dark { get; set; }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr hwnd);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        bool print = m.Msg is 0x0317 or 0x0318;
        if (!Dark || BorderStyle == BorderStyle.None || (m.Msg != 0x0085 && m.Msg != 0x000F && !print)) return;
        var dc = print ? m.WParam : GetWindowDC(Handle);
        if (dc == IntPtr.Zero) return;
        try
        {
            using var graphics = Graphics.FromHdc(dc); using var pen = new Pen(Color.FromArgb(65, 65, 65));
            graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            if (BorderStyle == BorderStyle.Fixed3D) graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
        }
        finally { if (!print) ReleaseDC(Handle, dc); }
    }
}

internal sealed class ThemeButton : Button
{
    public bool Dark { get; set; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!Dark || Enabled) return;
        using var fill = new SolidBrush(BackColor); e.Graphics.FillRectangle(fill, ClientRectangle);
        using var border = new Pen(Color.FromArgb(50, 50, 50)); e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Color.FromArgb(145, 145, 145), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}
