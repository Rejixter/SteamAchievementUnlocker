namespace SteamAchievementUnlocker;

public sealed partial class DesktopForm
{
    private bool _sessionReady;
    private void ResetSession()
    {
        _sessionReady = false; _account = 0; _statePath = null; _journalPath = null;
        _checked.Clear(); _results.Clear(); _retryQueue.Begin(0, false);
        _main = new(); _family = new(); _visible = new(); _state = new(); _journal = new(); _inventory = new();
        _detailAchievements.Clear(); _detailRates.Clear(); _snapshotNotice = ""; _usingSnapshot = false; _libraryLoadedAt = null;
        _keyVerification = "Not verified: refresh has not completed.";
        UpdateDetails(); UpdateSessionStatus(); UpdateSettingsNotice();
    }
    private void BindAccount(ulong account)
    {
        _account = account;
        _statePath = LibraryState.PathForAccount(UserSettings.DirectoryPath, account);
        _journalPath = OperationJournal.PathForAccount(UserSettings.DirectoryPath, account);
        _state = LibraryState.Load(_statePath); _journal = OperationJournal.Load(_journalPath, account);
    }
    private void AcceptanceChecks()
    {
        void Require(bool ok, string name) { if (!ok) throw new InvalidOperationException("Acceptance: " + name); Console.WriteLine("PASS: " + name); }
        Require(Path.GetFileName(UserSettings.DirectoryPath).StartsWith("sau-acceptance-", StringComparison.Ordinal), "isolated settings scope");
        Require(!UserSettings.HasSavedKey && UserSettings.ReadApiKey() == "", "fresh settings never discover a real credential");
        Require(!DesktopPreferences.Load(_preferencesPath).TourSeen, "fresh profile starts with onboarding pending");
        new DesktopPreferences { TourSeen = true, Animations = false }.Save(_preferencesPath);
        Require(DesktopPreferences.Load(_preferencesPath).TourSeen && !DesktopPreferences.Load(_preferencesPath).Animations, "onboarding and reduced motion persist in isolated profile");
        ResetSession(); BindAccount(101); _main = new() { new(10, "Account A sample", true) };
        _state.Favorites.Add(10); _state.Save(_statePath!); _journal.Begin(_main, true); _journal.Start(10); _journal.Save(_journalPath!);
        _state.Progress[10] = new(1, 1, DateTimeOffset.UtcNow); _state.Save(_statePath!);
        _checked.Add(10); _results[10] = "A result"; _retryQueue.Begin(101, true); _retryQueue.Record(10, false);
        ResetSession(); BindAccount(202); _main = new() { new(20, "Account B sample", true) }; Rebuild();
        Require(_checked.Count == 0 && _results.Count == 0 && _state.Favorites.Count == 0 && _journal.Pending is null && _journal.Account == 202, "switching accounts clears selections, results, preferences and pending work");
        Require(_retryQueue.Visible(202, _main, _state).Count == 0, "account switch clears retry queue");
        ResetSession(); BindAccount(101);
        Require(_state.Progress[10].Complete && _state.Favorites.Contains(10) && _journal.Pending?.Active == 10 && _journal.RecoverInterrupted(), "returning account loads only its own interrupted queue");
        ResetSession(); Rebuild();
        Require(!_sessionReady && _account == 0 && _visible.Count == 0 && _journalPath is null && _statePath is null, "failed refresh leaves no actionable stale account state");
        Require(!CanUseSteam(), "unready session refuses Steam operations");
    }
}
