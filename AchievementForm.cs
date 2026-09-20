using System.Drawing;
using System.Windows.Forms;

namespace SteamAchievementUnlocker;

public sealed class AchievementForm : Form
{
    private bool _saving;
    public AchievementForm(SteamGame game, List<AchievementInfo> achievements, Func<string[], Task<DesktopResult>> unlock)
    {
        Text = game.Name + " — Achievements"; Size = new(820, 620); MinimumSize = new(680, 450);
        StartPosition = FormStartPosition.CenterParent; Font = new Font("Segoe UI", 10);
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(18), RowCount = 3, ColumnCount = 1 };
        panel.RowStyles.Add(new(SizeType.Absolute, 45)); panel.RowStyles.Add(new(SizeType.Percent, 100)); panel.RowStyles.Add(new(SizeType.Absolute, 50));
        var summary = new Label { AutoSize = true }; panel.Controls.Add(summary);
        var list = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, HorizontalScrollbar = true, IntegralHeight = false };
        var remaining = achievements.Where(a => !a.Unlocked).ToList();
        summary.Text = $"{achievements.Count(a => a.Unlocked)}/{achievements.Count} unlocked. Select locked achievements below.";
        list.Items.AddRange(remaining.Select(a => $"{a.DisplayName}  [{a.ApiName}]").ToArray()); panel.Controls.Add(list);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill };
        var all = new Button { Text = "Select all locked", AutoSize = true };
        var save = new Button { Text = "Unlock selected", AutoSize = true, Enabled = remaining.Count > 0 };
        var close = new Button { Text = "Close", AutoSize = true }; close.Click += (_, _) => Close();
        all.Click += (_, _) => { for (int i = 0; i < list.Items.Count; i++) list.SetItemChecked(i, true); };
        save.Click += async (_, _) =>
        {
            var names = list.CheckedIndices.Cast<int>().Select(i => remaining[i].ApiName).ToArray();
            if (names.Length == 0) return;
            if (MessageBox.Show(this, $"Unlock {names.Length} selected achievements in {game.Name}?", "Confirm achievement changes", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            _saving = true; save.Enabled = all.Enabled = close.Enabled = list.Enabled = false; summary.Text = "Waiting for Steam confirmation…";
            try
            {
                var result = await unlock(names);
                MessageBox.Show(this, result.Status, "Steam result", MessageBoxButtons.OK, result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            finally { _saving = false; Close(); } // Reopen to read fresh state, especially after a timeout.
        };
        FormClosing += (_, e) => { if (_saving) e.Cancel = true; };
        buttons.Controls.AddRange(new Control[] { all, save, close }); panel.Controls.Add(buttons); Controls.Add(panel);
    }
}
