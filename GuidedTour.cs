using System.Drawing;
using System.Windows.Forms;

namespace SteamAchievementUnlocker;

public record TourStep(string Title, string Text, Control Target, Action Reveal);

// Explains the real UI without invoking any action handler or Steam request.
public sealed class GuidedTour : Form
{
    private readonly IReadOnlyList<TourStep> _steps;
    private readonly Form _owner;
    private readonly Label _heading = new() { AutoSize = true, Font = new("Segoe UI", 13, FontStyle.Bold) };
    private readonly Label _body = new() { AutoSize = true, MaximumSize = new(460, 0) };
    private readonly ThemeButton _back = new() { Text = "Back", AutoSize = true };
    private readonly ThemeButton _next = new() { Text = "Next", AutoSize = true };
    private Control? _frameParent;
    private Rectangle _frame;
    private int _index;
    public int StepIndex => _index;
    public bool Completed { get; private set; }
    public GuidedTour(Form owner, IReadOnlyList<TourStep> steps, string theme)
    {
        if (steps.Count == 0) throw new ArgumentException("Tour has no steps.");
        _owner = owner; _steps = steps;
        Text = "Welcome · Guided tour"; Font = owner.Font; AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog; MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
        ClientSize = new(510, 265); StartPosition = FormStartPosition.Manual;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(18), ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.AutoSize));
        var content = new Panel { Dock = DockStyle.Fill, AutoScroll = true }; content.Controls.Add(_body);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var skip = new ThemeButton { Text = "Skip tour", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.AddRange(new Control[] { _back, _next, skip });
        layout.Controls.Add(_heading, 0, 0); layout.Controls.Add(content, 0, 1); layout.Controls.Add(buttons, 0, 2); Controls.Add(layout);
        _back.Click += (_, _) => MoveTo(_index - 1);
        _next.Click += (_, _) => { if (_index == _steps.Count - 1) { Completed = true; Close(); } else MoveTo(_index + 1); };
        CancelButton = skip; AcceptButton = _next;
        DesktopTheme.Apply(this, theme);
        Shown += (_, _) => MoveTo(0);
        FormClosed += (_, _) => RemoveFrame();
    }
    public void MoveTo(int index)
    {
        RemoveFrame(); _index = Math.Clamp(index, 0, _steps.Count - 1);
        var step = _steps[_index]; step.Reveal();
        var ancestors = new List<Control>();
        for (Control? p = step.Target; p is not null; p = p.Parent) ancestors.Add(p);
        foreach (var page in ancestors.OfType<TabPage>().Reverse()) if (page.Parent is TabControl tabs) tabs.SelectedTab = page;
        foreach (var scroll in ancestors.OfType<ScrollableControl>().Reverse()) scroll.ScrollControlIntoView(step.Target);
        _owner.PerformLayout();
        _heading.Text = $"{_index + 1}/{_steps.Count} · {step.Title}"; _body.Text = step.Text;
        _back.Enabled = _index > 0; _next.Text = _index == _steps.Count - 1 ? "Finish" : "Next";
        _frameParent = step.Target.Parent;
        if (_frameParent is not null)
        {
            _frame = step.Target.Bounds; _frame.Inflate(3, 3);
            _frameParent.Paint += PaintFrame; _frameParent.Invalidate(_frame); _frameParent.Update();
        }
        var area = Screen.FromControl(_owner).WorkingArea;
        var target = step.Target.RectangleToScreen(step.Target.ClientRectangle);
        int x = Math.Clamp(_owner.Right - Width - 24, area.Left, Math.Max(area.Left, area.Right - Width));
        int y = Math.Clamp(_owner.Bottom - Height - 40, area.Top, Math.Max(area.Top, area.Bottom - Height));
        if (new Rectangle(x, y, Width, Height).IntersectsWith(target)) y = Math.Clamp(_owner.Top + 50, area.Top, Math.Max(area.Top, area.Bottom - Height));
        Location = new(x, y);
        AccessibleDescription = step.Title + ": " + step.Text;
    }
    private void PaintFrame(object? sender, PaintEventArgs e)
    { using var pen = new Pen(Color.FromArgb(0, 210, 230), 3); e.Graphics.DrawRectangle(pen, _frame); }
    private void RemoveFrame()
    {
        if (_frameParent is null) return;
        _frameParent.Paint -= PaintFrame; _frameParent.Invalidate(_frame); _frameParent = null;
    }
    protected override void Dispose(bool disposing) { if (disposing) RemoveFrame(); base.Dispose(disposing); }
}
