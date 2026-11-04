using System.Drawing;
using System.Windows.Forms;

namespace SteamAchievementUnlocker;

public sealed partial class DesktopForm
{
    private readonly System.Windows.Forms.Timer _searchDelay = new() { Interval = 220 };
    private readonly ThemeComboBox _view = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230, AccessibleName = "Saved library view" };
    private readonly Label _sessionStatus = new() { AutoSize = true, MaximumSize = new(650, 0) };
    private readonly Label _cacheStatus = new() { AutoSize = true, MaximumSize = new(650, 0) };
    private bool _tourOpen;
    private string _snapshotNotice = "";
    private bool _usingSnapshot;
    private DateTimeOffset? _libraryLoadedAt;

    private SteamGame? GameAt(int row) => row >= 0 && row < _visible.Count ? _visible[row] : null;
    private void FlushSearch()
    {
        if (!_searchDelay.Enabled) return;
        uint? current = GameAt(_grid.CurrentCell?.RowIndex ?? -1)?.AppId;
        Rebuild(); int row = _visible.FindIndex(g => g.AppId == current);
        _grid.CurrentCell = row >= 0 ? _grid.Rows[row].Cells[1] : null;
    }
    private IEnumerable<SteamGame> DisplayedGames()
    {
        if (_grid.RowCount == 0 || _grid.FirstDisplayedScrollingRowIndex < 0) return Enumerable.Empty<SteamGame>();
        return _visible.Skip(_grid.FirstDisplayedScrollingRowIndex).Take(_grid.DisplayedRowCount(true) + 1);
    }
    private object? CellValue(int row, int column)
    {
        if (GameAt(row) is not { } game) return null;
        return _grid.Columns[column].Name switch
        {
            "Selected" => _checked.Contains(game.AppId), "Favorite" => _state.Favorites.Contains(game.AppId),
            "Game" => game.Name, "AppID" => game.AppId,
            "Progress" => _state.Progress.TryGetValue(game.AppId, out var p) ? p.Label : "Not checked",
            "Source" => game.FromCache ? "Cached · unverified" : game.IsInstalled ? "Installed" : _usingSnapshot ? "Owned snapshot · stale" : "Owned / manual",
            "Last result" => _results.GetValueOrDefault(game.AppId, ""), _ => null
        };
    }
    private void SetFavorite(uint id, bool favorite)
    {
        if (favorite) _state.Favorites.Add(id); else _state.Favorites.Remove(id);
        SaveState(); BeginInvoke(new Action(() => { if (!IsDisposed) Rebuild(); }));
    }
    private void InitializeQol()
    {
        _searchDelay.Tick += (_, _) => Rebuild();
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Favorite", HeaderText = "Favorite", Width = 72, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
        _grid.Columns["Favorite"].DisplayIndex = 2;
        _view.Items.AddRange(new object[] { "All games", "Favorites", "Started, unfinished", "Almost complete (≤5 left)" });
        _view.SelectedIndex = _preferences.View;
        _view.SelectedIndexChanged += (_, _) => Rebuild(); _operationControls.Add(_view);
        var libraryTools = Descendants(this).OfType<TabPage>().First(p => p.Text == "Library & history").Controls[0];
        libraryTools.Controls.Add(_view);
        libraryTools.Controls.Add(ActionButton("Guided tour (F1)", () => { StartTour(); return Task.CompletedTask; }));
        var settings = (FlowLayoutPanel)_navigation.TabPages[2].Controls[0];
        settings.Controls.Add(new Label { Text = "Session & library sources", AutoSize = true, Font = new(Font, FontStyle.Bold), Margin = new(0, 22, 0, 8) });
        settings.Controls.Add(_sessionStatus);
        settings.Controls.Add(ActionButton("Replay guided tour", () => { StartTour(); return Task.CompletedTask; }));
        settings.Controls.Add(new Label { Text = "Cover cache", AutoSize = true, Font = new(Font, FontStyle.Bold), Margin = new(0, 22, 0, 8) });
        settings.Controls.Add(_cacheStatus);
        settings.Controls.Add(ActionButton("Refresh cache information", UpdateCacheInfo));
        settings.Controls.Add(ActionButton("Clear cover cache", () => ResetCovers(null)));
        _navigation.SelectedIndexChanged += async (_, _) =>
        {
            if (_navigation.SelectedIndex == 2) { UpdateSessionStatus(); await Guard(UpdateCacheInfo); }
        };
        _menu.Items.Add("Toggle favorite").Click += (_, _) =>
        { if (!_busy && GameAt(_grid.CurrentCell?.RowIndex ?? -1) is { } game) SetFavorite(game.AppId, !_state.Favorites.Contains(game.AppId)); };
        _menu.Items.Add("Redownload this cover").Click += async (_, _) =>
        { if (!_busy && GameAt(_grid.CurrentCell?.RowIndex ?? -1) is { } game) await Guard(() => ResetCovers(game.AppId)); };
        if (_preferences.Left is int left && _preferences.Top is int top)
        {
            var desired = new Rectangle(left, top, Width, Height);
            var screen = Screen.AllScreens.FirstOrDefault(s => Rectangle.Intersect(s.WorkingArea, desired) is { Width: >= 200, Height: >= 100 });
            if (screen is not null)
            {
                StartPosition = FormStartPosition.Manual; var area = screen.WorkingArea;
                Location = new(Math.Clamp(left, area.Left, Math.Max(area.Left, area.Right - Width)), Math.Clamp(top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
            }
        }
        InitializeEnhancements();
    }
    private void UpdateSessionStatus()
    {
        _sessionStatus.Text = _demo ? "Demo: sample library only. Steam is never initialized." :
            $"Steam account (from local Steam settings): {(_account == 0 ? "Not detected" : _account.ToString())}\n" +
            "Live access is checked again before each achievement operation.\n" +
            $"Main library: {_main.Count} · Family/cache candidates: {_family.Count}\n" +
            "Installed: local Steam manifests. Owned, uninstalled: optional Steam Web API.\n" +
            "Family/cache: local candidates, not verified ownership or access.\n" +
            (_libraryLoadedAt is { } time ? $"Last library load: {time.LocalDateTime:g}. " : "Library has not loaded yet. ") + _snapshotNotice;
    }
    private async Task UpdateCacheInfo()
    {
        if (_demo) { _cacheStatus.Text = "Demo: cache files are not accessed."; return; }
        var info = await Task.Run(_artwork.CacheInfo);
        if (!IsDisposed) _cacheStatus.Text = $"{info.Count} covers · {info.Bytes / 1048576d:0.0} MB\nLast image saved: {(info.LastWrite is { } date ? date.ToLocalTime().ToString("g") : "Never")}\nCached images work offline. Right-click a game to redownload its cover.";
    }
    private async Task ResetCovers(uint? id)
    {
        if (_demo || _busy || _tourOpen) return;
        if (!id.HasValue && MessageBox.Show(this, "Delete downloaded covers and remembered missing-cover results? Other settings and history will be kept. Downloads will remain paused until you resume them.", "Clear cover cache", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        bool wasPaused = _artPaused; _artPaused = true; Busy(true);
        try
        {
            while (_artLoading && !IsDisposed) await Task.Delay(50);
            if (IsDisposed) return;
            _artwork.Reset(id);
            foreach (uint key in _artImages.Keys.Where(k => !id.HasValue || k == id.Value).ToArray()) { _artImages[key].Dispose(); _artImages.Remove(key); _artStarted.Remove(key); }
            if (id.HasValue) { _artMissing.Remove(id.Value); _artAttempted.Remove(id.Value); _artCached.Remove(id.Value); }
            else { _artMissing.Clear(); _artAttempted.Clear(); _artCached.Clear(); }
            _artPaused = !id.HasValue || wasPaused; _pauseArtwork.Text = _artPaused ? "Resume cover downloads" : "Pause cover downloads";
            _artStatus.Text = _artPaused ? "Covers: paused" : "Covers: ready to redownload";
            await UpdateCacheInfo(); Log(id.HasValue ? "Cover reset. Resume downloads if paused." : "Cover cache cleared. Resume downloads when ready.");
        }
        finally { if (!IsDisposed) { _pauseArtwork.Text = _artPaused ? "Resume cover downloads" : "Pause cover downloads"; Busy(false); } }
    }
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls) { yield return child; foreach (var next in Descendants(child)) yield return next; }
    }
    private List<TourStep> TourSteps()
    {
        Control Button(string name) => Descendants(this).OfType<Button>().First(b => b.Text == name);
        void Library() => _navigation.SelectedIndex = 0;
        return new()
        {
            new("Your library, your choices", "Installed games come from Steam's local files; no API key is required. An optional protected key adds owned, uninstalled games. This tour explains the controls without starting any Steam action. Use Next, Back, or Esc to skip.", _tabs, Library),
            new("Refresh and account safety", "Refresh library after switching Steam accounts. The account displayed in Settings comes from local Steam settings; actual access is checked before each achievement operation.", Button("Refresh library"), Library),
            new("Find games", "Search by name or AppID. Progress filters require a fresh Read progress result. Hidden games are removed from your selection. Ctrl+F focuses search.", _search, Library),
            new("Separate libraries", "Main library contains installed and owned games. Family / cached contains extra unverified candidates. Excluded games stay out of batches until restored.", _tabs, Library),
            new("Favorites and saved views", "Check Favorite beside a game, or right-click it. Choose Favorites, Started, unfinished, or Almost complete here. Views use current account data and are remembered when you close the app.", _view, Library),
            new("Read before changing", "Check Select beside games, then Read progress. Reading may show you as in-game on Steam. This button only reads achievements; the tour never clicks it.", Button("Read progress"), Library),
            new("Choose achievements", "Open achievements for the current row, or double-click it. Browse descriptions, Steam global rarity and locked/unlocked filters. Select specific achievements there.", Button("Open achievements"), Library),
            new("Review every batch", "Unlock selected reviews the checked games and requires an explicit confirmation. Only confirmed Steam saves count as success. Uncertain outcomes must be read again; nothing retries automatically.", _unlock, Library),
            new("Covers and offline browsing", "Covers download in the background and stay cached across launches. Pause independently here. Settings shows cache size; right-click a game to redownload one cover. An owned-library snapshot is labeled stale when used offline.", _pauseArtwork, Library),
            new("History and recovery", "History records per-game results for this account. Pending batches need review before resuming. A recovery notice explains when a backup was used; uncertain work is checked again.", _navigation, () => _navigation.SelectedIndex = 1),
            new("Protected keys and session status", "Saved and verified are different: Windows protects the saved key for your user account, while Steam verification requires a successful request. Keys are never prefilled. Scroll down for session details, cache tools and Replay guided tour. F1 also replays this tour.", _keyInput, () => _navigation.SelectedIndex = 2)
        };
    }
    private void StartTour()
    {
        if (_busy || _tourOpen) return;
        FlushSearch();
        _tourOpen = true; int page = _navigation.SelectedIndex; bool paused = _artPaused; _artPaused = true;
        try
        {
            using var tour = new GuidedTour(this, TourSteps(), _theme.Text); tour.ShowDialog(this);
            _preferences.TourSeen = true; SavePreferences();
        }
        finally { _tourOpen = false; _artPaused = paused; _navigation.SelectedIndex = page; if (!paused) _ = LoadVisibleArtwork(); }
    }
    private void QolSmokeChecks()
    {
        void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        Require(_grid.VirtualMode && _grid.RowCount == _visible.Count, "Virtual list row count mismatch.");
        _grid.CurrentCell = _grid.Rows[_visible.FindIndex(g => g.AppId == 10)].Cells[1]; _checked.Add(10);
        _search.Text = "Puzzle"; FlushSearch();
        Require(_checked.Count == 0 && _grid.CurrentCell is null && _visible.Count == 1, "Pending search must flush before actions without changing the target game.");
        _search.Clear(); FlushSearch();
        _state.Favorites.Add(10); _view.SelectedIndex = 1; Require(_visible.Count == 1 && _visible[0].AppId == 10, "Favorite view mismatch.");
        _view.SelectedIndex = 2; Require(_visible.Count == 1 && _visible[0].AppId == 10, "Started view mismatch.");
        _view.SelectedIndex = 3; Require(_visible.Count == 0, "Almost complete must not invent unknown progress."); _view.SelectedIndex = 0;
        var original = _main; _main = Enumerable.Range(1, 10000).Select(i => new SteamGame((uint)i, "Game " + i, true)).ToList();
        Rebuild(); Require(_grid.RowCount == 10000 && CellValue(9999, 1) is string, "Large virtual library failed."); _main = original; Rebuild();
        using var tour = new GuidedTour(this, TourSteps(), _theme.Text); tour.Show(this);
        for (int i = 0; i < TourSteps().Count; i++) { tour.MoveTo(i); Require(tour.StepIndex == i, "Tour navigation failed."); }
        tour.MoveTo(0); Require(_navigation.SelectedIndex == 0, "Tour did not reveal Library.");
        if (Environment.GetEnvironmentVariable("SAU_TOUR_SCREENSHOT") is { Length: > 0 } path)
        {
            tour.MoveTo(5);
            using var image = new Bitmap(Width, Height); DrawToBitmap(image, new Rectangle(Point.Empty, Size));
            using var card = new Bitmap(tour.Width, tour.Height); tour.DrawToBitmap(card, new Rectangle(Point.Empty, tour.Size));
            using (var graphics = Graphics.FromImage(image)) graphics.DrawImageUnscaled(card, tour.Left - Left, tour.Top - Top);
            image.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        tour.MoveTo(1); Descendants(tour).OfType<Button>().Single(b => b.Text == "Back").PerformClick();
        Require(tour.StepIndex == 0, "Tour Back button failed.");
        Descendants(tour).OfType<Button>().Single(b => b.Text == "Next").PerformClick(); Require(tour.StepIndex == 1, "Tour Next button failed.");
        tour.MoveTo(TourSteps().Count - 1); Descendants(tour).OfType<Button>().Single(b => b.Text == "Finish").PerformClick();
        Require(tour.Completed, "Tour Finish button failed."); _navigation.SelectedIndex = 0;
    }
}
