using System.Drawing;
using System.Windows.Forms;

namespace SteamAchievementUnlocker;

public sealed partial class DesktopForm : Form
{
    private readonly bool _demo;
    private readonly ThemeTabControl _navigation = new() { Dock = DockStyle.Fill };
    private readonly Label _keyStatusLabel = new() { AutoSize = true, MaximumSize = new(700, 0) };
    private readonly ThemeTextBox _keyInput = new() { Width = 420, MaxLength = 32, UseSystemPasswordChar = true, AccessibleName = "New Steam API key" };
    private readonly DesktopPreferences _preferences;
    private readonly string _preferencesPath = Path.Combine(UserSettings.DirectoryPath, "desktop-preferences.json");
    private readonly ThemeComboBox _theme = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100, AccessibleName = "Theme" };
    private readonly ThemeComboBox _filter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170, AccessibleName = "Progress filter" };
    private readonly ThemeComboBox _sort = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 185, AccessibleName = "Sort games" };
    private readonly RetryQueue _retryQueue = new();
    private readonly Button _retry;
    private readonly Button _resume;
    private OperationJournal _journal = new();
    private string? _journalPath;
    private readonly ContextMenuStrip _menu = new();
    private readonly HttpClient _http = new();
    private readonly AchievementCatalog _catalog;
    private LibraryState _state = new();
    private List<SteamGame> _main = new(), _family = new(), _visible = new();
    private readonly HashSet<uint> _checked = new();
    private readonly Dictionary<uint, string> _results = new();
    private readonly ThemeTextBox _search = new() { PlaceholderText = "Search games or AppID", Width = 300 };
    private readonly CheckBox _hideComplete = new() { Text = "Hide 100% complete", AutoSize = true };
    private readonly CheckBox _hideWithout = new() { Text = "Hide games without achievements", Checked = true, AutoSize = true };
    private readonly ThemeTabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly DataGridView _grid = new ArtworkGrid();
    private readonly CheckBox _showCovers = new() { Text = "Show covers", Checked = true, AutoSize = true };
    private readonly CheckBox _animations = new() { Text = "Animations", Checked = true, AutoSize = true };
    private readonly CancellationTokenSource _artLifetime = new();
    private readonly Dictionary<uint, Bitmap> _artImages = new();
    private readonly Dictionary<uint, long> _artStarted = new();
    private readonly Dictionary<uint, long> _artMissing = new();
    private readonly HashSet<uint> _artAttempted = new();
    private readonly HashSet<uint> _artCached = new();
    private readonly Label _artStatus = new() { AutoSize = true, Padding = new(0, 7, 0, 0), Text = "Covers: waiting for library" };
    private readonly ThemeButton _pauseArtwork = new() { Text = "Pause cover downloads", AutoSize = true };
    private bool _artPaused;
    private bool _keyNoticeShown;
    private string _keyVerification = "Not verified in this session.";
    private readonly System.Windows.Forms.Timer _artTimer = new() { Interval = 40 };
    private readonly SteamArtwork _artwork;
    private bool _artLoading;
    private long _fadeStarted;
    private readonly Label _counts = new() { AutoSize = true, Padding = new(0, 7, 0, 0) };
    private readonly Label _status = new() { Text = "Ready", AutoSize = true, Padding = new(0, 6, 0, 0) };
    private readonly Label _hint = new() { AutoSize = true, MaximumSize = new(1200, 0), ForeColor = Color.DimGray };
    private readonly ThemeProgressBar _progress = new() { Dock = DockStyle.Fill, Height = 8 };
    private readonly ThemeTextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
    private readonly List<Control> _operationControls = new();
    private readonly Button _stop;
    private readonly Button _unlock;
    private readonly Button _restore;
    private readonly Button _exclude;
    private bool _busy, _stopRequested, _rebuilding, _closeAfter;
    private CancellationTokenSource? _scan;
    private ulong _account;
    private string? _statePath;

    public static void Run(bool demo, bool selfTest, bool acceptance = false)
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
                        try { form.SmokeChecks(); if (acceptance) form.AcceptanceChecks(); Console.WriteLine("PASS: desktop search, selection, tabs, progress filters and exclusions."); }
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
        _preferences = demo ? new() : DesktopPreferences.Load(_preferencesPath);
        _artwork = new(_http, Path.Combine(UserSettings.DirectoryPath, "artwork-cache"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SteamAchievementUnlocker/1.11.3");
        _catalog = new(_http, demo ? Path.Combine(Path.GetTempPath(), "sau-demo-unused-" + Guid.NewGuid() + ".json") : Path.Combine(UserSettings.DirectoryPath, "achievement-cache.json"));
        Text = "Steam Achievement Unlocker v1.11.3" + (demo ? " — DEMO (no Steam connection)" : "");
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(246, 247, 251);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(780, 560);
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 900);
        Size = new Size(Math.Min(1200, area.Width - 40), Math.Min(880, area.Height - 40));
        AutoScaleMode = AutoScaleMode.Dpi;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, Padding = new(24, 18, 24, 18) };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 74));
        layout.RowStyles.Add(new(SizeType.Absolute, 78));
        layout.RowStyles.Add(new(SizeType.Absolute, 45));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 145));
        layout.RowStyles.Add(new(SizeType.Absolute, 35));
        layout.RowStyles.Add(new(SizeType.Absolute, 10));
        layout.RowStyles.Add(new(SizeType.Absolute, 0));
        _navigation.TabPages.Add("Library"); _navigation.TabPages.Add("History"); _navigation.TabPages.Add("Settings");
        _navigation.TabPages[0].AutoScroll = true; _navigation.TabPages[0].AutoScrollMinSize = new(900, 620);
        layout.MinimumSize = new(900, 620);
        _navigation.TabPages[0].Controls.Add(layout); Controls.Add(_navigation);
        _navigation.SelectedIndexChanged += (_, _) =>
        {
            if (_navigation.SelectedIndex == 1)
            {
                foreach (Control old in _navigation.TabPages[1].Controls.Cast<Control>().ToArray()) old.Dispose();
                _navigation.TabPages[1].Controls.Add(HistoryPanel());
            }
            if (_navigation.SelectedIndex == 2) UpdateKeyStatus();
        };
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        header.ColumnStyles.Add(new(SizeType.Percent, 100)); header.ColumnStyles.Add(new(SizeType.Absolute, 260));
        var title = new Label { Text = "Steam Achievement Unlocker", Font = new Font("Segoe UI", 21, FontStyle.Bold), AutoSize = true, ForeColor = Color.FromArgb(31, 36, 55) };
        var heading = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        heading.Controls.Add(title);
        heading.Controls.Add(new Label { Text = demo ? "DEMO · Sample games only · Steam is never initialized" : "Your library. Your selection. Confirmed results.   /   v1.11.3", AutoSize = true, ForeColor = Color.DimGray });
        header.Controls.Add(heading, 0, 0);
        var topActions = Flow();
        topActions.Controls.Add(ActionButton("Refresh library", async () => await RefreshLibrary()));
        topActions.Controls.Add(ActionButton("Settings", Settings));
        header.Controls.Add(topActions, 1, 0); layout.Controls.Add(header, 0, 0);
        var filters = Flow(); filters.Controls.Add(_search); filters.Controls.Add(_hideComplete); filters.Controls.Add(_hideWithout);
        foreach (Control c in filters.Controls) c.Margin = new Padding(0, 6, 18, 0);
        _theme.Items.AddRange(new object[] { "Dark", "Light", "System" });
        _filter.Items.AddRange(new object[] { "All progress", "Not checked", "Incomplete", "Completed" });
        _sort.Items.AddRange(new object[] { "Name A–Z", "Name Z–A", "Progress low–high", "Progress high–low", "Most remaining" });
        filters.Controls.Add(_theme); filters.Controls.Add(_filter); filters.Controls.Add(_sort);
        filters.Controls.Add(_showCovers); filters.Controls.Add(_animations);
        layout.Controls.Add(filters, 0, 1); layout.Controls.Add(_hint, 0, 2);
        _tabs.TabPages.Add("Main library"); _tabs.TabPages.Add("Family / cached"); _tabs.TabPages.Add("Excluded games");
        _tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
        _tabs.DrawItem += (_, e) =>
        {
            bool dark = DesktopTheme.IsDark(_theme.Text);
            using var brush = new SolidBrush(dark ? Color.FromArgb(35, 39, 48) : Color.White);
            e.Graphics.FillRectangle(brush, e.Bounds);
            TextRenderer.DrawText(e.Graphics, _tabs.TabPages[e.Index].Text, Font, e.Bounds, dark ? Color.White : Color.Black, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };
        BuildGrid(); _tabs.TabPages[0].Controls.Add(_grid); layout.Controls.Add(_tabs, 0, 3);
        var actions = Flow();
        var actionSections = new ThemeTabControl { Dock = DockStyle.Fill };
        actionSections.TabPages.Add("Game actions"); actionSections.TabPages.Add("Library & history");
        var libraryActions = Flow(); actionSections.TabPages[0].Controls.Add(actions); actionSections.TabPages[1].Controls.Add(libraryActions);
        _operationControls.Add(actionSections);
        actions.Controls.Add(ActionButton("Select visible", () => { foreach (var g in _visible) _checked.Add(g.AppId); Rebuild(); return Task.CompletedTask; }));
        actions.Controls.Add(ActionButton("Clear selection", () => { _checked.Clear(); Rebuild(); return Task.CompletedTask; }));
        actions.Controls.Add(ActionButton("Read progress", async () => await RunQueue(false)));
        actions.Controls.Add(ActionButton("Scan library progress", ScanLibraryProgress));
        _unlock = ActionButton("Unlock selected", async () => await RunQueue(true));
        _unlock.BackColor = Color.FromArgb(79, 70, 210); _unlock.ForeColor = Color.White; _unlock.FlatStyle = FlatStyle.Flat;
        actions.Controls.Add(_unlock);
        actions.Controls.Add(ActionButton("Open achievements", OpenAchievements));
        _exclude = ActionButton("Exclude selected", () => { ChangeExclusions(false); return Task.CompletedTask; }); actions.Controls.Add(_exclude);
        _restore = ActionButton("Restore selected", () => { ChangeExclusions(true); return Task.CompletedTask; }); actions.Controls.Add(_restore);
        libraryActions.Controls.Add(ActionButton("Check achievement support", ScanMetadata));
        libraryActions.Controls.Add(ActionButton("Add AppID", AddGame));
        _retry = ActionButton("Retry failed", async () => await RunQueue(_retryQueue.Unlock, true));
        actions.Controls.Add(_retry);
        libraryActions.Controls.Add(ActionButton("History", History));
        libraryActions.Controls.Add(ActionButton("Library overview", Overview));
        var showLog = new CheckBox { Text = "Show activity log", AutoSize = true };
        showLog.CheckedChanged += (_, _) => { layout.RowStyles[7].Height = showLog.Checked ? 112 : 0; _log.Visible = showLog.Checked; };
        libraryActions.Controls.Add(showLog);
        _resume = ActionButton("Resume pending", async () => await RunQueue(_journal.Pending?.Unlock ?? false, resume: true));
        actions.Controls.Add(_resume);
        actions.Controls.Add(_counts); layout.Controls.Add(actionSections, 0, 4);
        actions.Controls.Add(_artStatus);
        actions.Controls.Add(_pauseArtwork);
        _pauseArtwork.Click += (_, _) =>
        {
            _artPaused = !_artPaused; _pauseArtwork.Text = _artPaused ? "Resume cover downloads" : "Pause cover downloads";
            _artStatus.Text = _artPaused ? "Covers: paused (current request may finish)" : "Covers: resuming…";
            if (!_artPaused) _ = LoadVisibleArtwork();
        };
        var activity = Flow();
        _stop = new ThemeButton { Text = "Stop after current game", AutoSize = true, Enabled = false };
        _stop.Click += (_, _) => { _stopRequested = true; _scan?.Cancel(); _status.Text = "Stopping after the current operation…"; _stop.Enabled = false; };
        activity.Controls.Add(_stop); activity.Controls.Add(_status); layout.Controls.Add(activity, 0, 5);
        layout.Controls.Add(_progress, 0, 6); layout.Controls.Add(_log, 0, 7);
        _log.Visible = false;
        BuildSettingsPage();
        _operationControls.AddRange(new Control[] { _search, _hideComplete, _hideWithout, _tabs, _theme, _sort, _filter, _showCovers });
        _search.TextChanged += (_, _) => { _searchDelay.Stop(); _searchDelay.Start(); }; _hideComplete.CheckedChanged += (_, _) => Rebuild(); _hideWithout.CheckedChanged += (_, _) => Rebuild();
        _tabs.SelectedIndexChanged += (_, _) => { _checked.Clear(); _tabs.SelectedTab!.Controls.Add(_grid); Rebuild(); };
        _theme.SelectedItem = _preferences.Theme;
        _filter.SelectedIndex = _preferences.Filter; _sort.SelectedIndex = _preferences.Sort;
        foreach (var combo in new[] { _theme, _filter, _sort })
        {
            combo.DrawMode = DrawMode.OwnerDrawFixed;
            combo.DrawItem += (_, e) =>
            {
                if (e.Index < 0) return;
                using var brush = new SolidBrush(combo.BackColor);
                e.Graphics.FillRectangle(brush, e.Bounds);
                TextRenderer.DrawText(e.Graphics, combo.Items[e.Index]?.ToString() ?? "", combo.Font, e.Bounds, combo.ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                e.DrawFocusRectangle();
            };
        }
        _search.Text = _preferences.Search; _hideComplete.Checked = _preferences.HideComplete; _hideWithout.Checked = _preferences.HideWithout;
        _showCovers.Checked = _preferences.ShowCovers; _animations.Checked = _preferences.Animations;
        _grid.Columns["Cover"].Visible = _showCovers.Checked;
        _showCovers.CheckedChanged += (_, _) => { _grid.Columns["Cover"].Visible = _showCovers.Checked; Rebuild(); };
        _animations.CheckedChanged += (_, _) => { if (!_animations.Checked) Opacity = 1; AnimateArtwork(); _grid.Invalidate(); };
        _artTimer.Tick += (_, _) =>
        {
            if (_fadeStarted > 0)
            {
                double elapsed = (Environment.TickCount64 - _fadeStarted) / 200d;
                Opacity = _animations.Checked ? Math.Min(1, 0.92 + 0.08 * elapsed) : 1;
                if (elapsed >= 1) _fadeStarted = 0;
            }
            if (_showCovers.Checked) _grid.InvalidateColumn(_grid.Columns["Cover"].Index);
            AnimateArtwork();
        };
        Size = new(Math.Min(_preferences.Width, area.Width), Math.Min(_preferences.Height, area.Height));
        if (_preferences.Maximized) WindowState = FormWindowState.Maximized;
        if (_preferences.ColumnWeights.Length == _grid.Columns.Count)
            for (int i = 0; i < _grid.Columns.Count; i++) _grid.Columns[i].FillWeight = _preferences.ColumnWeights[i];
        _theme.SelectedIndexChanged += (_, _) => ApplyTheme();
        _filter.SelectedIndexChanged += (_, _) => { if (_filter.SelectedIndex == 3) _hideComplete.Checked = false; Rebuild(); };
        _sort.SelectedIndexChanged += (_, _) => Rebuild();
        InitializeQol(); BuildContextMenu(); ApplyTheme();
        KeyPreview = true;
        KeyDown += async (_, e) =>
        {
            if (_busy || _tourOpen) return;
            if (e.KeyCode == Keys.F1) { e.SuppressKeyPress = true; StartTour(); }
            if (e.Control && e.KeyCode == Keys.F) { _search.Focus(); _search.SelectAll(); e.SuppressKeyPress = true; }
            if (e.KeyCode == Keys.F5) { e.SuppressKeyPress = true; await Guard(RefreshLibrary); }
        };
        Activated += (_, _) => { if (_theme.SelectedItem as string == "System") ApplyTheme(); };
        FormClosed += (_, _) => SavePreferences();
        Shown += async (_, _) =>
        {
            if (!_demo && _animations.Checked) { _fadeStarted = Environment.TickCount64; Opacity = 0.92; _artTimer.Start(); }
            await Guard(RefreshLibrary);
            if (!_demo && !_preferences.TourSeen && !IsDisposed) StartTour();
        };
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
        var b = new ThemeButton { Text = text, AutoSize = true, Padding = new(9, 5, 9, 5), Margin = new(0, 3, 8, 3), BackColor = Color.White };
        b.Click += async (_, _) => { FlushSearch(); await Guard(action); }; _operationControls.Add(b); return b;
    }
    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException or System.Text.Json.JsonException)
        { Log(ex is UnauthorizedAccessException ? "Access denied: check write permission for your Windows user settings folder." : ex is IOException ? "Storage unavailable: check free space and access to your settings folder." : ex is HttpRequestException ? "Connection failed: cached data is still available. Check your connection and refresh." : "Unexpected data or operation state: refresh the library before retrying."); }
    }
    private void BuildGrid()
    {
        _grid.Dock = DockStyle.Fill; _grid.AllowUserToAddRows = false; _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false; _grid.RowHeadersVisible = false; _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; _grid.BackgroundColor = Color.White;
        _grid.BorderStyle = BorderStyle.None; _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.RowTemplate.Height = 72; _grid.ColumnHeadersHeight = 38;
        _grid.EnableHeadersVisualStyles = false; _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(233, 235, 244);
        _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(249, 250, 253);
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(225, 228, 250); _grid.DefaultCellStyle.SelectionForeColor = Color.Black;
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Selected", HeaderText = "Select", FillWeight = 7, MinimumWidth = 58 });
        foreach (var (name, weight) in new[] { ("Game", 32), ("AppID", 9), ("Progress", 17), ("Source", 18), ("Last result", 25) })
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = name, FillWeight = weight, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Cover", HeaderText = "Cover", ReadOnly = true, Width = 138, MinimumWidth = 100, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, SortMode = DataGridViewColumnSortMode.NotSortable });
        _grid.Columns["Cover"].DisplayIndex = 1;
        _grid.CellPainting += PaintArtwork;
        _grid.Scroll += (_, _) => { LoadCachedVisibleArtwork(); _ = LoadVisibleArtwork(); };
        _grid.Resize += (_, _) => { LoadCachedVisibleArtwork(); _ = LoadVisibleArtwork(); };
        _grid.CurrentCellDirtyStateChanged += (_, _) => { if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _grid.VirtualMode = true;
        _grid.CellValueNeeded += (_, e) => e.Value = CellValue(e.RowIndex, e.ColumnIndex);
        _grid.CellValuePushed += (_, e) =>
        {
            if (_rebuilding || GameAt(e.RowIndex) is not { } game) return;
            if (e.ColumnIndex == 0) { if (e.Value is true) _checked.Add(game.AppId); else _checked.Remove(game.AppId); UpdateCounts(); }
            if (_grid.Columns[e.ColumnIndex].Name == "Favorite") { SetFavorite(game.AppId, e.Value is true); }
        };
        _grid.CellToolTipTextNeeded += (_, e) =>
        {
            if (GameAt(e.RowIndex) is not { } game) return;
            e.ToolTipText = _grid.Columns[e.ColumnIndex].Name == "Last result" ? FailureGuidance.Explain(_results.GetValueOrDefault(game.AppId, ""))
                : _grid.Columns[e.ColumnIndex].Name == "Progress" ? (_state.Progress.TryGetValue(game.AppId, out var progress) ? $"Last read: {progress.CheckedAt.LocalDateTime:g}. Counts expire after 24 hours." : "Use Read progress for this account.") : "";
        };
        _grid.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0 && e.ColumnIndex != 0 && !_busy) await Guard(OpenAchievements); };
    }
    private void Rebuild()
    {
        if (_tabs.TabPages.Count == 0) return;
        _searchDelay.Stop();
        bool excluded = _tabs.SelectedIndex == 2;
        IEnumerable<SteamGame> source = excluded ? _state.Excluded.Values : _tabs.SelectedIndex == 1 ? _family : _main;
        source = source.Select(g => g.FromCache ? g with { Name = _catalog.GetName(g.AppId) ?? g.Name } : g);
        if (!excluded) source = _catalog.Filter(source, _hideWithout.Checked);
        _visible = DesktopPreferences.Arrange(_state.Filter(source, _search.Text, _hideComplete.Checked && _filter.SelectedIndex != 3, excluded),
            _state, excluded ? 0 : _filter.SelectedIndex, _sort.SelectedIndex);
        if (!excluded) _visible = LibraryViews.Apply(_visible, _state, _view.SelectedIndex).ToList();
        _checked.IntersectWith(_visible.Select(g => g.AppId));
        _rebuilding = true;
        try
        {
            int first = _grid.FirstDisplayedScrollingRowIndex;
            _grid.RowTemplate.Height = _showCovers.Checked ? 72 : 36;
            _grid.RowCount = 0; _grid.RowCount = _visible.Count;
            if (first >= 0 && first < _visible.Count) _grid.FirstDisplayedScrollingRowIndex = first;
            _grid.Invalidate();
        }
        finally { _rebuilding = false; }
        _restore.Visible = excluded; _exclude.Visible = !excluded; _unlock.Enabled = !_busy && !excluded;
        _hint.Text = excluded ? "Excluded games stay out of both libraries. Select games here to restore them."
            : _tabs.SelectedIndex == 1 ? "Additional cache candidates only. Family access is unverified; stale entries may appear."
            : "Check games to choose a batch. Read progress may show you as in-game on Steam. Refresh after switching accounts.";
        if (!excluded && _tabs.SelectedIndex == 0 && _usingSnapshot) _hint.Text = "OFFLINE SNAPSHOT: uninstalled entries may be stale. Refresh to verify. " + _hint.Text;
        if (_preferences.RecoveryNotice is not null || _state.RecoveryNotice is not null || _journal.RecoveryNotice is not null) _hint.Text = "Settings recovery/protection notice: see Settings. " + _hint.Text;
        UpdateCounts(); UpdateDetails();
        LoadCachedVisibleArtwork(); UpdateDetails(); _ = LoadVisibleArtwork();
    }
    private void UpdateCounts()
    {
        _counts.Text = $"{_visible.Count} visible · {_checked.Count} selected";
        if (_resume is not null)
        {
            int pending = _journal.Resumable(_main.Concat(_family), _state).Count;
            _resume.Text = $"Resume pending ({pending})"; _resume.Enabled = !_busy && pending > 0;
        }
        if (_retry is not null)
        {
            int count = _tabs.SelectedIndex == 2 ? 0 : _retryQueue.Visible(_account, _visible, _state).Count;
            _retry.Text = $"Retry failed ({count})"; _retry.Enabled = !_busy && count > 0;
        }
    }
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
        if (_state.ReadOnly) { Log("Newer library settings are protected. Use the newer app or explicitly reset them in Settings."); return; }
        try { _state.Save(_statePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Log("Changes are visible for this session, but settings could not be saved to disk."); }
    }
    private async Task RefreshLibrary()
    {
        Busy(true);
        try
        {
            ResetSession();
            if (_demo)
            {
                _account = 1;
                _main = new() { new(10, "Sample Adventure", true), new(20, "Sample Puzzle", false), new(30, "Sample Racing", true), new(40, "Sample Explorer", true) };
                _family = new() { new(50, "Sample Family Game", false, true) };
                _state = new(); _state.Progress[10] = new(18, 25, DateTimeOffset.UtcNow); _state.Progress[20] = new(10, 10, DateTimeOffset.UtcNow);
                _sessionReady = true;
                Log("Demo ready. No Steam account is read or modified."); return;
            }
            Log("Loading library…");
            var loaded = await Task.Run(async () =>
            {
                string? path = SteamLibraryScanner.GetSteamInstallPath();
                ulong account = path is null ? 0 : SteamLibraryScanner.GetCurrentUserSteamId64(path) ?? 0;
                var installed = SteamLibraryScanner.GetInstalledGames();
                string key = UserSettings.ReadApiKey();
                using var ownedHttp = OwnedLibrary.CreateClient();
                var ownedResult = account != 0 && key.Length > 0 ? await OwnedLibrary.FetchAsync(ownedHttp, key, account) : new OwnedLibraryResult(null, key.Length == 0 ? "Not verified: no readable protected key." : "Not verified: Steam account not detected.");
                var owned = ownedResult.Games;
                string snapshotNotice = ""; bool usingSnapshot = false;
                string snapshotPath = LibrarySnapshot.PathFor(UserSettings.DirectoryPath, account);
                if (owned is not null)
                {
                    try { LibrarySnapshot.Save(snapshotPath, account, owned); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { snapshotNotice = "Offline library snapshot could not be saved."; }
                }
                else if (LibrarySnapshot.Load(snapshotPath, account) is { } snapshot)
                {
                    owned = snapshot.Games;
                    usingSnapshot = true;
                    snapshotNotice = $"Using owned-library snapshot from {snapshot.SavedAt.LocalDateTime:g}; uninstalled entries may be stale. Live ownership/access is not verified.";
                }
                var installedIds = installed.Select(g => g.AppId).ToHashSet();
                var main = owned?.Select(g => g with { IsInstalled = installedIds.Contains(g.AppId) }).ToList() ?? installed.ToList();
                var ids = main.Select(g => g.AppId).ToHashSet(); main.AddRange(installed.Where(g => ids.Add(g.AppId)));
                var family = SteamLibraryScanner.GetAdditionalCachedGames(main, SteamLibraryScanner.GetCachedLibraryGames());
                return (account, main, family, fallback: key.Length > 0 && ownedResult.Games is null, verification: ownedResult.Verification, snapshotNotice, usingSnapshot, complete: ownedResult.Games is not null);
            });
            BindAccount(loaded.account); _main = loaded.main; _family = loaded.family;
            _snapshotNotice = loaded.snapshotNotice; _usingSnapshot = loaded.usingSnapshot; _libraryLoadedAt = DateTimeOffset.UtcNow; UpdateSessionStatus();
            _keyVerification = loaded.verification; Log("API key: " + _keyVerification);
            _detailAchievements.Clear(); _detailRates.Clear(); UpdateSettingsNotice();
            if (_state.RecoveryNotice is { } recovery) Log(recovery);
            UpdateInventory(loaded.complete);
            if (_journal.RecoveryNotice is { } notice) { Log(notice); if (_journal.Pending is not null || _journal.History.Count > 0) SaveJournal(); }
            if (_journal.RecoverInterrupted()) { SaveJournal(); Log("An interrupted game has an uncertain result. Resume will read its progress first."); }
            if (_journal.Pending?.Games.Count > 0) Log("A previous batch has pending games. Use Resume pending to review them; nothing starts automatically.");
            _artAttempted.Clear(); _artCached.Clear();
            foreach (uint id in _main.Concat(_family).Select(g => g.AppId).Distinct()) if (_artwork.HasCached(id)) _artCached.Add(id);
            Log(loaded.fallback ? "Owned-library request unavailable. Showing installed games and separate cache candidates." : "Library loaded. Use Check achievement support or select games and Read progress.");
            if (_account == 0) Log("Steam account not detected. Sign in to Steam and refresh before reading or changing achievements.");
            if (_snapshotNotice.Length > 0) Log(_snapshotNotice);
            _sessionReady = true;
            if (!_keyNoticeShown)
            {
                _keyNoticeShown = true;
                bool found = UserSettings.HasSavedKey;
                Log(found ? "Saved API key found in your Windows user settings. Previous versions share these settings."
                    : "No API key saved. An optional key adds owned, uninstalled games. You can continue without one.");
                Log(UserSettings.StorageStatus);
                if (_preferences.TourSeen) BeginInvoke(new Action(async () =>
                {
                    if (IsDisposed || _busy) return;
                    if (ShowKeyNotice(found)) await Guard(Settings);
                }));
            }
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
        if (!_sessionReady) { Log("Library session is not ready. Refresh the library before continuing."); return false; }
        if (_journal.ReadOnly) { Log("Operation history is protected or belongs to another account. No Steam operation will start."); return false; }
        if (_state.ReadOnly) { Log("Newer library settings cannot be used safely. Reset them explicitly or use the newer application."); return false; }
        if (_demo) { Log("Steam operations are disabled in demo mode."); return false; }
        if (_account == 0) { Log("Sign in to Steam and refresh the library first."); return false; }
        return true;
    }
    private async Task ScanLibraryProgress()
    {
        if (_tabs.SelectedIndex == 2) { Log("Choose Main library or Family / cached before scanning."); return; }
        if (!CanUseSteam()) return;
        string scope = _tabs.SelectedIndex == 1 ? "Family / cached" : "Main library";
        var choice = MessageBox.Show(this, $"Scan progress in {scope}, including games hidden by search or filters. Excluded games are skipped.\n\nYes: scan only not-checked or expired games.\nNo: re-read every game in this library.\nCancel: do not scan.\n\nNo achievements will be changed. Steam may show you as in-game. Large libraries can take a long time; you can stop after the current game.", "Scan library progress", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Information);
        if (choice == DialogResult.Cancel) return;
        await RunQueue(false, scanLibrary: true, refreshAll: choice == DialogResult.No);
    }
    private async Task RunQueue(bool unlock, bool retry = false, bool resume = false, bool scanLibrary = false, bool refreshAll = false)
    {
        FlushSearch();
        if (scanLibrary) unlock = false;
        var games = scanLibrary ? _state.ProgressScan(_tabs.SelectedIndex == 1 ? _family : _main, refreshAll) : resume ? _journal.Resumable(_main.Concat(_family), _state) : retry ? _retryQueue.Visible(_account, _visible, _state) : LibraryState.SelectedVisible(_visible, _checked);
        if (games.Count == 0) { Log(scanLibrary ? "No unchecked or expired games to scan. Choose a full rescan to refresh current counts." : "Select at least one game using the checkboxes."); return; }
        if (!resume && _tabs.SelectedIndex == 2) { Log("Restore excluded games before processing them."); return; }
        if (!CanUseSteam()) return;
        if (!ConfirmGames(games, unlock)) return;
        uint? uncertain = resume ? _journal.Pending?.Active : null;
        bool recheck = (retry && unlock) || (resume && (_journal.Pending?.RecheckAll == true || uncertain.HasValue));
        if (!resume && _journal.Pending?.Games.Count > 0 && MessageBox.Show(this, "Replace the previous pending batch with this selection?", "Pending batch", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        _journal.Begin(games, unlock); _journal.Pending!.RecheckAll = recheck; SaveJournal();
        if (!retry) _retryQueue.Begin(_account, unlock);
        Busy(true); var results = new List<BatchResult>();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            foreach (var game in games)
            {
                if (_stopRequested) break;
                _journal.Start(game.AppId); SaveJournal(); // Durable checkpoint before making any Steam request.
                Log($"{(unlock ? "Processing" : "Reading")} {results.Count + 1}/{games.Count}: {game.Name}");
                if (unlock && recheck)
                {
                    var checkedResult = await DesktopWorker.StartAsync(game.AppId, new(_account, "read"));
                    ApplyResult(game.AppId, checkedResult);
                    if (!checkedResult.Success)
                    {
                        _journal.Complete(game, "Read before resume", false, checkedResult.Status); SaveJournal();
                        _retryQueue.Record(game.AppId, !FailureGuidance.Retryable(checkedResult.Status)); results.Add(new(game.AppId, game.Name, checkedResult.Status));
                        Log("Resume skipped the uncertain game because fresh progress could not be read.");
                        if (checkedResult.Status.StartsWith("Steam account changed", StringComparison.Ordinal)) break;
                        continue;
                    }
                    _journal.Add(new(DateTimeOffset.UtcNow, game.AppId, game.Name, "Read before resume", "Read", checkedResult.Status)); SaveJournal();
                }
                var result = await DesktopWorker.StartAsync(game.AppId, new(_account, unlock ? "unlock-all" : "read"));
                _journal.Complete(game, unlock ? "Unlock all" : "Read progress", result.Success, result.Status); SaveJournal();
                _retryQueue.Record(game.AppId, result.Success || !FailureGuidance.Retryable(result.Status));
                ApplyResult(game.AppId, result); results.Add(new(game.AppId, game.Name, result.Status));
                if (!unlock) { SaveState(); Rebuild(); }
                Log($"{game.Name}: {FailureGuidance.Explain(result.Status)}"); _progress.Value = 100 * results.Count / games.Count;
                if (result.Status.StartsWith("Steam account changed", StringComparison.Ordinal)) { _stopRequested = true; break; }
            }
            SaveState();
            if (_journal.Pending?.Games.Count > 0)
            {
                foreach (var game in _journal.Pending.Games) _journal.Add(new(DateTimeOffset.UtcNow, game.AppId, game.Name, unlock ? "Unlock all" : "Read progress", "Stopped", "Not started. Kept in the pending batch."));
                SaveJournal();
            }
            if (unlock && results.Count > 0)
            {
                try { BatchRunner.SaveReport(results); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log("The local batch report could not be saved."); }
            }
            Log($"Finished {results.Count}/{games.Count} games in {timer.Elapsed.TotalSeconds:0}s. Retry failed applies to visible failures only.");
        }
        finally { Busy(false); }
    }
    private void ApplyResult(uint id, DesktopResult result)
    {
        _results[id] = result.Success ? result.Status : FailureGuidance.Explain(result.Status);
        if (result.Success && result.Achievements is not null)
        { _state.Record(id, result.Achievements); _catalog.RecordFromClient(id, result.Achievements.Count > 0); }
        else _state.Progress.Remove(id); // Never display optimistic local flags after an unconfirmed save.
        RecordDetails(id, result);
    }
    private bool ConfirmGames(List<SteamGame> games, bool unlock)
    {
        using var dialog = new Form { Text = "Confirm achievement changes", Size = new(590, 410), MinimumSize = new(490, 350), StartPosition = FormStartPosition.CenterParent, Font = Font, MinimizeBox = false, MaximizeBox = false };
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(18), RowCount = 4, ColumnCount = 1 };
        panel.RowStyles.Add(new(SizeType.Absolute, 58)); panel.RowStyles.Add(new(SizeType.Percent, 100)); panel.RowStyles.Add(new(SizeType.Absolute, 38)); panel.RowStyles.Add(new(SizeType.Absolute, 45));
        panel.Controls.Add(new Label { Text = unlock ? "Review the batch. Uncheck games to remove them.\nType UNLOCK ALL to confirm changes to your Steam profile." : "Review progress reads. Uncheck games to remove them.\nSteam may show you as in-game while reading.", AutoSize = true });
        var list = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true }; foreach (var g in games)
            list.Items.Add($"{g.Name} ({g.AppId}) — " + (_state.Progress.TryGetValue(g.AppId, out var p) && p.IsFresh ? $"{p.Total - p.Unlocked} locked (cached)" : "Locked count unknown"), true);
        panel.Controls.Add(list);
        var input = new ThemeTextBox { Dock = DockStyle.Fill, AccessibleName = "Confirmation text", Visible = unlock }; panel.Controls.Add(input);
        var buttons = Flow(); var confirm = new ThemeButton { Text = unlock ? "Unlock" : "Read progress", Enabled = !unlock, DialogResult = DialogResult.OK, AutoSize = true }; var cancel = new ThemeButton { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        void UpdateConfirm() => confirm.Enabled = (!unlock || input.Text == "UNLOCK ALL") && list.CheckedItems.Count > 0;
        input.TextChanged += (_, _) => UpdateConfirm(); list.ItemCheck += (_, _) => dialog.BeginInvoke(new Action(UpdateConfirm));
        buttons.Controls.Add(confirm); buttons.Controls.Add(cancel); panel.Controls.Add(buttons); dialog.Controls.Add(panel); dialog.CancelButton = cancel;
        DesktopTheme.Apply(dialog, _theme.Text);
        if (dialog.ShowDialog(this) != DialogResult.OK) return false;
        var chosen = list.CheckedIndices.Cast<int>().Select(i => games[i]).ToList(); games.Clear(); games.AddRange(chosen);
        return games.Count > 0;
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
        FlushSearch();
        if (GameAt(_grid.CurrentCell?.RowIndex ?? -1) is not { } game) { Log("Choose a game row first."); return; }
        if (_tabs.SelectedIndex == 2) { Log("Restore the game before opening achievements."); return; }
        if (!CanUseSteam()) return;
        Busy(true);
        try
        {
            Log("Reading achievements for " + game.Name + "…");
            var result = await DesktopWorker.StartAsync(game.AppId, new(_account, "read")); ApplyResult(game.AppId, result); SaveState();
            _journal.Add(new(DateTimeOffset.UtcNow, game.AppId, game.Name, "Open achievements", result.Success ? "Read" : "Failed", result.Status)); SaveJournal();
            if (_stopRequested || _closeAfter) return;
            if (!result.Success || result.Achievements is null) { Log(result.Status); return; }
            using var dialog = new AchievementForm(game, result.Achievements, async names =>
            {
                var started = new OperationEntry(DateTimeOffset.UtcNow, game.AppId, game.Name, "Unlock selected", "Uncertain", "Request started; final confirmation pending. Re-read progress after an interruption.");
                _journal.Add(started); SaveJournal();
                var saved = await DesktopWorker.StartAsync(game.AppId, new(_account, "unlock", names));
                // Single-game changes do not consume an unrelated pending batch.
                var entryJournal = new OperationJournal(); entryJournal.Complete(game, "Unlock selected", saved.Success, saved.Status);
                _journal.History.Remove(started); _journal.Add(entryJournal.History.Single()); SaveJournal();
                ApplyResult(game.AppId, saved); SaveState(); Log(saved.Status); return saved;
            }, _http, _animations.Checked);
            DesktopTheme.Apply(dialog, _theme.Text);
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
    private void SaveJournal()
    {
        if (!_demo && _journalPath is not null)
        {
            if (_account == 0 || _journal.Account != _account) throw new InvalidOperationException("Operation history account mismatch.");
            _journal.Save(_journalPath);
        }
    }
    private Task Overview()
    {
        var summary = LibrarySummary.Calculate(_main.Concat(_family), _state);
        MessageBox.Show(this, $"Library: {summary.Total} games (excludes hidden/excluded games only by exclusion, not search filters)\n\nCompleted: {summary.Completed}\nIncomplete: {summary.Incomplete}\nNot checked / expired: {summary.Unknown}\nNo achievements: {summary.NoAchievements}\n\nKnown achievements: {summary.Unlocked}/{summary.KnownAchievements} unlocked\nExcluded games: {_state.Excluded.Count}\n\nOnly fresh account-specific reads are counted. Unknown data is not zero.\nFamily/cache candidates may not be accessible through Steam.", "Library overview", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return Task.CompletedTask;
    }
    private Task History()
    {
        _navigation.SelectedIndex = 1; return Task.CompletedTask;
    }
    private Control HistoryPanel()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(18), RowCount = 3, ColumnCount = 1 };
        layout.RowStyles.Add(new(SizeType.Absolute, 42)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 42));
        var search = new ThemeTextBox { Dock = DockStyle.Fill, PlaceholderText = "Search games, operations, outcomes or details" }; layout.Controls.Add(search);
        var grid = new ArtworkGrid { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, EnableHeadersVisualStyles = false };
        foreach (string name in new[] { "Time", "Game", "Operation", "Outcome", "Details" }) grid.Columns.Add(name, name);
        grid.Columns["Details"].FillWeight = 220; layout.Controls.Add(grid);
        void Fill()
        {
            grid.Rows.Clear(); string query = search.Text.Trim();
            foreach (var item in _journal.History.AsEnumerable().Reverse().Where(e => query.Length == 0 || $"{e.Game} {e.Operation} {e.Outcome} {e.Detail}".Contains(query, StringComparison.OrdinalIgnoreCase)))
                grid.Rows.Add(item.Time.LocalDateTime.ToString("g"), item.Game, item.Operation, item.Outcome, item.Detail);
        }
        search.TextChanged += (_, _) => Fill(); Fill();
        layout.Controls.Add(new Label { Text = "Last 2,000 entries. Uncertain means Steam's final save was not confirmed. History stays on this computer.", AutoSize = true });
        DesktopTheme.Apply(layout, _theme.Text); return layout;
    }
    private bool ShowKeyNotice(bool found)
    {
        using var dialog = new Form { Text = "API key status", Size = new(620, 230), StartPosition = FormStartPosition.CenterParent, Font = Font, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false };
        var label = new Label { AutoSize = false, Location = new(18, 18), Size = new(570, 100), Text = found
            ? "Saved API key found.\n" + UserSettings.StorageStatus + "\nA saved file does not verify whether Steam accepts the key."
            : "No API key saved.\nAdd an optional key to include owned, uninstalled games.\nYou can use installed games without a key." };
        var add = new ThemeButton { Text = found ? "Open settings" : "Add API key", Location = new(18, 130), AutoSize = true, DialogResult = DialogResult.OK };
        var close = new ThemeButton { Text = found ? "Continue" : "Continue without key", Location = new(175, 130), AutoSize = true, DialogResult = DialogResult.Cancel };
        dialog.Controls.AddRange(new Control[] { label, add, close }); dialog.CancelButton = close; dialog.AcceptButton = close;
        DesktopTheme.Apply(dialog, _theme.Text);
        return dialog.ShowDialog(this) == DialogResult.OK;
    }
    private async Task Settings()
    {
        _navigation.SelectedIndex = 2; UpdateKeyStatus(); await Task.CompletedTask;
    }
    private void UpdateKeyStatus() => _keyStatusLabel.Text = _demo ? "Demo: no credentials are read or written." : UserSettings.StorageStatus + "\nSteam check: " + _keyVerification;
    private void BuildSettingsPage()
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new(24) };
        panel.Controls.Add(new Label { Text = "Steam API key", AutoSize = true, Font = new(Font, FontStyle.Bold) }); panel.Controls.Add(_keyStatusLabel);
        panel.Controls.Add(new Label { Text = "Optional: includes owned, uninstalled games. The existing key is never shown.\nNew keys are protected for your Windows account; no plaintext fallback.", AutoSize = true, Margin = new(0, 16, 0, 12) });
        panel.Controls.Add(_keyInput); _operationControls.Add(_keyInput);
        panel.Controls.Add(ActionButton("Save protected key", async () =>
        {
            if (_demo) return;
            string key = _keyInput.Text.Trim(); _keyInput.Clear();
            if (!UserSettings.IsValidApiKey(key)) { _keyStatusLabel.Text = "Enter a 32-character hexadecimal key. The saved key was not changed."; return; }
            try { UserSettings.SaveApiKey(key); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            { _keyStatusLabel.Text = "Protection, saving or legacy cleanup failed. No plaintext fallback was created; review the saved-key status before retrying."; return; }
            _keyVerification = "Not verified: new key saved."; UpdateKeyStatus(); await RefreshLibrary(); UpdateKeyStatus();
        }));
        panel.Controls.Add(ActionButton("Verify with Steam", async () => { if (_demo) return; await RefreshLibrary(); UpdateKeyStatus(); }));
        panel.Controls.Add(ActionButton("Remove saved key", async () =>
        {
            if (_demo || MessageBox.Show(this, "Remove this application's saved API key?", "Remove key", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            UserSettings.SaveApiKey(""); _keyInput.Clear(); _keyVerification = "Not verified: no saved key."; await RefreshLibrary(); UpdateKeyStatus();
        }));
        _navigation.TabPages[2].Controls.Add(panel);
    }
    private string? TextPrompt(string title, string message, bool secret)
    {
        using var form = new Form { Text = title, Size = new(640, 225), StartPosition = FormStartPosition.CenterParent, Font = Font, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false };
        var label = new Label { Text = message, AutoSize = true, Location = new(18, 16) };
        var input = new ThemeTextBox { Location = new(18, 75), Width = 585, UseSystemPasswordChar = secret, AccessibleName = secret ? "API key" : "AppID", MaxLength = secret ? 32 : 10 };
        var save = new ThemeButton { Text = "Save", Location = new(18, 120), DialogResult = DialogResult.OK }; var cancel = new ThemeButton { Text = "Cancel", Location = new(105, 120), DialogResult = DialogResult.Cancel };
        form.Controls.AddRange(new Control[] { label, input, save, cancel }); form.AcceptButton = save; form.CancelButton = cancel;
        DesktopTheme.Apply(form, _theme.Text);
        return form.ShowDialog(this) == DialogResult.OK ? input.Text : null;
    }
    private void ApplyTheme()
    {
        DesktopTheme.Apply(this, _theme.Text);
        _unlock.BackColor = Color.FromArgb(79, 70, 210); _unlock.ForeColor = Color.White;
        _menu.BackColor = _grid.DefaultCellStyle.BackColor; _menu.ForeColor = _grid.DefaultCellStyle.ForeColor;
    }
    private void SavePreferences()
    {
        if (_demo || _skipPreferenceSave || _preferences.ReadOnly) return;
        _preferences.ShowDetails = _showDetails.Checked;
        _preferences.Theme = _theme.Text; _preferences.Search = _search.Text;
        _preferences.Filter = _filter.SelectedIndex; _preferences.Sort = _sort.SelectedIndex;
        _preferences.HideComplete = _hideComplete.Checked; _preferences.HideWithout = _hideWithout.Checked;
        _preferences.ShowCovers = _showCovers.Checked; _preferences.Animations = _animations.Checked;
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _preferences.Width = bounds.Width; _preferences.Height = bounds.Height;
        _preferences.View = _view.SelectedIndex; _preferences.Left = bounds.Left; _preferences.Top = bounds.Top;
        _preferences.Maximized = WindowState == FormWindowState.Maximized;
        _preferences.ColumnWeights = _grid.Columns.Cast<DataGridViewColumn>().Select(c => c.FillWeight).ToArray();
        try { _preferences.Save(_preferencesPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { MessageBox.Show(this, "Display preferences could not be saved. Check folder access or use a newer application if the settings version changed.", "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
    private void BuildContextMenu()
    {
        _grid.ContextMenuStrip = _menu;
        var open = _menu.Items.Add("Open achievements");
        open.Click += async (_, _) => { if (!_busy) await Guard(OpenAchievements); };
        var change = _menu.Items.Add("Exclude game");
        change.Click += (_, _) =>
        {
            if (_busy || GameAt(_grid.CurrentCell?.RowIndex ?? -1) is not { } g) return;
            _checked.Clear(); _checked.Add(g.AppId); ChangeExclusions(_tabs.SelectedIndex == 2);
        };
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Open Steam store page").Click += (_, _) =>
        {
            if (GameAt(_grid.CurrentCell?.RowIndex ?? -1) is not { } g) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"https://store.steampowered.com/app/{g.AppId}/") { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Log("The browser could not be opened."); }
        };
        _menu.Items.Add("Copy App ID").Click += (_, _) =>
        {
            if (GameAt(_grid.CurrentCell?.RowIndex ?? -1) is not { } g) return;
            try { Clipboard.SetText(g.AppId.ToString()); Log("App ID copied."); }
            catch (System.Runtime.InteropServices.ExternalException) { Log("Clipboard is busy. Try again."); }
        };
        _grid.CellMouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0) return;
            _grid.CurrentCell = _grid.Rows[e.RowIndex].Cells[1];
        };
        _menu.Opening += (_, e) =>
        {
            if (_busy || _grid.CurrentRow is null) { e.Cancel = true; return; }
            open.Enabled = _tabs.SelectedIndex != 2;
            change.Text = _tabs.SelectedIndex == 2 ? "Restore game" : "Exclude game";
        };
    }
    private void SmokeChecks()
    {
        void Require(bool value) { if (!value) throw new InvalidOperationException("Desktop behavior mismatch."); }
        using (var browser = new AchievementForm(new(10, "Sample", true), new() { new("FIRST", "First", false, "A description"), new("SECOND", "Second", true) }, _ => throw new InvalidOperationException("Smoke test must never unlock"))) browser.SmokeChecks();
        Require(_visible.Count == 4);
        _theme.SelectedItem = "Light"; Require(_grid.DefaultCellStyle.BackColor == Color.White);
        _theme.SelectedItem = "Dark"; Require(_grid.DefaultCellStyle.BackColor == Color.Black && BackColor == Color.Black);
        foreach (DataGridViewColumn column in _grid.Columns) { _grid.CurrentCell = _grid.Rows[0].Cells[column.Index]; Require(_grid.ColumnHeadersDefaultCellStyle.SelectionBackColor == _grid.ColumnHeadersDefaultCellStyle.BackColor); }
        Require(_navigation.Dark && _tabs.Dark && _search.Dark && _theme.Dark);
        _sort.SelectedIndex = 1; Require(_visible[0].Name == "Sample Racing"); _sort.SelectedIndex = 0;
        _filter.SelectedIndex = 3; Require(_visible.Count == 1 && _visible[0].AppId == 20);
        _filter.SelectedIndex = 1; Require(_visible.Count == 2); _filter.SelectedIndex = 0;
        _retryQueue.Begin(_account, false); _retryQueue.Record(10, false); UpdateCounts(); Require(_retry.Enabled);
        _search.Text = "Puzzle"; Rebuild(); Require(!_retry.Enabled); _search.Clear(); Rebuild();
        _checked.Add(10); _search.Text = "Puzzle"; Rebuild(); Require(_visible.Count == 1 && _checked.Count == 0);
        _search.Clear(); Rebuild(); _hideComplete.Checked = true; Require(_visible.Count == 3);
        _hideComplete.Checked = false; _checked.Add(10); ChangeExclusions(false); Require(_visible.Count == 3);
        _tabs.SelectedIndex = 2; Require(_visible.Count == 1); _checked.Add(10); ChangeExclusions(true); Require(_visible.Count == 0);
        _tabs.SelectedIndex = 1; Require(_visible.Count == 1 && _visible[0].AppId == 50);
        _tabs.SelectedIndex = 0; Require(_visible.Count == 4 && !_visible.Any(g => g.AppId == 50));
        _showCovers.Checked = false; Require(!_grid.Columns["Cover"].Visible && _grid.Rows[0].Height == 36);
        _showCovers.Checked = true; Require(_grid.Columns["Cover"].Visible && _grid.Rows[0].Height == 72);
        _animations.Checked = false; Require(!_artTimer.Enabled);
        _pauseArtwork.PerformClick(); Require(_artPaused); _pauseArtwork.PerformClick(); Require(!_artPaused);
        _navigation.SelectedIndex = 1; Require(_navigation.TabPages[1].Controls.Count == 1);
        _navigation.SelectedIndex = 2; Require(_keyInput.UseSystemPasswordChar && _keyInput.Text.Length == 0);
        _navigation.SelectedIndex = 0;
        EnhancementSmokeChecks();
        foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f })
        {
            using var viewport = new DesktopForm(true); viewport.Show(); viewport.Scale(new SizeF(scale, scale));
            viewport.MinimumSize = new(600, 400); viewport.Size = new(900, 650); viewport.PerformLayout();
            Require(viewport._navigation.TabPages.Count == 3 && viewport._navigation.TabPages[0].AutoScroll && viewport._grid.Height > 0);
            viewport._navigation.SelectedIndex = 2; viewport.PerformLayout();
            Require(viewport._navigation.TabPages[2].Controls[0] is FlowLayoutPanel { AutoScroll: true } && viewport._keyInput.UseSystemPasswordChar);
            using (var scaledTour = new GuidedTour(viewport, viewport.TourSteps(), viewport._theme.Text))
            {
                scaledTour.Show(viewport); scaledTour.MoveTo(5); scaledTour.PerformLayout();
                Require(scaledTour.Controls[0].Bounds.Height > 0 && viewport._navigation.SelectedIndex == 0);
                scaledTour.MoveTo(10); Require(viewport._navigation.SelectedIndex == 2); scaledTour.Close();
            }
            if (scale == 1.5f && Environment.GetEnvironmentVariable("SAU_SETTINGS_SCREENSHOT") is { Length: > 0 } settingsImage)
            {
                using var image = new Bitmap(viewport.Width, viewport.Height); viewport.DrawToBitmap(image, new Rectangle(0, 0, viewport.Width, viewport.Height)); image.Save(settingsImage, System.Drawing.Imaging.ImageFormat.Png);
            }
            viewport.Close();
        }
        QolSmokeChecks();
        if (Environment.GetEnvironmentVariable("SAU_DEMO_ARTWORK") is { Length: > 0 } fixture)
        {
            using var sample = Image.FromFile(fixture);
            _artImages[10] = new Bitmap(sample); _artStarted[10] = Environment.TickCount64;
        }
        if (Environment.GetEnvironmentVariable("SAU_DEMO_SCREENSHOT") is { Length: > 0 } screenshot)
        {
            using var bitmap = new Bitmap(Width, Height);
            DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));
            bitmap.Save(screenshot, System.Drawing.Imaging.ImageFormat.Png);
        }
    }
    private void AnimateArtwork()
    {
        bool active = _animations.Checked && (_fadeStarted > 0 || (_showCovers.Checked && DisplayedGames()
            .Any(g => _artLoading && !_artImages.ContainsKey(g.AppId) && !_artMissing.ContainsKey(g.AppId) ||
                _artStarted.TryGetValue(g.AppId, out long time) && Environment.TickCount64 - time < 250)));
        _artTimer.Enabled = active;
    }
    private void LoadCachedVisibleArtwork()
    {
        if (_demo || !_showCovers.Checked || _rebuilding || !IsHandleCreated || IsDisposed) return;
        var visible = DisplayedGames().Select(g => g.AppId).ToHashSet();
        foreach (uint id in visible.Where(id => !_artImages.ContainsKey(id)))
        {
            var image = _artwork.ReadCached(id);
            if (image is null) continue;
            _artImages[id] = image; _artStarted[id] = Environment.TickCount64 - 250; _artCached.Add(id);
        }
        foreach (uint old in _artImages.Keys.Where(id => !visible.Contains(id)).Take(Math.Max(0, _artImages.Count - 96)).ToArray())
        { _artImages[old].Dispose(); _artImages.Remove(old); _artStarted.Remove(old); }
        _grid.InvalidateColumn(_grid.Columns["Cover"].Index);
    }
    private async Task LoadVisibleArtwork()
    {
        if (_demo || _artLoading || _artPaused || !_showCovers.Checked || !IsHandleCreated || IsDisposed) return;
        _artLoading = true; AnimateArtwork();
        try
        {
            while (!_artLifetime.IsCancellationRequested && _showCovers.Checked && !_artPaused)
            {
                var visible = DisplayedGames().Select(g => g.AppId).ToArray();
                var library = _main.Concat(_family).Where(g => !_state.Excluded.ContainsKey(g.AppId)).Select(g => g.AppId).Distinct().ToArray();
                var blocked = _artMissing.Where(p => Environment.TickCount64 < p.Value).Select(p => p.Key).ToHashSet();
                blocked.UnionWith(library.Where(_artwork.MissingCached));
                var backgroundAttempted = _artAttempted.Concat(blocked).ToHashSet();
                // Memory loading for visible rows remains a priority, even if previously downloaded off-screen.
                uint id = SteamArtwork.Queue(library, visible, _artImages.Keys.ToHashSet(), blocked, _artCached.Contains)
                    .FirstOrDefault(candidate => visible.Contains(candidate) || !backgroundAttempted.Contains(candidate));
                if (id == 0)
                {
                    _artStatus.Text = $"Covers: {_artCached.Count(k => library.Contains(k))}/{library.Length} cached · {_artwork.MissingCount(library)} unavailable";
                    break;
                }
                _artStatus.Text = $"Covers: {_artCached.Count(k => library.Contains(k))}/{library.Length} cached · {_artwork.MissingCount(library)} unavailable · downloading…";
                    var image = await _artwork.GetAsync(id, _artLifetime.Token);
                    if (_artLifetime.IsCancellationRequested || IsDisposed) { image?.Dispose(); return; }
                    if (image is null && _artwork.CoolingDown)
                    {
                        _artStatus.Text = "Covers paused: Steam limit or connection issue. Refresh library to retry.";
                        break;
                    }
                    _artAttempted.Add(id);
                    if (image is null) _artMissing[id] = Environment.TickCount64 + 3600000;
                    else
                    {
                        if (_artwork.HasCached(id)) _artCached.Add(id);
                        if (visible.Contains(id)) { _artImages[id] = image; _artStarted[id] = Environment.TickCount64; }
                        else image.Dispose();
                        foreach (uint old in _artImages.Keys.Where(k => !visible.Contains(k)).Take(Math.Max(0, _artImages.Count - 96)).ToArray())
                        { _artImages[old].Dispose(); _artImages.Remove(old); _artStarted.Remove(old); }
                    }
                    _grid.InvalidateColumn(_grid.Columns["Cover"].Index); UpdateDetails();
                    // Yield between background downloads so scrolling and actions stay responsive.
                    await Task.Delay(200, _artLifetime.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
        finally { _artLoading = false; if (!IsDisposed) { AnimateArtwork(); if (!_showCovers.Checked || _artPaused) _artStatus.Text = "Covers: paused"; } }
    }
    private void PaintArtwork(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || _grid.Columns[e.ColumnIndex].Name != "Cover" || e.Graphics is null) return;
        e.PaintBackground(e.ClipBounds, true);
        if (GameAt(e.RowIndex) is not { } game) return;
        var rect = new Rectangle(e.CellBounds.X + 5, e.CellBounds.Y + 6, Math.Max(1, e.CellBounds.Width - 10), Math.Max(1, e.CellBounds.Height - 12));
        if (_artImages.TryGetValue(game.AppId, out var cover))
        {
            float alpha = _animations.Checked ? Math.Clamp((Environment.TickCount64 - _artStarted.GetValueOrDefault(game.AppId)) / 250f, 0, 1) : 1;
            using var attributes = new System.Drawing.Imaging.ImageAttributes();
            attributes.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = alpha });
            e.Graphics.DrawImage(cover, rect, 0, 0, cover.Width, cover.Height, GraphicsUnit.Pixel, attributes);
        }
        else
        {
            bool dark = DesktopTheme.IsDark(_theme.Text);
            using var brush = new SolidBrush(dark ? Color.FromArgb(48, 53, 66) : Color.FromArgb(222, 225, 235));
            e.Graphics.FillRectangle(brush, rect);
            string label = _demo ? "Demo artwork" : _artMissing.ContainsKey(game.AppId) || _artwork.MissingCached(game.AppId) ? "No cover" : _artPaused ? "Paused" : "Loading" + (_animations.Checked ? new string('.', (int)(Environment.TickCount64 / 350 % 4)) : "…");
            TextRenderer.DrawText(e.Graphics, label, Font, rect, dark ? Color.LightGray : Color.DimGray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        e.Handled = true;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _detailCover.Image?.Dispose(); _detailCover.Image = null;
            _artLifetime.Cancel(); _artTimer.Dispose(); _searchDelay.Dispose(); _scan?.Cancel(); _menu.Dispose();
            foreach (var image in _artImages.Values) image.Dispose(); _artImages.Clear(); _http.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class ArtworkGrid : DataGridView
{
    public ArtworkGrid() { DoubleBuffered = true; }
}
