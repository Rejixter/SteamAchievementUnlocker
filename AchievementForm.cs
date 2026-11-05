using System.Drawing;
using System.Windows.Forms;

namespace SteamAchievementUnlocker;

public sealed class AchievementForm : Form
{
    private bool _saving;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Image> _images = new();
    private readonly Action _check;
    private readonly CancellationTokenSource _iconLifetime = new();
    private Task? _iconsTask;
    private readonly Func<(int Icons, int Rare)> _visualState;
    private readonly System.Windows.Forms.Timer _searchDelay = new() { Interval = 180 };
    private readonly System.Windows.Forms.Timer _neonTimer = new() { Interval = 40 };
    internal void SmokeChecks() { Show(); try { _check(); } finally { Hide(); } }
    public AchievementForm(SteamGame game, List<AchievementInfo> achievements, Func<string[], Task<DesktopResult>> unlock, HttpClient? http = null, bool animations = true, Func<string[], Action<AchievementInfo>, CancellationToken, Task<string>>? loadIcons = null, Task<Dictionary<string, double>>? ratesTask = null, AchievementIconCache? iconCache = null)
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
        var cache = iconCache ?? new AchievementIconCache(Path.Combine(UserSettings.DirectoryPath, "achievement-icons"));
        bool cacheEnabled = http is not null || loadIcons is not null || iconCache is not null;
        string iconsStatus = loadIcons is null ? "cached" : "loading…";
        var rowIndex = new Dictionary<string, DataGridViewRow>(StringComparer.Ordinal);
        var missingCache = new HashSet<string>(StringComparer.Ordinal);
        var placeholder = new Bitmap(48, 48);
        using (var g = Graphics.FromImage(placeholder)) { g.Clear(Color.FromArgb(24, 24, 24)); using var pen = new Pen(Color.Gray, 2); g.DrawRectangle(pen, 9, 9, 29, 29); g.DrawLine(pen, 10, 36, 24, 20); g.DrawLine(pen, 24, 20, 38, 36); }
        _images.Add(placeholder);
        grid.Columns["Icon"].DefaultCellStyle.NullValue = placeholder;
        var selected = new HashSet<string>(StringComparer.Ordinal); var icons = new Dictionary<string, Image>(StringComparer.Ordinal);
        foreach (var a in achievements.Where(a => a.IconPng is { Length: > 0 }))
        {
            try { using var stream = new MemoryStream(a.IconPng!); using var source = Image.FromStream(stream); var image = new Bitmap(source); icons[a.ApiName] = image; _images.Add(image); }
            catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException) { }
        }
        string ratesDirectory = Path.Combine(UserSettings.DirectoryPath, "global-achievements");
        var global = http is null ? new Dictionary<string, double>(StringComparer.Ordinal) : GlobalAchievements.ReadCached(game.AppId, ratesDirectory);
        if (ratesTask is { IsCompletedSuccessfully: true }) global = ratesTask.Result;
        bool rebuilding = false; bool loading = http is not null && global.Count == 0;
        IEnumerable<DataGridViewRow> DisplayedRows()
        {
            int first = grid.FirstDisplayedScrollingRowIndex;
            if (first < 0) return Array.Empty<DataGridViewRow>();
            return Enumerable.Range(first, Math.Min(grid.RowCount - first, grid.DisplayedRowCount(true) + 1)).Select(i => grid.Rows[i]);
        }
        void InstallIcon(AchievementInfo a, byte[] bytes)
        {
            if (!rowIndex.TryGetValue(a.ApiName, out var row) || !row.Displayed || icons.ContainsKey(a.ApiName)) return;
            try
            {
                using var stream = new MemoryStream(bytes); using var source = Image.FromStream(stream);
                var image = new Bitmap(source); icons[a.ApiName] = image; _images.Add(image); row.Cells[1].Value = image;
            }
            catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException) { }
        }
        void LoadVisibleCachedIcons()
        {
            if (!cacheEnabled || rebuilding || IsDisposed) return;
            var visible = DisplayedRows().Where(r => r.Tag is AchievementInfo).Select(r => (AchievementInfo)r.Tag!).ToList();
            foreach (var a in visible.Where(a => !icons.ContainsKey(a.ApiName) && !missingCache.Contains(a.ApiName)))
            {
                var bytes = cache.Read(game.AppId, a.ApiName, a.Unlocked);
                if (bytes is null) missingCache.Add(a.ApiName); else InstallIcon(a, bytes);
            }
            var ids = visible.Select(a => a.ApiName).ToHashSet(StringComparer.Ordinal);
            foreach (string old in icons.Keys.Where(k => !ids.Contains(k)).Take(Math.Max(0, icons.Count - 128)).ToArray())
            {
                var image = icons[old];
                if (rowIndex.TryGetValue(old, out var row)) row.Cells[1].Value = null;
                icons.Remove(old); _images.Remove(image); image.Dispose();
            }
        }
        grid.Scroll += (_, _) => LoadVisibleCachedIcons(); grid.Resize += (_, _) => LoadVisibleCachedIcons();
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
        void UpdateNeonTimer() => _neonTimer.Enabled = animations && !_saving && ContainsFocus && WindowState != FormWindowState.Minimized && DisplayedRows().Any(r => r.Tag is AchievementInfo a && Rare(a));
        _neonTimer.Tick += (_, _) =>
        {
            if (!ContainsFocus || _saving || WindowState == FormWindowState.Minimized) { _neonTimer.Stop(); return; }
            foreach (var row in DisplayedRows()) if (row.Tag is AchievementInfo a && Rare(a)) grid.InvalidateRow(row.Index);
        };
        Activated += (_, _) => UpdateNeonTimer(); Deactivate += (_, _) => _neonTimer.Stop();
        grid.Scroll += (_, _) => UpdateNeonTimer(); Resize += (_, _) => UpdateNeonTimer();
        void Counts()
        {
            summary.Text = $"{achievements.Count(a => a.Unlocked)}/{achievements.Count} unlocked · {selected.Count} selected · Icons: {iconsStatus} · Global rates: {(loading ? "loading…" : global.Count == 0 ? "Unavailable" : "Steam, cached up to 24h")}";
            save.Enabled = !_saving && selected.Count > 0;
        }
        void Rebuild()
        {
            _searchDelay.Stop();
            string query = search.Text.Trim();
            var visible = achievements.Where(a => filter.SelectedIndex == 2 || a.Unlocked == (filter.SelectedIndex == 1))
                .Where(a => !rareOnly.Checked || Rare(a))
                .Where(a => query.Length == 0 || a.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) || a.Description.Contains(query, StringComparison.OrdinalIgnoreCase) || a.ApiName.Contains(query, StringComparison.OrdinalIgnoreCase));
            visible = sort.SelectedIndex == 1 ? visible.OrderBy(a => global.GetValueOrDefault(a.ApiName, 101)).ThenBy(a => a.DisplayName) : visible.OrderBy(a => a.DisplayName);
            var rows = visible.ToList(); selected.IntersectWith(rows.Where(a => !a.Unlocked).Select(a => a.ApiName));
            rebuilding = true; grid.SuspendLayout(); grid.Rows.Clear(); rowIndex.Clear();
            foreach (var a in rows)
            {
                int index = grid.Rows.Add(selected.Contains(a.ApiName), icons.GetValueOrDefault(a.ApiName), a.DisplayName, a.Description, a.Unlocked ? "Unlocked" : "Locked",
                    global.TryGetValue(a.ApiName, out double percent) ? $"{percent:0.0}% of players" + (Rare(a) ? " · Rare" : "") : "Unavailable");
                rowIndex[a.ApiName] = grid.Rows[index];
                grid.Rows[index].Tag = a; grid.Rows[index].Cells[0].ReadOnly = a.Unlocked; grid.Rows[index].Cells[2].ToolTipText = a.ApiName;
            }
            grid.ResumeLayout(); rebuilding = false; LoadVisibleCachedIcons(); Counts(); UpdateNeonTimer();
        }
        grid.CurrentCellDirtyStateChanged += (_, _) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        grid.CellValueChanged += (_, e) =>
        {
            if (rebuilding || e.RowIndex < 0 || e.ColumnIndex != 0 || grid.Rows[e.RowIndex].Tag is not AchievementInfo a || a.Unlocked) return;
            if (grid.Rows[e.RowIndex].Cells[0].Value is true) selected.Add(a.ApiName); else selected.Remove(a.ApiName); Counts();
        };
        all.Click += (_, _) => { foreach (DataGridViewRow r in grid.Rows) if (r.Tag is AchievementInfo { Unlocked: false } a) selected.Add(a.ApiName); Rebuild(); };
        clear.Click += (_, _) => { selected.Clear(); Rebuild(); };
        _searchDelay.Tick += (_, _) => Rebuild();
        search.TextChanged += (_, _) => { _searchDelay.Stop(); _searchDelay.Start(); }; filter.SelectedIndexChanged += (_, _) => Rebuild(); sort.SelectedIndexChanged += (_, _) => Rebuild();
        rareOnly.CheckedChanged += (_, _) => Rebuild();
        save.Click += async (_, _) =>
        {
            Rebuild(); grid.EndEdit(); var names = selected.ToArray(); if (names.Length == 0) return;
            if (MessageBox.Show(this, $"Unlock {names.Length} selected achievements in {game.Name}?", "Confirm achievement changes", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            _saving = true; foreach (Control c in new Control[] { save, all, clear, close, grid, search, filter, sort, rareOnly }) c.Enabled = false; summary.Text = "Waiting for Steam confirmation…";
            try { _iconLifetime.Cancel(); if (_iconsTask is not null) await _iconsTask; var result = await unlock(names); MessageBox.Show(this, result.Status, "Steam result", MessageBoxButtons.OK, result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            { MessageBox.Show(this, "The operation or local history could not be completed. Reopen the game to read its current progress before retrying.", "Operation incomplete", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { _saving = false; Close(); }
        };
        async Task LoadIcons()
        {
            if (loadIcons is null) { LoadVisibleCachedIcons(); return; }
            try
            {
                var missing = await Task.Run(() => achievements.Where(a => cache.Read(game.AppId, a.ApiName, a.Unlocked) is null).Select(a => a.ApiName).ToArray(), _iconLifetime.Token);
                if (_lifetime.IsCancellationRequested || _iconLifetime.IsCancellationRequested) return;
                if (missing.Length == 0) { iconsStatus = "from cache"; LoadVisibleCachedIcons(); Counts(); return; }
                iconsStatus = await loadIcons(missing, a =>
                {
                    if (IsDisposed || _saving || _lifetime.IsCancellationRequested || _iconLifetime.IsCancellationRequested) return;
                    if (!achievements.Any(original => original.ApiName == a.ApiName && original.Unlocked == a.Unlocked) || a.IconPng is null) return;
                    cache.Save(game.AppId, a.ApiName, a.Unlocked, a.IconPng); missingCache.Remove(a.ApiName); InstallIcon(a, a.IconPng);
                }, _iconLifetime.Token);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception) { iconsStatus = "unavailable"; }
            if (!IsDisposed && !_saving && !_lifetime.IsCancellationRequested) Counts();
        }
        Shown += async (_, _) =>
        {
            LoadVisibleCachedIcons(); _iconsTask = LoadIcons();
            if (http is null) return;
            var fetched = await (ratesTask ?? GlobalAchievements.LoadAsync(http, game.AppId, ratesDirectory, _lifetime.Token));
            if (IsDisposed || _saving || _lifetime.IsCancellationRequested) return;
            global = fetched; loading = false;
            if (rareOnly.Checked || sort.SelectedIndex == 1) Rebuild();
            else
            {
                foreach (DataGridViewRow row in grid.Rows) if (row.Tag is AchievementInfo a)
                    row.Cells[5].Value = global.TryGetValue(a.ApiName, out double percent) ? $"{percent:0.0}% of players" + (Rare(a) ? " · Rare" : "") : "Unavailable";
                Counts(); UpdateNeonTimer(); grid.Invalidate();
            }
        };
        FormClosing += (_, e) => { if (_saving) e.Cancel = true; else { _lifetime.Cancel(); _iconLifetime.Cancel(); } };
        Rebuild();
        _visualState = () => (icons.Count, achievements.Count(Rare));
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
            search.Text = "__no_matching_achievement__"; Rebuild(); Require(grid.Rows.Count == 0 && selected.Count == 0);
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
    internal static async Task VisualSmokeChecks(Form owner)
    {
        void Require(bool ok, string name) { if (!ok) throw new InvalidOperationException("Visual loading: " + name); Console.WriteLine("PASS: " + name); }
        string directory = Path.Combine(Path.GetTempPath(), "sau-visual-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new AchievementIconCache(directory);
            using var fixture = new Bitmap(64, 64);
            using (var g = Graphics.FromImage(fixture)) { g.Clear(Color.DarkOrange); using var pen = new Pen(Color.White, 5); g.DrawEllipse(pen, 12, 12, 40, 40); }
            using var output = new MemoryStream(); fixture.Save(output, System.Drawing.Imaging.ImageFormat.Png); byte[] png = output.ToArray();
            cache.Save(10, "FIRST", false, png);
            var items = new List<AchievementInfo> { new("FIRST", "Cached achievement", false, "Available immediately from disk"), new("SECOND", "Streaming achievement", false, "Arrives after the window is already visible") };
            var rates = Task.FromResult(new Dictionary<string, double> { ["FIRST"] = 0.5, ["SECOND"] = 3 });
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int requests = 0;
            async Task<string> Loader(string[] names, Action<AchievementInfo> ready, CancellationToken token)
            { requests++; Require(names.SequenceEqual(new[] { "SECOND" }), "only missing icons are requested"); await gate.Task.WaitAsync(token); ready(items[1] with { IconPng = png }); return "loaded"; }
            using (var browser = new AchievementForm(new(10, "Visual loading test", true), items, _ => throw new InvalidOperationException(), animations: true, loadIcons: Loader, ratesTask: rates, iconCache: cache))
            {
                browser.Show(owner); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (requests == 0) await Task.Delay(10, timeout.Token);
                Require(browser.Visible && browser._visualState().Icons == 1, "cached icon is visible while the background request is pending");
                Require(browser._visualState().Rare == 2, "known rarity frames are ready without waiting for icons");
                gate.SetResult(); while (browser._visualState().Icons < 2) await Task.Delay(10, timeout.Token);
                Require(cache.Read(10, "SECOND", false) is not null, "background icon is rendered and persisted");
                if (Environment.GetEnvironmentVariable("SAU_VISUAL_SCREENSHOT") is { Length: > 0 } path)
                { DesktopTheme.Apply(browser, "Dark"); using var image = new Bitmap(browser.Width, browser.Height); browser.DrawToBitmap(image, new Rectangle(Point.Empty, browser.Size)); image.Save(path, System.Drawing.Imaging.ImageFormat.Png); }
                browser.OnDeactivate(EventArgs.Empty); Require(!browser._neonTimer.Enabled, "neon timer stops when the window deactivates"); browser.Close();
            }
            using (var reopened = new AchievementForm(new(10, "Visual cache reopen", true), items, _ => throw new InvalidOperationException(), animations: false, loadIcons: Loader, ratesTask: rates, iconCache: cache))
            {
                reopened.Show(owner); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (reopened._iconsTask is null) await Task.Delay(10, timeout.Token);
                await reopened._iconsTask;
                Require(reopened._visualState().Icons == 2 && requests == 1, $"reopening uses cached icons without another icon worker (icons={reopened._visualState().Icons}, requests={requests})");
                Require(!reopened._neonTimer.Enabled && reopened._visualState().Rare == 2, "reduced motion retains static rare frames without a timer"); reopened.Close();
            }
            bool waiting = false;
            async Task<string> WaitingLoader(string[] names, Action<AchievementInfo> ready, CancellationToken token)
            { waiting = true; await Task.Delay(Timeout.Infinite, token); return "unexpected"; }
            using (var closing = new AchievementForm(new(11, "Cancellation test", true), items, _ => throw new InvalidOperationException(), loadIcons: WaitingLoader, iconCache: cache))
            {
                closing.Show(owner); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (!waiting) await Task.Delay(10, timeout.Token);
                closing.Close(); await closing._iconsTask!.WaitAsync(timeout.Token);
                Require(closing._iconsTask.IsCompleted, "closing the window cancels outstanding visual loading");
            }
        }
        finally
        {
            string full = Path.GetFullPath(directory), parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (Path.GetDirectoryName(full) == parent && Path.GetFileName(full).StartsWith("sau-visual-test-", StringComparison.Ordinal) && Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
    protected override void Dispose(bool disposing) { if (disposing) { _neonTimer.Dispose(); _searchDelay.Dispose(); _lifetime.Cancel(); _iconLifetime.Cancel(); foreach (var image in _images) image.Dispose(); } base.Dispose(disposing); }
}
