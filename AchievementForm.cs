using System.Drawing;
using System.Windows.Forms;

namespace SteamAchievementUnlocker;

public sealed class AchievementForm : Form
{
    private bool _saving;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Image> _images = new();
    private readonly Action _check;
    private readonly System.Windows.Forms.Timer _neonTimer = new() { Interval = 40 };
    internal void SmokeChecks() { Show(); try { _check(); } finally { Hide(); } }
    public AchievementForm(SteamGame game, List<AchievementInfo> achievements, Func<string[], Task<DesktopResult>> unlock, HttpClient? http = null, bool animations = true)
    {
        Text = game.Name + " — Achievements"; Size = new(1100, 700); MinimumSize = new(850, 500);
        StartPosition = FormStartPosition.CenterParent; Font = new Font("Segoe UI", 10);
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(18), RowCount = 4, ColumnCount = 1 };
        panel.RowStyles.Add(new(SizeType.Absolute, 45)); panel.RowStyles.Add(new(SizeType.Absolute, 45)); panel.RowStyles.Add(new(SizeType.Percent, 100)); panel.RowStyles.Add(new(SizeType.Absolute, 52));
        var summary = new Label { AutoSize = true }; panel.Controls.Add(summary);
        var search = new ThemeTextBox { PlaceholderText = "Search achievements and descriptions", Width = 330 };
        var filter = new ThemeComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 }; filter.Items.AddRange(new object[] { "Locked", "Unlocked", "All" }); filter.SelectedIndex = 0;
        var sort = new ThemeComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 }; sort.Items.AddRange(new object[] { "Name A–Z", "Rarest first" }); sort.SelectedIndex = 0;
        var rareOnly = new CheckBox { Text = "Rare only (≤5%)", AutoSize = true };
        var filters = new FlowLayoutPanel { Dock = DockStyle.Fill }; filters.Controls.AddRange(new Control[] { search, filter, sort, rareOnly }); panel.Controls.Add(filters);
        var grid = new ArtworkGrid { Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, EnableHeadersVisualStyles = false };
        grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Select", FillWeight = 7 });
        grid.Columns.Add(new DataGridViewImageColumn { Name = "Icon", ImageLayout = DataGridViewImageCellLayout.Zoom, FillWeight = 10, ReadOnly = true, DefaultCellStyle = new() { NullValue = null } });
        foreach (var (name, weight) in new[] { ("Achievement", 28), ("Description", 45), ("State", 15), ("Global unlock rate", 25) }) grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = name, FillWeight = weight, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.RowTemplate.Height = 58; grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True; panel.Controls.Add(grid);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill };
        var all = new ThemeButton { Text = "Select visible locked", AutoSize = true }; var clear = new ThemeButton { Text = "Clear selection", AutoSize = true };
        var save = new ThemeButton { Text = "Unlock selected", AutoSize = true }; var close = new ThemeButton { Text = "Close", AutoSize = true }; close.Click += (_, _) => Close();
        buttons.Controls.AddRange(new Control[] { all, clear, save, close }); panel.Controls.Add(buttons); Controls.Add(panel);
        var selected = new HashSet<string>(StringComparer.Ordinal); var icons = new Dictionary<string, Image>(StringComparer.Ordinal);
        foreach (var a in achievements.Where(a => a.IconPng is { Length: > 0 }))
        {
            try { using var stream = new MemoryStream(a.IconPng!); using var source = Image.FromStream(stream); var image = new Bitmap(source); icons[a.ApiName] = image; _images.Add(image); }
            catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException) { }
        }
        var global = new Dictionary<string, double>(StringComparer.Ordinal); bool rebuilding = false; bool loading = http is not null;
        bool Rare(AchievementInfo a) => RarityStyle.IsRare(global.TryGetValue(a.ApiName, out double percent) ? percent : null);
        grid.RowPostPaint += (_, e) =>
        {
            if (grid.Rows[e.RowIndex].Tag is not AchievementInfo a || !Rare(a)) return;
            var rect = e.RowBounds; rect.X += 2; rect.Y += 2; rect.Width = Math.Min(rect.Width, grid.ClientSize.Width) - 5; rect.Height -= 5;
            if (rect.Width <= 0 || rect.Height <= 0) return;
            double intensity = RarityStyle.Intensity(Environment.TickCount64, animations && !_saving && ContainsFocus);
            Color neon = global[a.ApiName] <= 1 ? Color.FromArgb(235, 95, 255) : Color.FromArgb(30, 225, 255);
            using var glow = new Pen(Color.FromArgb((int)(55 * intensity), neon), 6);
            using var border = new Pen(Color.FromArgb((int)(240 * intensity), neon), 2);
            e.Graphics.DrawRectangle(glow, rect); e.Graphics.DrawRectangle(border, rect);
        };
        _neonTimer.Tick += (_, _) =>
        {
            if (!animations || _saving || !ContainsFocus || WindowState == FormWindowState.Minimized) return;
            foreach (DataGridViewRow row in grid.Rows) if (row.Displayed && row.Tag is AchievementInfo a && Rare(a)) grid.InvalidateRow(row.Index);
        };
        void Counts()
        {
            summary.Text = $"{achievements.Count(a => a.Unlocked)}/{achievements.Count} unlocked · {selected.Count} selected · Global rates: {(loading ? "loading…" : global.Count == 0 ? "Unavailable" : "Steam, cached up to 24h")}";
            save.Enabled = !_saving && selected.Count > 0;
        }
        void Rebuild()
        {
            string query = search.Text.Trim();
            var visible = achievements.Where(a => filter.SelectedIndex == 2 || a.Unlocked == (filter.SelectedIndex == 1))
                .Where(a => !rareOnly.Checked || Rare(a))
                .Where(a => query.Length == 0 || a.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) || a.Description.Contains(query, StringComparison.OrdinalIgnoreCase) || a.ApiName.Contains(query, StringComparison.OrdinalIgnoreCase));
            visible = sort.SelectedIndex == 1 ? visible.OrderBy(a => global.GetValueOrDefault(a.ApiName, 101)).ThenBy(a => a.DisplayName) : visible.OrderBy(a => a.DisplayName);
            var rows = visible.ToList(); selected.IntersectWith(rows.Where(a => !a.Unlocked).Select(a => a.ApiName));
            rebuilding = true; grid.Rows.Clear();
            foreach (var a in rows)
            {
                int index = grid.Rows.Add(selected.Contains(a.ApiName), icons.GetValueOrDefault(a.ApiName), a.DisplayName, a.Description, a.Unlocked ? "Unlocked" : "Locked",
                    global.TryGetValue(a.ApiName, out double percent) ? $"{percent:0.0}% of players" + (Rare(a) ? " · Rare" : "") : "Unavailable");
                grid.Rows[index].Tag = a; grid.Rows[index].Cells[0].ReadOnly = a.Unlocked; grid.Rows[index].Cells[2].ToolTipText = a.ApiName;
            }
            rebuilding = false; Counts(); _neonTimer.Enabled = animations && rows.Any(Rare);
        }
        grid.CurrentCellDirtyStateChanged += (_, _) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        grid.CellValueChanged += (_, e) =>
        {
            if (rebuilding || e.RowIndex < 0 || e.ColumnIndex != 0 || grid.Rows[e.RowIndex].Tag is not AchievementInfo a || a.Unlocked) return;
            if (grid.Rows[e.RowIndex].Cells[0].Value is true) selected.Add(a.ApiName); else selected.Remove(a.ApiName); Counts();
        };
        all.Click += (_, _) => { foreach (DataGridViewRow r in grid.Rows) if (r.Tag is AchievementInfo { Unlocked: false } a) selected.Add(a.ApiName); Rebuild(); };
        clear.Click += (_, _) => { selected.Clear(); Rebuild(); };
        search.TextChanged += (_, _) => Rebuild(); filter.SelectedIndexChanged += (_, _) => Rebuild(); sort.SelectedIndexChanged += (_, _) => Rebuild();
        rareOnly.CheckedChanged += (_, _) => Rebuild();
        save.Click += async (_, _) =>
        {
            grid.EndEdit(); var names = selected.ToArray(); if (names.Length == 0) return;
            if (MessageBox.Show(this, $"Unlock {names.Length} selected achievements in {game.Name}?", "Confirm achievement changes", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            _saving = true; foreach (Control c in new Control[] { save, all, clear, close, grid, search, filter, sort, rareOnly }) c.Enabled = false; summary.Text = "Waiting for Steam confirmation…";
            try { var result = await unlock(names); MessageBox.Show(this, result.Status, "Steam result", MessageBoxButtons.OK, result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            { MessageBox.Show(this, "The operation or local history could not be completed. Reopen the game to read its current progress before retrying.", "Operation incomplete", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { _saving = false; Close(); }
        };
        Shown += async (_, _) =>
        {
            if (http is null) return;
            var fetched = await GlobalAchievements.LoadAsync(http, game.AppId, Path.Combine(UserSettings.DirectoryPath, "global-achievements"), _lifetime.Token);
            if (IsDisposed || _saving) return;
            global = fetched; loading = false; Rebuild();
        };
        FormClosing += (_, e) => { if (_saving) e.Cancel = true; else _lifetime.Cancel(); };
        Rebuild();
        _check = () =>
        {
            void Require(bool condition) { if (!condition) throw new InvalidOperationException("Achievement browser mismatch."); }
            DesktopTheme.Apply(this, "Dark");
            Require(BackColor == Color.Black && grid.BackgroundColor == Color.Black);
            foreach (DataGridViewColumn column in grid.Columns)
            {
                if (grid.Rows.Count > 0) grid.CurrentCell = grid.Rows[0].Cells[column.Index];
                Require(grid.ColumnHeadersDefaultCellStyle.SelectionBackColor == grid.ColumnHeadersDefaultCellStyle.BackColor && grid.ColumnHeadersDefaultCellStyle.SelectionForeColor == grid.ColumnHeadersDefaultCellStyle.ForeColor);
            }
            DesktopTheme.Apply(this, "Light"); Require(grid.BackgroundColor == Color.White);
            DesktopTheme.Apply(this, "Dark");
            Require(grid.Rows.Count == achievements.Count(a => !a.Unlocked));
            all.PerformClick(); Require(selected.Count == achievements.Count(a => !a.Unlocked));
            search.Text = "__no_matching_achievement__"; Require(grid.Rows.Count == 0 && selected.Count == 0);
            search.Clear(); filter.SelectedIndex = 1; all.PerformClick(); Require(selected.Count == 0);
            filter.SelectedIndex = 2; Require(grid.Rows.Count == achievements.Count);
            global[achievements[0].ApiName] = 2.5; rareOnly.Checked = true; Require(grid.Rows.Count == 1);
            global.Clear(); Rebuild(); Require(grid.Rows.Count == 0);
            rareOnly.Checked = false; global[achievements[0].ApiName] = 2.5;
            if (achievements.Count > 1) global[achievements[1].ApiName] = 0.5;
            Rebuild();
            if (Environment.GetEnvironmentVariable("SAU_NEON_SCREENSHOT") is { Length: > 0 } screenshot)
            {
                DesktopTheme.Apply(this, "Dark");
                using var bitmap = new Bitmap(Width, Height); DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));
                bitmap.Save(screenshot, System.Drawing.Imaging.ImageFormat.Png);
            }
        };
    }
    protected override void Dispose(bool disposing) { if (disposing) { _neonTimer.Dispose(); _lifetime.Cancel(); foreach (var image in _images) image.Dispose(); } base.Dispose(disposing); }
}
