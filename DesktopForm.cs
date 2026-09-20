using System.Drawing;
using System.Windows.Forms;

namespace SteamAchievementUnlocker;

public sealed class DesktopForm : Form
{
    private readonly bool _demo;
    private readonly HttpClient _http = new();
    private readonly AchievementCatalog _catalog;
    private LibraryState _state = new();
    private List<SteamGame> _main = new(), _family = new(), _visible = new();
    private readonly HashSet<uint> _checked = new();
    private readonly Dictionary<uint, string> _results = new();
    private readonly TextBox _search = new() { PlaceholderText = "Search games or AppID", Width = 300 };
    private readonly CheckBox _hideComplete = new() { Text = "Hide 100% complete", AutoSize = true };
    private readonly CheckBox _hideWithout = new() { Text = "Hide games without achievements", Checked = true, AutoSize = true };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly DataGridView _grid = new();
    private readonly Label _counts = new() { AutoSize = true, Padding = new(0, 7, 0, 0) };
    private readonly Label _status = new() { Text = "Ready", AutoSize = true, Padding = new(0, 6, 0, 0) };
    private readonly Label _hint = new() { AutoSize = true, MaximumSize = new(1200, 0), ForeColor = Color.DimGray };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Height = 8 };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
    private readonly List<Control> _operationControls = new();
    private readonly Button _stop;
    private readonly Button _unlock;
    private readonly Button _restore;
    private readonly Button _exclude;
    private bool _busy, _stopRequested, _rebuilding, _closeAfter;
    private CancellationTokenSource? _scan;
    private ulong _account;
    private string? _statePath;

    public static void Run(bool demo, bool selfTest)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using var form = new DesktopForm(demo);
                if (selfTest)
                    form.Shown += (_, _) => form.BeginInvoke(new Action(() =>
                    {
                        try { form.SmokeChecks(); Console.WriteLine("PASS: desktop search, selection, tabs, progress filters and exclusions."); }
                        catch (Exception ex) { failure = ex; }
                        finally { form.Close(); }
                    }));
                Application.Run(form);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null)
        {
            Environment.ExitCode = 1;
            if (selfTest) Console.Error.WriteLine("Desktop self-test failed: " + failure.Message);
            else MessageBox.Show("The desktop window could not start. Re-extract the full Windows release ZIP.", "Steam Achievement Unlocker", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public DesktopForm(bool demo)
    {
        _demo = demo;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SteamAchievementUnlocker/1.4.0");
        _catalog = new(_http, demo ? Path.Combine(Path.GetTempPath(), "sau-demo-unused-" + Guid.NewGuid() + ".json") : Path.Combine(UserSettings.DirectoryPath, "achievement-cache.json"));
        Text = "Steam Achievement Unlocker v1.4.0" + (demo ? " — DEMO (no Steam connection)" : "");
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(246, 247, 251);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(980, 700);
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 900);
        Size = new Size(Math.Min(1200, area.Width - 40), Math.Min(880, area.Height - 40));
        AutoScaleMode = AutoScaleMode.Dpi;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, Padding = new(24, 18, 24, 18) };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 74));
        layout.RowStyles.Add(new(SizeType.Absolute, 45));
        layout.RowStyles.Add(new(SizeType.Absolute, 45));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 95));
        layout.RowStyles.Add(new(SizeType.Absolute, 35));
        layout.RowStyles.Add(new(SizeType.Absolute, 10));
        layout.RowStyles.Add(new(SizeType.Absolute, 112));
        Controls.Add(layout);
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        header.ColumnStyles.Add(new(SizeType.Percent, 100)); header.ColumnStyles.Add(new(SizeType.Absolute, 260));
        var title = new Label { Text = "Steam Achievement Unlocker", Font = new Font("Segoe UI", 21, FontStyle.Bold), AutoSize = true, ForeColor = Color.FromArgb(31, 36, 55) };
        var heading = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        heading.Controls.Add(title);
        heading.Controls.Add(new Label { Text = demo ? "DEMO · Sample games only · Steam is never initialized" : "Your library. Your selection. Confirmed results.   /   v1.4.0", AutoSize = true, ForeColor = Color.DimGray });
        header.Controls.Add(heading, 0, 0);
        var topActions = Flow();
        topActions.Controls.Add(ActionButton("Refresh library", async () => await RefreshLibrary()));
        topActions.Controls.Add(ActionButton("Settings", Settings));
        header.Controls.Add(topActions, 1, 0); layout.Controls.Add(header, 0, 0);
        var filters = Flow(); filters.Controls.Add(_search); filters.Controls.Add(_hideComplete); filters.Controls.Add(_hideWithout);
        foreach (Control c in filters.Controls) c.Margin = new Padding(0, 6, 18, 0);
        layout.Controls.Add(filters, 0, 1); layout.Controls.Add(_hint, 0, 2);
        _tabs.TabPages.Add("Main library"); _tabs.TabPages.Add("Family / cached"); _tabs.TabPages.Add("Excluded games");
        BuildGrid(); _tabs.TabPages[0].Controls.Add(_grid); layout.Controls.Add(_tabs, 0, 3);
        var actions = Flow();
        actions.Controls.Add(ActionButton("Select visible", () => { foreach (var g in _visible) _checked.Add(g.AppId); Rebuild(); return Task.CompletedTask; }));
        actions.Controls.Add(ActionButton("Clear selection", () => { _checked.Clear(); Rebuild(); return Task.CompletedTask; }));
        actions.Controls.Add(ActionButton("Read progress", async () => await RunQueue(false)));
        _unlock = ActionButton("Unlock selected", async () => await RunQueue(true));
        _unlock.BackColor = Color.FromArgb(79, 70, 210); _unlock.ForeColor = Color.White; _unlock.FlatStyle = FlatStyle.Flat;
        actions.Controls.Add(_unlock);
        actions.Controls.Add(ActionButton("Open achievements", OpenAchievements));
        _exclude = ActionButton("Exclude selected", () => { ChangeExclusions(false); return Task.CompletedTask; }); actions.Controls.Add(_exclude);
        _restore = ActionButton("Restore selected", () => { ChangeExclusions(true); return Task.CompletedTask; }); actions.Controls.Add(_restore);
        actions.Controls.Add(ActionButton("Check achievement support", ScanMetadata));
        actions.Controls.Add(ActionButton("Add AppID", AddGame));
        actions.Controls.Add(_counts); layout.Controls.Add(actions, 0, 4);
        var activity = Flow();
        _stop = new Button { Text = "Stop after current game", AutoSize = true, Enabled = false };
        _stop.Click += (_, _) => { _stopRequested = true; _scan?.Cancel(); _status.Text = "Stopping after the current operation…"; _stop.Enabled = false; };
        activity.Controls.Add(_stop); activity.Controls.Add(_status); layout.Controls.Add(activity, 0, 5);
        layout.Controls.Add(_progress, 0, 6); layout.Controls.Add(_log, 0, 7);
        _operationControls.AddRange(new Control[] { _search, _hideComplete, _hideWithout, _tabs });
        _search.TextChanged += (_, _) => Rebuild(); _hideComplete.CheckedChanged += (_, _) => Rebuild(); _hideWithout.CheckedChanged += (_, _) => Rebuild();
        _tabs.SelectedIndexChanged += (_, _) => { _checked.Clear(); _tabs.SelectedTab!.Controls.Add(_grid); Rebuild(); };
        Shown += async (_, _) => await Guard(RefreshLibrary);
        FormClosing += (_, e) =>
        {
            if (!_busy) return;
            e.Cancel = true;
            if (MessageBox.Show(this, "Stop after the current operation and close? Confirmed changes will stay saved.", "Operation in progress", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            { _closeAfter = true; _stopRequested = true; _scan?.Cancel(); }
        };
    }

    private static FlowLayoutPanel Flow() => new() { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true, Margin = new(0) };
    private Button ActionButton(string text, Func<Task> action)
    {
        var b = new Button { Text = text, AutoSize = true, Padding = new(9, 5, 9, 5), Margin = new(0, 3, 8, 3), BackColor = Color.White };
        b.Click += async (_, _) => await Guard(action); _operationControls.Add(b); return b;
    }
    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException or System.Text.Json.JsonException)
        { Log("Operation failed. Check Steam, the internet connection and access to your settings folder."); }
    }
    private void BuildGrid()
    {
        _grid.Dock = DockStyle.Fill; _grid.AllowUserToAddRows = false; _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false; _grid.RowHeadersVisible = false; _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; _grid.BackgroundColor = Color.White;
        _grid.BorderStyle = BorderStyle.None; _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.RowTemplate.Height = 36; _grid.ColumnHeadersHeight = 38;
        _grid.EnableHeadersVisualStyles = false; _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(233, 235, 244);
        _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(249, 250, 253);
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(225, 228, 250); _grid.DefaultCellStyle.SelectionForeColor = Color.Black;
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Selected", HeaderText = "Select", FillWeight = 7, MinimumWidth = 58 });
        foreach (var (name, weight) in new[] { ("Game", 32), ("AppID", 9), ("Progress", 17), ("Source", 18), ("Last result", 25) })
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = name, FillWeight = weight, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
        _grid.CurrentCellDirtyStateChanged += (_, _) => { if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _grid.CellValueChanged += (_, e) =>
        {
            if (_rebuilding || e.RowIndex < 0 || e.ColumnIndex != 0) return;
            var game = (SteamGame)_grid.Rows[e.RowIndex].Tag!;
            if (_grid.Rows[e.RowIndex].Cells[0].Value is true) _checked.Add(game.AppId); else _checked.Remove(game.AppId);
            UpdateCounts();
        };
        _grid.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0 && e.ColumnIndex != 0 && !_busy) await Guard(OpenAchievements); };
    }
    private void Rebuild()
    {
        if (_tabs.TabPages.Count == 0) return;
        bool excluded = _tabs.SelectedIndex == 2;
        IEnumerable<SteamGame> source = excluded ? _state.Excluded.Values : _tabs.SelectedIndex == 1 ? _family : _main;
        source = source.Select(g => g.FromCache ? g with { Name = _catalog.GetName(g.AppId) ?? g.Name } : g);
        if (!excluded) source = _catalog.Filter(source, _hideWithout.Checked);
        _visible = _state.Filter(source, _search.Text, _hideComplete.Checked, excluded);
        _checked.IntersectWith(_visible.Select(g => g.AppId));
        _rebuilding = true;
        try
        {
            _grid.Rows.Clear();
            foreach (var game in _visible)
            {
                string progress = _state.Progress.TryGetValue(game.AppId, out var p) ? p.Label : "Not checked";
                string origin = game.FromCache ? "Cached · unverified" : game.IsInstalled ? "Installed" : "Owned / manual";
                int row = _grid.Rows.Add(_checked.Contains(game.AppId), game.Name, game.AppId, progress, origin, _results.GetValueOrDefault(game.AppId, ""));
                _grid.Rows[row].Tag = game;
                _grid.Rows[row].Cells[3].ToolTipText = p is null ? "Use Read progress to load this account's achievements." : $"Last read: {p.CheckedAt.LocalDateTime:g}. Counts expire after 24 hours. Refresh library after switching Steam accounts.";
            }
        }
        finally { _rebuilding = false; }
        _restore.Visible = excluded; _exclude.Visible = !excluded; _unlock.Enabled = !_busy && !excluded;
        _hint.Text = excluded ? "Excluded games stay out of both libraries. Select games here to restore them."
            : _tabs.SelectedIndex == 1 ? "Additional cache candidates only. Family access is unverified; stale entries may appear."
            : "Check games to choose a batch. Read progress may show you as in-game on Steam. Refresh after switching accounts.";
        UpdateCounts();
    }
    private void UpdateCounts() => _counts.Text = $"{_visible.Count} visible · {_checked.Count} selected";
    private void Log(string text) { _status.Text = text.Length > 95 ? text[..92] + "…" : text; _log.AppendText(text + Environment.NewLine); }
    private void Busy(bool value)
    {
        _busy = value;
        foreach (var c in _operationControls) c.Enabled = !value;
        _stop.Enabled = value;
        if (value) { _stopRequested = false; _progress.Value = 0; }
        else { Rebuild(); if (_closeAfter) BeginInvoke(new Action(Close)); }
    }
    private void SaveState()
    {
        if (_demo || _statePath is null) return;
        try { _state.Save(_statePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Log("Changes are visible for this session, but settings could not be saved to disk."); }
    }
    private async Task RefreshLibrary()
    {
        Busy(true);
        try
        {
            _checked.Clear(); _results.Clear();
            if (_demo)
            {
                _account = 1;
                _main = new() { new(10, "Sample Adventure", true), new(20, "Sample Puzzle", false), new(30, "Sample Racing", true), new(40, "Sample Explorer", true) };
                _family = new() { new(50, "Sample Family Game", false, true) };
                _state = new(); _state.Progress[10] = new(18, 25, DateTimeOffset.UtcNow); _state.Progress[20] = new(10, 10, DateTimeOffset.UtcNow);
                Log("Demo ready. No Steam account is read or modified."); return;
            }
            Log("Loading library…");
            var loaded = await Task.Run(async () =>
            {
                string? path = SteamLibraryScanner.GetSteamInstallPath();
                ulong account = path is null ? 0 : SteamLibraryScanner.GetCurrentUserSteamId64(path) ?? 0;
                var installed = SteamLibraryScanner.GetInstalledGames();
                string key = UserSettings.ReadApiKey();
                var owned = account != 0 && key.Length > 0 ? await SteamLibraryScanner.TryGetOwnedGamesAsync(key, account) : null;
                var installedIds = installed.Select(g => g.AppId).ToHashSet();
                var main = owned?.Select(g => g with { IsInstalled = installedIds.Contains(g.AppId) }).ToList() ?? installed.ToList();
                var ids = main.Select(g => g.AppId).ToHashSet(); main.AddRange(installed.Where(g => ids.Add(g.AppId)));
                var family = SteamLibraryScanner.GetAdditionalCachedGames(main, SteamLibraryScanner.GetCachedLibraryGames());
                return (account, main, family, fallback: key.Length > 0 && owned is null);
            });
            _account = loaded.account; _main = loaded.main; _family = loaded.family;
            _statePath = LibraryState.PathForAccount(UserSettings.DirectoryPath, _account); _state = LibraryState.Load(_statePath);
            Log(loaded.fallback ? "Owned-library request unavailable. Showing installed games and separate cache candidates." : "Library loaded. Use Check achievement support or select games and Read progress.");
            if (_account == 0) Log("Steam account not detected. Sign in to Steam and refresh before reading or changing achievements.");
        }
        finally { Busy(false); }
    }
    private async Task ScanMetadata()
    {
        if (_demo) { Log("Metadata requests are disabled in demo mode."); return; }
        var snapshot = _visible.ToList(); Busy(true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90)); _scan = timeout;
        try
        {
            Log("Checking Store metadata (90-second limit)…");
            await _catalog.RefreshAsync(snapshot, new Progress<FilterProgress>(p => { _progress.Value = p.Total == 0 ? 0 : Math.Clamp(100 * p.Checked / p.Total, 0, 100); _status.Text = $"Checked {p.Checked}/{p.Total}"; }), timeout.Token);
            Log("Support check finished. Unknown games stay visible; run again to continue.");
        }
        finally { _scan = null; Busy(false); }
    }
    private bool CanUseSteam()
    {
        if (_demo) { Log("Steam operations are disabled in demo mode."); return false; }
        if (_account == 0) { Log("Sign in to Steam and refresh the library first."); return false; }
        return true;
    }
    private async Task RunQueue(bool unlock)
    {
        var games = LibraryState.SelectedVisible(_visible, _checked);
        if (games.Count == 0) { Log("Select at least one game using the checkboxes."); return; }
        if (_tabs.SelectedIndex == 2) { Log("Restore excluded games before processing them."); return; }
        if (!CanUseSteam()) return;
        if (unlock && !ConfirmGames(games)) return;
        Busy(true); var results = new List<BatchResult>();
        try
        {
            foreach (var game in games)
            {
                if (_stopRequested) break;
                Log($"{(unlock ? "Processing" : "Reading")} {results.Count + 1}/{games.Count}: {game.Name}");
                var result = await DesktopWorker.StartAsync(game.AppId, new(_account, unlock ? "unlock-all" : "read"));
                ApplyResult(game.AppId, result); results.Add(new(game.AppId, game.Name, result.Status));
                Log($"{game.Name}: {result.Status}"); _progress.Value = 100 * results.Count / games.Count;
                if (result.Status.StartsWith("Steam account changed", StringComparison.Ordinal)) { _stopRequested = true; break; }
            }
            SaveState();
            if (unlock && results.Count > 0)
            {
                try { BatchRunner.SaveReport(results); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log("The local batch report could not be saved."); }
            }
            Log($"Finished {results.Count}/{games.Count} games. Per-game results are shown above.");
        }
        finally { Busy(false); }
    }
    private void ApplyResult(uint id, DesktopResult result)
    {
        _results[id] = result.Status;
        if (result.Success && result.Achievements is not null)
        { _state.Record(id, result.Achievements); _catalog.RecordFromClient(id, result.Achievements.Count > 0); }
        else _state.Progress.Remove(id); // Never display optimistic local flags after an unconfirmed save.
    }
    private bool ConfirmGames(List<SteamGame> games)
    {
        using var dialog = new Form { Text = "Confirm achievement changes", Size = new(590, 410), MinimumSize = new(490, 350), StartPosition = FormStartPosition.CenterParent, Font = Font, MinimizeBox = false, MaximizeBox = false };
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(18), RowCount = 4, ColumnCount = 1 };
        panel.RowStyles.Add(new(SizeType.Absolute, 58)); panel.RowStyles.Add(new(SizeType.Percent, 100)); panel.RowStyles.Add(new(SizeType.Absolute, 38)); panel.RowStyles.Add(new(SizeType.Absolute, 45));
        panel.Controls.Add(new Label { Text = $"Unlock every locked achievement in these {games.Count} games?\nType UNLOCK ALL to confirm changes to your Steam profile.", AutoSize = true });
        var list = new ListBox { Dock = DockStyle.Fill }; list.Items.AddRange(games.Select(g => $"{g.Name} ({g.AppId})").ToArray()); panel.Controls.Add(list);
        var input = new TextBox { Dock = DockStyle.Fill, AccessibleName = "Confirmation text" }; panel.Controls.Add(input);
        var buttons = Flow(); var confirm = new Button { Text = "Unlock", Enabled = false, DialogResult = DialogResult.OK, AutoSize = true }; var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        input.TextChanged += (_, _) => confirm.Enabled = input.Text == "UNLOCK ALL";
        buttons.Controls.Add(confirm); buttons.Controls.Add(cancel); panel.Controls.Add(buttons); dialog.Controls.Add(panel); dialog.CancelButton = cancel;
        return dialog.ShowDialog(this) == DialogResult.OK;
    }
    private void ChangeExclusions(bool restore)
    {
        var games = LibraryState.SelectedVisible(_visible, _checked);
        if (games.Count == 0) { Log("Select games using the checkboxes first."); return; }
        foreach (var game in games)
        {
            if (restore)
            {
                _state.Excluded.Remove(game.AppId);
                if (!_main.Concat(_family).Any(g => g.AppId == game.AppId))
                { if (game.FromCache) _family.Add(game); else _main.Add(game); }
            }
            else _state.Excluded[game.AppId] = game;
        }
        _checked.Clear(); Rebuild(); Log($"{games.Count} games {(restore ? "restored" : "excluded")}."); SaveState();
    }
    private async Task OpenAchievements()
    {
        if (_grid.CurrentRow?.Tag is not SteamGame game) { Log("Choose a game row first."); return; }
        if (_tabs.SelectedIndex == 2) { Log("Restore the game before opening achievements."); return; }
        if (!CanUseSteam()) return;
        Busy(true);
        try
        {
            Log("Reading achievements for " + game.Name + "…");
            var result = await DesktopWorker.StartAsync(game.AppId, new(_account, "read")); ApplyResult(game.AppId, result); SaveState();
            if (_stopRequested || _closeAfter) return;
            if (!result.Success || result.Achievements is null) { Log(result.Status); return; }
            using var dialog = new AchievementForm(game, result.Achievements, async names =>
            {
                var saved = await DesktopWorker.StartAsync(game.AppId, new(_account, "unlock", names));
                ApplyResult(game.AppId, saved); SaveState(); Log(saved.Status); return saved;
            });
            dialog.ShowDialog(this);
        }
        finally { Busy(false); }
    }
    private Task AddGame()
    {
        string? input = TextPrompt("Add a game", "Enter a numeric Steam AppID:", false);
        if (input is null) return Task.CompletedTask;
        if (!uint.TryParse(input.Trim(), out uint id) || id == 0) { Log("Enter a valid nonzero numeric AppID."); return Task.CompletedTask; }
        if (_state.Excluded.ContainsKey(id)) { Log("This game is excluded. Restore it from Excluded games first."); return Task.CompletedTask; }
        if (!_main.Concat(_family).Any(g => g.AppId == id)) _main.Add(new(id, "AppID " + id, false));
        _search.Clear(); _tabs.SelectedIndex = _family.Any(g => g.AppId == id) ? 1 : 0; Rebuild(); Log("Game added for this session. Use Read progress to check Steam access.");
        return Task.CompletedTask;
    }
    private async Task Settings()
    {
        if (_demo) { Log("Settings are disabled in demo mode."); return; }
        string? key = TextPrompt("Optional Steam Web API key", "Enter your own key for owned, uninstalled games. Leave empty to remove it.\nStored as text in your Windows user folder, outside the release folder.", true);
        if (key is null) return;
        key = key.Trim();
        if (key.Length > 0 && !UserSettings.IsValidApiKey(key)) { Log("Expected a 32-character hexadecimal key. Nothing changed."); return; }
        Directory.CreateDirectory(UserSettings.DirectoryPath);
        if (key.Length == 0) File.Delete(UserSettings.KeyPath); else File.WriteAllText(UserSettings.KeyPath, key);
        await RefreshLibrary();
    }
    private string? TextPrompt(string title, string message, bool secret)
    {
        using var form = new Form { Text = title, Size = new(640, 225), StartPosition = FormStartPosition.CenterParent, Font = Font, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false };
        var label = new Label { Text = message, AutoSize = true, Location = new(18, 16) };
        var input = new TextBox { Location = new(18, 75), Width = 585, UseSystemPasswordChar = secret, AccessibleName = secret ? "API key" : "AppID", MaxLength = secret ? 32 : 10 };
        var save = new Button { Text = "Save", Location = new(18, 120), DialogResult = DialogResult.OK }; var cancel = new Button { Text = "Cancel", Location = new(105, 120), DialogResult = DialogResult.Cancel };
        form.Controls.AddRange(new Control[] { label, input, save, cancel }); form.AcceptButton = save; form.CancelButton = cancel;
        return form.ShowDialog(this) == DialogResult.OK ? input.Text : null;
    }
    private void SmokeChecks()
    {
        void Require(bool value) { if (!value) throw new InvalidOperationException("Desktop behavior mismatch."); }
        Require(_visible.Count == 4);
        _checked.Add(10); _search.Text = "Puzzle"; Require(_visible.Count == 1 && _checked.Count == 0);
        _search.Clear(); _hideComplete.Checked = true; Require(_visible.Count == 3);
        _hideComplete.Checked = false; _checked.Add(10); ChangeExclusions(false); Require(_visible.Count == 3);
        _tabs.SelectedIndex = 2; Require(_visible.Count == 1); _checked.Add(10); ChangeExclusions(true); Require(_visible.Count == 0);
        _tabs.SelectedIndex = 1; Require(_visible.Count == 1 && _visible[0].AppId == 50);
        _tabs.SelectedIndex = 0; Require(_visible.Count == 4 && !_visible.Any(g => g.AppId == 50));
    }
    protected override void Dispose(bool disposing)
    { if (disposing) { _scan?.Cancel(); _http.Dispose(); } base.Dispose(disposing); }
}
