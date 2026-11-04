using System.Drawing;
using System.Windows.Forms;

namespace SteamAchievementUnlocker;

public sealed partial class DesktopForm
{
    private readonly CheckBox _showDetails = new() { Text = "Show game details", AutoSize = true, Checked = true };
    private readonly PictureBox _detailCover = new() { Size = new(240, 112), SizeMode = PictureBoxSizeMode.Zoom, TabStop = false };
    private readonly Label _detailText = new() { AutoSize = true, MaximumSize = new(250, 0) };
    private readonly Label _detailRarity = new() { AutoSize = true, MaximumSize = new(250, 0) };
    private readonly Label _changesLabel = new() { AutoSize = true, MaximumSize = new(640, 0) };
    private readonly Label _settingsNotice = new() { AutoSize = true, MaximumSize = new(650, 0) };
    private readonly Dictionary<uint, List<AchievementInfo>> _detailAchievements = new();
    private readonly Dictionary<uint, Dictionary<string, double>> _detailRates = new();
    private LibraryInventory _inventory = new();
    private bool _inventoryComplete;
    private SplitContainer? _detailsSplit;
    private void InitializeEnhancements()
    {
        _grid.AccessibleName = "Steam games"; _search.AccessibleName = "Search games or AppID";
        foreach (var (column, width) in new[] { ("Game", 140), ("AppID", 65), ("Progress", 105), ("Source", 110), ("Last result", 130) }) _grid.Columns[column].MinimumWidth = width;
        _grid.StandardTab = true; _grid.TabIndex = 2; _search.TabIndex = 0; _tabs.TabIndex = 1;
        var parent = (TableLayoutPanel)_tabs.Parent!; parent.Controls.Remove(_tabs);
        _detailsSplit = new SplitContainer { Dock = DockStyle.Fill, Size = new(1000, 350), FixedPanel = FixedPanel.Panel2, SplitterDistance = 700, Panel1MinSize = 250, Panel2MinSize = 260 };
        _detailsSplit.Panel1.Controls.Add(_tabs);
        var detail = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false, FlowDirection = FlowDirection.TopDown, Padding = new(10), AccessibleName = "Selected game details" };
        detail.Controls.Add(new Label { Text = "Game details", AutoSize = true, Font = new(Font, FontStyle.Bold) });
        detail.Controls.Add(_detailCover); detail.Controls.Add(_detailText); detail.Controls.Add(_detailRarity);
        detail.Controls.Add(ActionButton("Load public rarity", LoadDetailRarity));
        _detailsSplit.Panel2.Controls.Add(detail); parent.Controls.Add(_detailsSplit, 0, 3);
        var tools = Descendants(this).OfType<TabPage>().First(p => p.Text == "Library & history").Controls[0];
        tools.Controls.Add(_showDetails); tools.Controls.Add(ActionButton("Library changes", ShowLibraryChanges)); tools.Controls.Add(_changesLabel);
        _showDetails.Checked = _preferences.ShowDetails; _detailsSplit.Panel2Collapsed = !_showDetails.Checked;
        _showDetails.CheckedChanged += (_, _) => { _detailsSplit.Panel2Collapsed = !_showDetails.Checked; UpdateDetails(); };
        _grid.CurrentCellChanged += (_, _) => { if (!_rebuilding) UpdateDetails(); };
        _grid.KeyDown += (_, e) => HandleGridKey(e);
        _grid.CellPainting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || !_grid.Focused || _grid.CurrentCell?.RowIndex != e.RowIndex || _grid.CurrentCell.ColumnIndex != e.ColumnIndex || e.Graphics is null) return;
            if (!e.Handled) e.Paint(e.ClipBounds, DataGridViewPaintParts.All);
            var bounds = Rectangle.Inflate(e.CellBounds, -2, -2); using var pen = new Pen(Color.DeepSkyBlue, 2); e.Graphics.DrawRectangle(pen, bounds); e.Handled = true;
        };
        var settings = (FlowLayoutPanel)_navigation.TabPages[2].Controls[0];
        settings.Controls.Add(new Label { Text = "Recovery & diagnostics", AutoSize = true, Font = new(Font, FontStyle.Bold), Margin = new(0, 20, 0, 8) });
        settings.Controls.Add(_settingsNotice);
        settings.Controls.Add(ActionButton("Reset display settings safely", ResetDisplaySettings));
        settings.Controls.Add(ActionButton("Reset this account's library preferences", ResetLibraryPreferences));
        settings.Controls.Add(ActionButton("Preview diagnostic report", PreviewDiagnostics));
        settings.Controls.Add(new Label { Text = "Keyboard: Ctrl+F search · F5 refresh · F1 tour · Space selects a game.\nTab leaves the grid; arrow keys move inside it.\nTurn off Animations to reduce motion, including rare-achievement glows.", AutoSize = true, MaximumSize = new(650, 0) });
        _animations.AccessibleName = "Animations (disable to reduce motion)";
        foreach (var button in Descendants(this).OfType<Button>()) button.AccessibleName ??= button.Text;
        UpdateSettingsNotice(); UpdateDetails();
    }
    private void HandleGridKey(KeyEventArgs e)
    {
        if (_busy || e.KeyCode != Keys.Space || GameAt(_grid.CurrentCell?.RowIndex ?? -1) is not { } game) return;
        e.Handled = true; e.SuppressKeyPress = true;
        if (_grid.CurrentCell!.OwningColumn.Name == "Favorite") SetFavorite(game.AppId, !_state.Favorites.Contains(game.AppId));
        else { if (!_checked.Add(game.AppId)) _checked.Remove(game.AppId); _grid.InvalidateRow(_grid.CurrentCell.RowIndex); UpdateCounts(); }
    }
    private void UpdateSettingsNotice() => _settingsNotice.Text = string.Join("\n", new[] { _preferences.RecoveryNotice, _state.RecoveryNotice, _journal.RecoveryNotice }.Where(s => !string.IsNullOrEmpty(s))) is { Length: > 0 } notice ? notice : "Display, library and operation settings are versioned and keep a last-readable backup.";
    private void UpdateDetails()
    {
        if (_rebuilding || _detailsSplit is null) return;
        _detailCover.Image?.Dispose(); _detailCover.Image = null; _detailCover.Visible = false;
        if (GameAt(_grid.CurrentCell?.RowIndex ?? -1) is not { } game)
        { _detailCover.Image = null; _detailText.Text = "Select a game row to view details."; _detailRarity.Text = ""; return; }
        if (_showCovers.Checked && _artImages.TryGetValue(game.AppId, out var cover)) { _detailCover.Image = new Bitmap(cover); _detailCover.Visible = true; }
        var progress = _state.Progress.GetValueOrDefault(game.AppId);
        var last = _results.GetValueOrDefault(game.AppId) ?? _journal.History.LastOrDefault(e => e.AppId == game.AppId)?.Detail ?? "No operation recorded.";
        _detailText.Text = $"{game.Name}\nAppID: {game.AppId}\n{(progress?.Label ?? "Not checked")}\n{(game.FromCache ? "Cache candidate: access unverified" : game.IsInstalled ? "Installed" : _usingSnapshot ? "Stale owned-library snapshot" : "Owned / manual")}\n\nLast result: {last}";
        _detailRarity.Text = "Rarity: load public rates below. Read progress to obtain achievement names.";
        if (_detailAchievements.TryGetValue(game.AppId, out var achievements) && _detailRates.TryGetValue(game.AppId, out var rates))
        {
            var rare = achievements.Where(a => rates.ContainsKey(a.ApiName)).OrderBy(a => rates[a.ApiName]).Take(3).Select(a => $"{a.DisplayName}: {rates[a.ApiName]:0.##}% · {(a.Unlocked ? "Unlocked" : "Locked")}");
            _detailRarity.Text = "Rarest achievements (Steam global):\n" + (rare.Any() ? string.Join("\n", rare) : "Unavailable");
        }
    }
    private async Task LoadDetailRarity()
    {
        if (GameAt(_grid.CurrentCell?.RowIndex ?? -1) is not { } game) return;
        if (_demo) { _detailRarity.Text = "Demo: public rarity requests are disabled."; return; }
        ulong account = _account;
        var rates = await GlobalAchievements.LoadAsync(_http, game.AppId, Path.Combine(UserSettings.DirectoryPath, "global-achievements"), _artLifetime.Token);
        if (IsDisposed || _account != account) return;
        if (_detailRates.Count >= 100) _detailRates.Remove(_detailRates.Keys.First()); _detailRates[game.AppId] = rates;
        UpdateDetails();
        if (!_detailAchievements.ContainsKey(game.AppId) && GameAt(_grid.CurrentCell?.RowIndex ?? -1)?.AppId == game.AppId) _detailRarity.Text = $"{rates.Count} public achievement rates available. Use Read progress to obtain this game's achievement names.";
    }
    private void RecordDetails(uint id, DesktopResult result)
    {
        if (result.Success && result.Achievements is not null)
        {
            if (_detailAchievements.Count >= 100) _detailAchievements.Remove(_detailAchievements.Keys.First());
            _detailAchievements[id] = result.Achievements.Select(a => new AchievementInfo(a.ApiName, a.DisplayName, a.Unlocked)).ToList();
        }
        else _detailAchievements.Remove(id);
        UpdateDetails();
    }
    private void UpdateInventory(bool complete)
    {
        _inventoryComplete = complete;
        if (_demo) { _changesLabel.Text = "Demo: no inventory is saved."; return; }
        if (_account == 0) { _changesLabel.Text = "Changes unavailable: account not detected."; return; }
        string path = LibrarySnapshot.PathFor(UserSettings.DirectoryPath, _account).Replace("library-snapshot-", "library-inventory-");
        var loaded = SettingsFile.Load<LibraryInventory>(path); _inventory = loaded.Value;
        _inventory.Current ??= new(); _inventory.Missing ??= new(); _inventory.Added ??= new();
        _inventory.Update(_main, complete);
        _changesLabel.Text = $"Library changes: {_inventory.Added.Count} added · {_inventory.Missing.Count} not found" + (complete ? "" : " · partial scan");
        if (loaded.ReadOnly) { Log(loaded.Notice!); return; }
        try { SettingsFile.Save(path, _inventory, false, loaded.PreserveOriginal); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { _changesLabel.Text += " · not saved"; Log("Library changes could not be saved. Current account and operation history still load independently."); }
    }
    private Task ShowLibraryChanges()
    {
        using var dialog = new Form { Text = "Library changes", Size = new(640, 480), StartPosition = FormStartPosition.CenterParent, Font = Font };
        var text = new ThemeTextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Text =
            (_inventoryComplete ? "Compared against the last recorded owned library." : "Partial/offline scan: missing ownership is not inferred.") + "\r\nNot found does not mean unowned or deleted. Missing records stay available here.\r\n\r\nADDED\r\n" +
            string.Join("\r\n", _inventory.Added.Select(g => $"{g.Name} ({g.AppId})")) + "\r\n\r\nNOT FOUND IN LATEST COMPLETE SCAN\r\n" + string.Join("\r\n", _inventory.Missing.Select(g => $"{g.Name} ({g.AppId})")) };
        dialog.Controls.Add(text); DesktopTheme.Apply(dialog, _theme.Text); dialog.ShowDialog(this); return Task.CompletedTask;
    }
    private Task ResetDisplaySettings()
    {
        if (_demo || MessageBox.Show(this, "Preserve the current display files and reset them for the next launch? The app will close. Keys, history and library preferences stay unchanged.", "Reset display settings", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return Task.CompletedTask;
        SettingsFile.PreserveAndReset(_preferencesPath); new DesktopPreferences().Save(_preferencesPath); _skipPreferenceSave = true; Close(); return Task.CompletedTask;
    }
    private bool _skipPreferenceSave;
    private async Task ResetLibraryPreferences()
    {
        if (_demo || _statePath is null || MessageBox.Show(this, "Preserve and reset this account's favorites, exclusions and cached progress? Steam achievements and operation history will not change.", "Reset library preferences", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        SettingsFile.PreserveAndReset(_statePath); new LibraryState().Save(_statePath); await RefreshLibrary(); UpdateSettingsNotice();
    }
    private Task PreviewDiagnostics()
    {
        string report = DiagnosticReport.Create("1.11.3", _theme.Text, _main.Count + _family.Count, _journal.History.Count, _busy, _usingSnapshot, _preferences.ReadOnly || _state.ReadOnly || _journal.ReadOnly);
        using var dialog = new Form { Text = "Preview diagnostic report · nothing is uploaded", Size = new(680, 500), StartPosition = FormStartPosition.CenterParent, Font = Font };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 }; layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(new ThemeTextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Text = report.Replace("\n", "\r\n"), AccessibleName = "Exact diagnostic report preview" });
        var save = new ThemeButton { Text = "Save this report locally…", AutoSize = true };
        save.Click += (_, _) =>
        {
            using var picker = new SaveFileDialog { Filter = "Text report (*.txt)|*.txt", FileName = "SteamAchievementUnlocker-diagnostic.txt", OverwritePrompt = true };
            if (picker.ShowDialog(dialog) != DialogResult.OK) return;
            try { File.WriteAllText(picker.FileName, report); MessageBox.Show(dialog, "Report saved locally. Nothing was uploaded."); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageBox.Show(dialog, "Report could not be saved. Check folder access and free space."); }
        };
        layout.Controls.Add(save); dialog.Controls.Add(layout); DesktopTheme.Apply(dialog, _theme.Text); dialog.ShowDialog(this); return Task.CompletedTask;
    }
    private void EnhancementSmokeChecks()
    {
        void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        _grid.CurrentCell = _grid.Rows[_visible.FindIndex(g => g.AppId == 10)].Cells[1]; UpdateDetails();
        Require(_detailText.Text.Contains("Sample Adventure") && _detailText.Text.Contains("18/25"), "Game details do not follow the selected row.");
        HandleGridKey(new(Keys.Space)); Require(_checked.Contains(10), "Space did not select the current game.");
        HandleGridKey(new(Keys.Space)); Require(!_checked.Contains(10), "Space did not clear selection.");
        Require(_grid.StandardTab && _grid.AccessibleName == "Steam games", "Grid keyboard accessibility missing.");
        _detailAchievements[10] = new() { new("RARE", "Rare sample", false) }; _detailRates[10] = new() { ["RARE"] = 0.5 }; UpdateDetails();
        Require(_detailRarity.Text.Contains("Rare sample"), "Details did not show available rarity data.");
        _showDetails.Checked = false; Require(_detailsSplit!.Panel2Collapsed, "Details did not collapse."); _showDetails.Checked = true;
        if (Environment.GetEnvironmentVariable("SAU_DETAILS_SCREENSHOT") is { Length: > 0 } path)
        { using var image = new Bitmap(Width, Height); DrawToBitmap(image, new Rectangle(Point.Empty, Size)); image.Save(path, System.Drawing.Imaging.ImageFormat.Png); }
    }
}
