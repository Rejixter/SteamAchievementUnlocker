using System.Net;
using System.Text.Json;
using SteamAchievementUnlocker;

// Isolated child is deliberately killed by the parent; it never references Steam APIs.
if (args.Length == 3 && args[0] == "--crash-journal-child")
{
    string root = Path.GetFullPath(args[1]);
    if (!Path.GetFileName(Path.GetDirectoryName(root)!).StartsWith("sau-tests-", StringComparison.Ordinal) || !Directory.Exists(root)) return;
    string path = OperationJournal.PathForAccount(root, 101);
    var childJournal = new OperationJournal { Account = 101 }; var game = new SteamGame(10, "Synthetic crash sample", true);
    childJournal.Begin(new[] { game }, true); childJournal.Save(path);
    if (args[2] != "before-start") { childJournal.Start(10); childJournal.Save(path); }
    if (args[2] == "after-remote-confirmation") File.WriteAllText(Path.Combine(root, "synthetic-server-confirmed.txt"), "Synthetic result only; no Steam access.");
    File.WriteAllText(Path.Combine(root, "ready.txt"), "Ready");
    await Task.Delay(Timeout.Infinite); return;
}

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
    passed++;
}
string App(string data) => "{\"10\":{\"success\":true,\"data\":" + data + "}}";
AchievementSupport Parse(string json) => AchievementCatalog.ParseStoreResponse(json, 10);
Check(Parse(App("{\"achievements\":{\"total\":3}}")) == AchievementSupport.Available, "Positive achievement count is kept");
Check(Parse(App("{\"categories\":[{\"id\":22}]}")) == AchievementSupport.Available, "Achievement category is kept");
Check(Parse(App("{\"categories\":[{\"id\":1}]}")) == AchievementSupport.None, "Known categories without achievements are filtered");
Check(Parse(App("{\"achievements\":{\"total\":0}}")) == AchievementSupport.None, "Explicit zero achievements is filtered");
Check(Parse(App("{\"achievements\":{\"total\":0},\"categories\":[{\"id\":22}]}")) == AchievementSupport.Available, "Conflicting positive evidence keeps the game");
Check(Parse("{\"10\":{\"success\":false}}") == AchievementSupport.Unknown, "Unavailable store entry stays unknown");
Check(Parse(App("{}")) == AchievementSupport.Unknown, "Missing metadata does not hide games");
Check(Parse(App("{\"categories\":[]}")) == AchievementSupport.Unknown, "Empty metadata does not hide games");
Check(Parse(App("{\"categories\":[{\"id\":\"invalid\"}]}")) == AchievementSupport.Unknown, "Malformed category stays unknown");
Check(Parse("[]") == AchievementSupport.Unknown, "Wrong response shape stays unknown");
Check(Parse("{\"11\":{\"success\":true,\"data\":{\"achievements\":{\"total\":0}}}}") == AchievementSupport.Unknown, "Other app metadata is ignored");

string scratch = Path.Combine(Path.GetTempPath(), "sau-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
try
{
    var fakeProtector = new FakeProtector(); string isolatedStore = Path.Combine(scratch, "credentials"); Directory.CreateDirectory(isolatedStore);
    var keyStore = new CredentialStore(isolatedStore, fakeProtector); string syntheticKey = new('c', 32);
    File.WriteAllText(keyStore.LegacyPath, syntheticKey);
    Check(keyStore.Read() == syntheticKey && !File.Exists(keyStore.LegacyPath) && File.Exists(keyStore.ProtectedPath), "Legacy migration deletes plaintext only after protected readback");
    byte[] previousProtected = File.ReadAllBytes(keyStore.ProtectedPath); fakeProtector.FailRead = true;
    bool failedReadback = false; try { keyStore.Save(new string('d', 32)); } catch (System.Security.Cryptography.CryptographicException) { failedReadback = true; }
    Check(failedReadback && File.ReadAllBytes(keyStore.ProtectedPath).SequenceEqual(previousProtected) && Directory.GetFiles(isolatedStore, "*.tmp").Length == 0, "Failed readback preserves the previous encrypted key and cleans temporary files");
    File.WriteAllText(keyStore.LegacyPath, syntheticKey);
    Check(keyStore.Read() == "" && keyStore.Error is not null, "Unreadable protected data never falls back to a legacy plaintext key");
    fakeProtector.FailRead = false;
    var blockedStore = new CredentialStore(Path.Combine(scratch, "blocked-credentials"), new FakeProtector { FailProtect = true }); Directory.CreateDirectory(Path.GetDirectoryName(blockedStore.LegacyPath)!); File.WriteAllText(blockedStore.LegacyPath, syntheticKey);
    Check(blockedStore.Read() == "" && File.Exists(blockedStore.LegacyPath) && !File.Exists(blockedStore.ProtectedPath), "Failed migration preserves the legacy file but refuses to use its key");
    bool invalidKeyRejected = false; try { keyStore.Save("invalid"); } catch (ArgumentException) { invalidKeyRejected = true; }
    Check(invalidKeyRejected && File.ReadAllBytes(keyStore.ProtectedPath).SequenceEqual(previousProtected), "Invalid replacement cannot overwrite a protected key");
    keyStore.Save(""); Check(!keyStore.Exists, "Explicit remove clears protected and legacy copies");
    using (var request = OwnedLibrary.Request(syntheticKey, 100))
        Check(!request.RequestUri!.ToString().Contains(syntheticKey) && !request.RequestUri.Query.Contains("key=") && request.Headers.GetValues("x-webapi-key").Single() == syntheticKey, "Credentials use the dedicated Steam header, never URL parameters");
    using (var transport = OwnedLibrary.CreateHandler()) Check(!transport.AllowAutoRedirect && !transport.UseCookies && transport.ServerCertificateCustomValidationCallback is null, "Authenticated transport refuses redirects and retains normal certificate validation");
    using var verifiedHttp = new HttpClient(new FakeHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("{\"response\":{\"game_count\":0}}") }));
    var verifiedLibrary = await OwnedLibrary.FetchAsync(verifiedHttp, syntheticKey, 100);
    Check(verifiedLibrary.Games?.Count == 0 && verifiedLibrary.Verification.StartsWith("Verified"), "Successful authenticated empty library is distinguished from a saved key");
    using var rejectedHttp = new HttpClient(new FakeHandler(_ => new(HttpStatusCode.Forbidden)));
    Check((await OwnedLibrary.FetchAsync(rejectedHttp, syntheticKey, 100)).Verification.StartsWith("Rejected"), "Steam rejection is explicit, separate from connection errors");
    using var brokenHttp = new HttpClient(new FakeHandler(_ => throw new HttpRequestException("Sensitive diagnostic " + syntheticKey)));
    var safeFailure = await OwnedLibrary.FetchAsync(brokenHttp, syntheticKey, 100);
    Check(!safeFailure.Verification.Contains(syntheticKey) && safeFailure.Verification.StartsWith("Not verified"), "Transport exception text cannot disclose key contents in UI diagnostics");
    int redirectRequests = 0;
    using var redirectHttp = new HttpClient(new FakeHandler(_ => { redirectRequests++; return new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://example.invalid/") } }; }));
    Check((await OwnedLibrary.FetchAsync(redirectHttp, syntheticKey, 100)).Games is null && redirectRequests == 1, "Redirect responses do not trigger application-level credential forwarding");
    Check(RarityStyle.IsRare(0) && RarityStyle.IsRare(5) && !RarityStyle.IsRare(5.01) && !RarityStyle.IsRare(null) && !RarityStyle.IsRare(double.NaN) && !RarityStyle.IsRare(-1), "Neon rarity requires a real bounded Steam percentage");
    Check(RarityStyle.Intensity(0, false) == 1 && RarityStyle.Intensity(1200, false) == 1, "Disabling animations keeps rare frames static");
    Check(Enumerable.Range(0, 2400).All(t => RarityStyle.Intensity(t, true) >= 0.65 && RarityStyle.Intensity(t, true) <= 1), "Neon pulse stays visible throughout its slow brightness cycle");
    string protectedKey = Path.Combine(scratch, "test-key.dat"); string dummyKey = new('a', 32);
    bool dpapiAvailable = true;
    try { CredentialVault.Save(protectedKey, dummyKey); }
    catch (System.Security.Cryptography.CryptographicException) { dpapiAvailable = false; Console.WriteLine("SKIP: Windows DPAPI is unavailable in this test user profile. New credentials still fail closed; no plaintext fallback."); }
    if (dpapiAvailable)
    {
        Check(CredentialVault.Read(protectedKey) == dummyKey && !File.ReadAllBytes(protectedKey).AsSpan().SequenceEqual(System.Text.Encoding.UTF8.GetBytes(dummyKey)), "Windows current-user protection round-trips without plaintext storage");
        File.WriteAllText(protectedKey, "invalid protected bytes");
        bool rejectedProtectedData = false; try { CredentialVault.Read(protectedKey); } catch (System.Security.Cryptography.CryptographicException) { rejectedProtectedData = true; }
        Check(rejectedProtectedData, "Corrupt protected credentials are rejected without plaintext fallback");
    }
    var percentages = GlobalAchievements.Parse("{\"achievementpercentages\":{\"achievements\":[{\"name\":\"FIRST\",\"percent\":25.2},{\"name\":\"ZERO\",\"percent\":\"0\"},{\"name\":\"BAD\",\"percent\":101}]}}");
    Check(percentages.Count == 2 && percentages["FIRST"] == 25.2 && percentages["ZERO"] == 0, "Global rates accept real zero and reject out-of-range values");
    Check(GlobalAchievements.Parse("broken").Count == 0 && GlobalAchievements.Parse("{}").Count == 0, "Unavailable global rates never become invented zeros");
    int globalCalls = 0;
    using var globalHttp = new HttpClient(new FakeHandler(_ => { globalCalls++; return new(HttpStatusCode.OK) { Content = new StringContent("{\"achievementpercentages\":{\"achievements\":[{\"name\":\"FIRST\",\"percent\":25.2}]}}") }; }));
    string globalPath = Path.Combine(scratch, "global");
    var firstGlobal = await GlobalAchievements.LoadAsync(globalHttp, 620, globalPath, CancellationToken.None);
    var secondGlobal = await GlobalAchievements.LoadAsync(globalHttp, 620, globalPath, CancellationToken.None);
    Check(globalCalls == 1 && secondGlobal["FIRST"] == 25.2, "Global rates cache survives repeated reads");
    var journal = new OperationJournal(); var journalGame = new SteamGame(10, "Example", true);
    journal.Begin(new[] { journalGame, new SteamGame(20, "Other", false) }, true); journal.Start(10);
    string journalPath = OperationJournal.PathForAccount(scratch, 100); journal.Save(journalPath);
    var recoveredJournal = OperationJournal.Load(journalPath);
    Check(recoveredJournal.RecoverInterrupted() && !recoveredJournal.RecoverInterrupted() && recoveredJournal.History.Single().Outcome == "Uncertain", "Crash recovery records uncertainty once and keeps pending games");
    Check(recoveredJournal.Pending!.Games.Count == 2 && recoveredJournal.Pending.Active == 10, "Uncertain active game remains identifiable before resume");
    recoveredJournal.Complete(journalGame, "Unlock all", true, "Confirmed by Steam");
    Check(recoveredJournal.Pending!.Games.Single().AppId == 20 && recoveredJournal.History.Last().Outcome == "Confirmed", "Confirmed games leave the pending queue atomically with history");
    recoveredJournal.Complete(new(20, "Other", false), "Unlock all", false, "Save failed or unconfirmed.");
    Check(recoveredJournal.Pending is null && recoveredJournal.History.Last().Outcome == "Uncertain", "Unconfirmed saves are not classified as success");
    Check(OperationJournal.PathForAccount(scratch, 100) != OperationJournal.PathForAccount(scratch, 200), "Pending batches and history are account-scoped");
    var summaryState = new LibraryState(); summaryState.Progress[10] = new(5, 5, DateTimeOffset.UtcNow); summaryState.Progress[20] = new(5, 5, DateTimeOffset.UtcNow.AddDays(-2));
    var summary = LibrarySummary.Calculate(new[] { journalGame, journalGame, new SteamGame(20, "Unknown", false) }, summaryState);
    Check(summary.Total == 2 && summary.Completed == 1 && summary.Unknown == 1 && summary.KnownAchievements == 5, "Overview deduplicates and excludes expired counts from achievement totals");
    journal.Begin(new[] { journalGame }, true); summaryState.Excluded[10] = journalGame;
    Check(journal.Resumable(new[] { journalGame }, summaryState).Count == 0 && journal.Resumable(Array.Empty<SteamGame>(), new()).Count == 0, "Resume never restores excluded or missing library games");
    File.WriteAllText(journalPath, "broken"); var backupRecovery = OperationJournal.Load(journalPath);
    Check(backupRecovery.Pending?.Games.Count == 2 && backupRecovery.RecoveryNotice is not null && backupRecovery.Pending.RecheckAll, "Corrupt journal restores its backup, reports recovery and requires fresh reads before resume");
    backupRecovery.Save(journalPath);
    Check(OperationJournal.Load(journalPath).Pending?.Games.Count == 2 && OperationJournal.Load(journalPath).RecoveryNotice is null, "Recovered journal replaces corrupt primary without losing its backup");
    File.WriteAllText(journalPath, "broken"); File.WriteAllText(journalPath + ".bak", "also broken");
    var unrecoverable = OperationJournal.Load(journalPath);
    Check(unrecoverable.Pending is null && unrecoverable.RecoveryNotice is not null, "Unrecoverable journal warns instead of silently starting empty");
    string ArtworkJson(string url) => "{\"620\":{\"success\":true,\"data\":{\"header_image\":\"" + url + "\"}}}";
    Check(SteamArtwork.ImageUrl(ArtworkJson("https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/620/header.jpg"), 620) is not null, "Steam artwork permits official HTTPS CDN");
    Check(SteamArtwork.ImageUrl(ArtworkJson("https://steamstatic.com.attacker.test/header.jpg"), 620) is null, "Artwork rejects lookalike external hosts");
    Check(SteamArtwork.ImageUrl(ArtworkJson("http://shared.fastly.steamstatic.com/header.jpg"), 620) is null, "Artwork rejects insecure URLs");
    Check(SteamArtwork.ImageUrl(ArtworkJson("https://secret@shared.fastly.steamstatic.com/header.jpg"), 620) is null, "Artwork rejects embedded credentials");
    Check(SteamArtwork.ImageUrl("broken", 620) is null && SteamArtwork.ImageUrl(ArtworkJson("https://shared.fastly.steamstatic.com/header.jpg"), 10) is null, "Missing or malformed artwork metadata is harmless");
    byte[] fixture;
    using (var sample = new System.Drawing.Bitmap(2, 2))
    using (var imageData = new MemoryStream()) { sample.Save(imageData, System.Drawing.Imaging.ImageFormat.Jpeg); fixture = imageData.ToArray(); }
    int artworkCalls = 0;
    using var artworkHttp = new HttpClient(new FakeHandler(request =>
    {
        artworkCalls++;
        return new(HttpStatusCode.OK) { Content = request.RequestUri!.Host == "store.steampowered.com" ?
            new StringContent(ArtworkJson("https://shared.fastly.steamstatic.com/header.jpg")) : new ByteArrayContent(fixture) };
    }));
    var artworkCache = new SteamArtwork(artworkHttp, Path.Combine(scratch, "artwork"));
    using var cover = await artworkCache.GetAsync(620, CancellationToken.None);
    Check(cover is { Width: 184, Height: 86 } && artworkCalls == 2, "Artwork downloads and creates a bounded thumbnail");
    using var cachedCover = await artworkCache.GetAsync(620, CancellationToken.None);
    Check(cachedCover is not null && artworkCalls == 2, "Cached artwork avoids network requests");
    File.SetLastWriteTimeUtc(Path.Combine(scratch, "artwork", "620.jpg"), DateTime.UtcNow.AddDays(-90));
    var restartedArtwork = new SteamArtwork(artworkHttp, Path.Combine(scratch, "artwork"));
    using var persistentCover = await restartedArtwork.GetAsync(620, CancellationToken.None);
    Check(persistentCover is not null && artworkCalls == 2 && restartedArtwork.HasCached(620), "Restart reuses old downloaded covers without network requests");
    var planned = SteamArtwork.Queue(new uint[] { 1, 2, 3, 4 }, new uint[] { 3 }, new HashSet<uint>(), new HashSet<uint>(), id => id == 2);
    Check(planned.SequenceEqual(new uint[] { 3, 1, 4 }), "Preload covers the whole library while prioritizing visible games and skipping disk cache");
    Check(SteamArtwork.Queue(new uint[] { 1, 1, 0, 2 }, new uint[] { 2 }, new HashSet<uint> { 2 }, new HashSet<uint> { 1 }, _ => false).SequenceEqual(new uint[] { 2 }), "Preload deduplicates IDs and excludes attempted or invalid games");
    Check(SteamArtwork.Queue(new uint[] { 1, 2 }, new uint[] { 2 }, new HashSet<uint>(), new HashSet<uint>(), _ => true).SequenceEqual(new uint[] { 2 }), "Visible cached covers remain eligible for memory loading");
    int missingArtworkCalls = 0;
    using var missingArtHttp = new HttpClient(new FakeHandler(_ => { missingArtworkCalls++; return new(HttpStatusCode.NotFound); }));
    var missingArt = new SteamArtwork(missingArtHttp, Path.Combine(scratch, "missing-artwork"));
    Check(await missingArt.GetAsync(1, CancellationToken.None) is null && await missingArt.GetAsync(1, CancellationToken.None) is null && missingArtworkCalls == 1, "Missing artwork uses fallback without repeated requests");
    var restartedMissingArt = new SteamArtwork(missingArtHttp, Path.Combine(scratch, "missing-artwork"));
    Check(await restartedMissingArt.GetAsync(1, CancellationToken.None) is null && missingArtworkCalls == 1 && restartedMissingArt.MissingCount(new uint[] { 1, 1, 2 }) == 1, "Missing-cover cache persists across restart and counts distinct failures");
    var qolState = new LibraryState();
    var qolGames = new List<SteamGame> { new(1, "Zulu", true), new(2, "Alpha", true), new(3, "Unknown", true), new(4, "Stale", true) };
    qolState.Progress[1] = new(2, 10, DateTimeOffset.UtcNow);
    qolState.Progress[2] = new(10, 10, DateTimeOffset.UtcNow);
    qolState.Progress[4] = new(10, 10, DateTimeOffset.UtcNow.AddDays(-2));
    Check(DesktopPreferences.Arrange(qolGames, qolState, 1, 0).Select(g => g.AppId).ToHashSet().SetEquals(new uint[] { 3, 4 }), "Unchecked filter includes expired counts");
    Check(DesktopPreferences.Arrange(qolGames, qolState, 2, 0).Single().AppId == 1, "Incomplete filter excludes completed and unknown games");
    Check(DesktopPreferences.Arrange(qolGames, qolState, 3, 0).Single().AppId == 2, "Completed filter excludes expired counts");
    Check(DesktopPreferences.Arrange(qolGames, qolState, 0, 2).Select(g => g.AppId).Take(2).SequenceEqual(new uint[] { 1, 2 }), "Ascending progress sorts unknowns last");
    Check(DesktopPreferences.Arrange(qolGames, qolState, 0, 3).Select(g => g.AppId).Take(2).SequenceEqual(new uint[] { 2, 1 }), "Descending progress sorts unknowns last");
    Check(DesktopPreferences.Arrange(qolGames, qolState, 0, 4).First().AppId == 1, "Remaining achievements sort uses actual counts");
    var retry = new RetryQueue(); retry.Begin(100, true); retry.Record(1, false); retry.Record(2, true);
    Check(retry.Visible(100, qolGames, qolState).Single().AppId == 1 && retry.Unlock, "Retry keeps failed attempts and original operation");
    Check(retry.Visible(200, qolGames, qolState).Count == 0, "Retry cannot cross accounts");
    Check(retry.Visible(100, qolGames.Skip(1), qolState).Count == 0, "Retry cannot include hidden failures");
    qolState.Excluded[1] = qolGames[0]; Check(retry.Visible(100, qolGames, qolState).Count == 0, "Retry cannot include excluded games");
    qolState.Excluded.Clear(); retry.Record(1, true); Check(retry.Visible(100, qolGames, qolState).Count == 0, "Successful retries leave the failure queue");
    retry.Record(1, false); retry.Begin(100, false); Check(retry.Visible(100, qolGames, qolState).Count == 0 && !retry.Unlock, "New batch resets old failures");
    string prefsFile = Path.Combine(scratch, "desktop-preferences.json");
    new DesktopPreferences { Theme = "Light", Search = "Puzzle", Sort = 4, Filter = 2, Width = 1400, ColumnWeights = new float[] { 7, 32, 9, 17, 18, 25 } }.Save(prefsFile);
    var prefs = DesktopPreferences.Load(prefsFile);
    Check(prefs.Theme == "Light" && prefs.Search == "Puzzle" && prefs.Sort == 4 && prefs.Filter == 2 && prefs.Width == 1400 && prefs.ColumnWeights.Length == 6, "Desktop preferences survive restart");
    File.WriteAllText(prefsFile, "{\"Theme\":null,\"Search\":null,\"Filter\":999,\"Width\":-1,\"ColumnWeights\":[0]}");
    prefs = DesktopPreferences.Load(prefsFile);
    Check(prefs.Theme == "Dark" && prefs.Search == "" && prefs.Filter == 3 && prefs.Width == 780 && prefs.ColumnWeights.Length == 0, "Invalid preferences are bounded safely");
    File.WriteAllText(prefsFile, "broken"); Check(DesktopPreferences.Load(prefsFile).Theme == "Light" && DesktopPreferences.Load(prefsFile).RecoveryNotice is not null, "Corrupt display settings restore their readable backup with notice");
    new DesktopPreferences { ShowCovers = false, Animations = false }.Save(prefsFile);
    Check(!DesktopPreferences.Load(prefsFile).ShowCovers && !DesktopPreferences.Load(prefsFile).Animations, "Artwork and motion preferences survive restart");
    var state = new LibraryState();
    var list = new List<SteamGame> { new(1, "Puzzle Adventure", true), new(2, "Space Game", false), new(3, "Unknown", true), new(4, "No achievements", true) };
    state.Progress[1] = new(5, 5, DateTimeOffset.UtcNow);
    state.Progress[2] = new(10, 10, DateTimeOffset.UtcNow.AddDays(-2));
    state.Progress[4] = new(0, 0, DateTimeOffset.UtcNow);
    Check(state.Filter(list, "PUZZLE", false, false).Single().AppId == 1, "Search is case-insensitive");
    Check(state.Filter(list, "2", false, false).Single().AppId == 2, "Search accepts AppIDs");
    Check(state.Filter(list, "", true, false).Select(g => g.AppId).SequenceEqual(new uint[] { 4, 2, 3 }), "Completed filter keeps unknown, stale and zero-achievement entries");
    Check(new GameProgress(2, 1, DateTimeOffset.UtcNow).Complete == false, "Invalid counts never count as completed");
    Check(new GameProgress(1, 1, DateTimeOffset.UtcNow.AddHours(2)).Complete == false, "Future timestamps never count as completed");
    state.Excluded[1] = list[0];
    Check(!state.Filter(list, "", false, false).Any(g => g.AppId == 1), "Excluded games stay out of the main list");
    Check(state.Filter(state.Excluded.Values, "", true, true).Single().AppId == 1, "Excluded games remain restorable even when completed");
    Check(LibraryState.SelectedVisible(list.Skip(1), new HashSet<uint> { 1, 2 }).Single().AppId == 2, "Hidden selections never enter a batch");
    string statePath = LibraryState.PathForAccount(scratch, 123);
    Check(statePath != LibraryState.PathForAccount(scratch, 456), "Different accounts have separate progress and exclusions");
    state.Save(statePath);
    var loadedState = LibraryState.Load(statePath);
    Check(loadedState.Excluded.ContainsKey(1) && loadedState.Progress[1].Complete, "Progress and exclusions survive restart");
    loadedState.Excluded.Remove(1); loadedState.Save(statePath);
    Check(!LibraryState.Load(statePath).Excluded.ContainsKey(1), "Restoring a game is persisted");
    File.WriteAllText(statePath, "invalid json");
    Check(LibraryState.Load(statePath).Excluded.ContainsKey(1) && LibraryState.Load(statePath).RecoveryNotice is not null, "Corrupt library settings restore their backup with notice");
    File.WriteAllText(statePath, "{\"Excluded\":null,\"Progress\":null}");
    Check(LibraryState.Load(statePath).Progress.Count == 0, "Null settings collections are repaired");
    string fakeSteam = Path.Combine(scratch, "steam");
    string sharedCache = Path.Combine(fakeSteam, "appcache", "librarycache");
    Directory.CreateDirectory(Path.Combine(sharedCache, "620"));
    File.WriteAllText(Path.Combine(sharedCache, "620_header.jpg"), "");
    File.WriteAllText(Path.Combine(sharedCache, "10_library_600x900.jpg"), "");
    File.WriteAllText(Path.Combine(sharedCache, "0_header.jpg"), "");
    File.WriteAllText(Path.Combine(sharedCache, "bad_header.jpg"), "");
    Directory.CreateDirectory(Path.Combine(fakeSteam, "config"));
    File.WriteAllText(Path.Combine(fakeSteam, "config", "loginusers.vdf"), "\"76561197960265729\" { \"MostRecent\" \"1\" }");
    string userCache = Path.Combine(fakeSteam, "userdata", "1", "config", "librarycache");
    Directory.CreateDirectory(userCache);
    File.WriteAllText(Path.Combine(userCache, "999.json"), "{}");
    File.WriteAllText(Path.Combine(userCache, "achievement_progress.json"), "{}");
    var candidates = SteamLibraryScanner.GetCachedLibraryGames(fakeSteam);
    Check(candidates.Select(g => g.AppId).SequenceEqual(new uint[] { 10, 620, 999 }), "Family candidates merge old/new cache layouts and current user cache without duplicates");
    Check(candidates.All(g => g.FromCache && !g.IsInstalled), "Cache candidates never claim installation or ownership");
    Check(SteamLibraryScanner.GetCachedLibraryGames(Path.Combine(scratch, "missing")).Count == 0, "Missing Steam cache is handled");
    var mainGames = new List<SteamGame> { new(10, "Installed", true), new(620, "Owned, not installed", false) };
    var mainSnapshot = mainGames.ToArray();
    var separate = SteamLibraryScanner.GetAdditionalCachedGames(mainGames, candidates.Concat(candidates).Append(new(0, "Invalid", false, true)));
    Check(separate.Select(g => g.AppId).SequenceEqual(new uint[] { 999 }), "Second list excludes all main-library IDs, duplicates and invalid IDs");
    Check(mainGames.SequenceEqual(mainSnapshot) && candidates.Count == 3, "Building the second list does not mutate either input library");
    Check(separate.All(g => g.FromCache && !g.IsInstalled), "Second list preserves unverified cache flags");
    Check(SteamLibraryScanner.GetAdditionalCachedGames(mainGames, mainGames).Count == 0, "A fully overlapping cache produces an empty second list");
    Check(SteamLibraryScanner.GetAdditionalCachedGames(Array.Empty<SteamGame>(), candidates).SequenceEqual(candidates), "Cache remains browsable when the main library is empty");
    var separateBatch = await BatchRunner.RunAsync(separate, _ => Task.FromResult(2), () => false, _ => { });
    Check(separateBatch.Select(g => g.AppId).SequenceEqual(new uint[] { 999 }) && mainGames.SequenceEqual(mainSnapshot), "A second-list batch only visits second-list games");
    var attempted = new List<uint>();
    var reported = new List<BatchResult>();
    var batch = await BatchRunner.RunAsync(candidates.Concat(candidates), game =>
    {
        attempted.Add(game.AppId);
        if (game.AppId == 10) throw new IOException("test process failure");
        return Task.FromResult(game.AppId == 620 ? 6 : 0);
    }, () => false, reported.Add);
    Check(attempted.SequenceEqual(new uint[] { 10, 620, 999 }) && reported.SequenceEqual(batch), "Batch is sequential, deduplicated, and continues after a worker exception");
    Check(batch[0].Status.Contains("unknown") && batch[1].Status.Contains("unconfirmed") && batch[2].Status == "Confirmed", "Failed/unconfirmed saves are never reported as success");
    int calls = 0;
    var stopped = await BatchRunner.RunAsync(candidates, _ => { calls++; return Task.FromResult(2); }, () => calls == 1, _ => { });
    Check(stopped.Count == 1 && calls == 1, "Stop prevents remaining games from starting");
    var neverStarted = await BatchRunner.RunAsync(candidates, _ => throw new Exception("Must not run"), () => true, _ => { });
    Check(neverStarted.Count == 0, "Cancellation before the first game changes nothing");
    var start = BatchRunner.CreateStartInfo(620, "Game name \"with quotes\" & spaces", true);
    Check(!start.UseShellExecute && start.ArgumentList.TakeLast(3).SequenceEqual(new[] { "--bulk-worker", "620", "Game name \"with quotes\" & spaces" }), "Worker names are literal arguments, not shell code");

    var games = new List<SteamGame> { new(10, "No achievements", true), new(620, "Achievements", false), new(999, "Unknown", true) };
    int requests = 0;
    using var client = new HttpClient(new FakeHandler(request =>
    {
        Interlocked.Increment(ref requests);
        string url = request.RequestUri!.ToString();
        return url.Contains("appids=10&") ? new(HttpStatusCode.OK) { Content = new StringContent(App("{\"categories\":[{\"id\":1}]}")) } :
            url.Contains("appids=620&") ? new(HttpStatusCode.OK) { Content = new StringContent("{\"620\":{\"success\":true,\"data\":{\"achievements\":{\"total\":51}}}}") } :
            new(HttpStatusCode.InternalServerError);
    }));
    using var namedClient = new HttpClient(new FakeHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(App("{\"name\":\"Cached family game\",\"achievements\":{\"total\":1}}")) }));
    var namedCatalog = new AchievementCatalog(namedClient, Path.Combine(scratch, "names.json"), TimeSpan.Zero);
    await namedCatalog.RefreshAsync(new[] { new SteamGame(10, "AppID 10", false, true) }, null, CancellationToken.None);
    Check(namedCatalog.GetName(10) == "Cached family game", "Store names resolve cached AppIDs");
    namedCatalog.RecordFromClient(10, true);
    var namedReload = new AchievementCatalog(namedClient, Path.Combine(scratch, "names.json"));
    Check(namedReload.GetName(10) == "Cached family game", "Client achievement updates preserve cached names");
    string cache = Path.Combine(scratch, "cache.json");
    var catalog = new AchievementCatalog(client, cache, TimeSpan.Zero);
    await catalog.RefreshAsync(games, null, CancellationToken.None);
    Check(catalog.Filter(games, true).Select(g => g.AppId).SequenceEqual(new uint[] { 620, 999 }), "Filter preserves order and unknown games");
    Check(catalog.Filter(games, false).Count == 3, "Show-all restores all games");
    await catalog.RefreshAsync(games, null, CancellationToken.None);
    Check(requests == 3, "Fresh positive, negative and unknown cache entries avoid repeated requests");
    var reopened = new AchievementCatalog(client, cache, TimeSpan.Zero);
    Check(reopened.GetSupport(10) == AchievementSupport.None, "Cache survives application restart");
    reopened.RecordFromClient(10, true);
    Check(reopened.Filter(games, true).Count == 3, "Actual client achievements override Store metadata");

    File.WriteAllText(cache, JsonSerializer.Serialize(new Dictionary<uint, AchievementCacheEntry> { [10] = new(AchievementSupport.None, DateTimeOffset.UtcNow.AddDays(-8)) }));
    var expired = new AchievementCatalog(client, cache, TimeSpan.Zero);
    Check(expired.GetSupport(10) == AchievementSupport.Unknown, "Expired negative cache cannot hide games");
    await expired.RefreshAsync(games.Take(1), null, CancellationToken.None);
    Check(requests == 4, "Expired entries are fetched again");
    File.WriteAllText(cache, "broken json");
    var corrupted = new AchievementCatalog(client, cache, TimeSpan.Zero);
    Check(corrupted.Filter(games, true).Count == 3, "Corrupt cache cannot empty the library");

    int rateCalls = 0;
    using var limitedClient = new HttpClient(new FakeHandler(_ => { Interlocked.Increment(ref rateCalls); return new(HttpStatusCode.TooManyRequests); }));
    var limited = new AchievementCatalog(limitedClient, Path.Combine(scratch, "limited.json"), TimeSpan.Zero);
    var largeLibrary = Enumerable.Range(1, 400).Select(i => new SteamGame((uint)i, "Game", true)).ToList();
    await limited.RefreshAsync(largeLibrary, null, CancellationToken.None);
    Check(rateCalls <= 2 && limited.Filter(largeLibrary, true).Count == 400, "Rate limiting stops requests and preserves the library");
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    await limited.RefreshAsync(largeLibrary, null, cancelled.Token);
    Check(limited.Filter(largeLibrary, true).Count == 400, "Cancelled scan preserves unresolved games");

    int offlineCalls = 0;
    using var offlineClient = new HttpClient(new FakeHandler(_ => { Interlocked.Increment(ref offlineCalls); throw new HttpRequestException("offline"); }));
    var offline = new AchievementCatalog(offlineClient, Path.Combine(scratch, "offline.json"), TimeSpan.Zero);
    await offline.RefreshAsync(largeLibrary, null, CancellationToken.None);
    Check(offline.Filter(largeLibrary, true).Count == 400, "Offline scan never removes unknown games");
    Check(offlineCalls <= 7, "Offline scan stops after bounded connection failures");
    int missingCalls = 0;
    using var missingClient = new HttpClient(new FakeHandler(_ => { Interlocked.Increment(ref missingCalls); return new(HttpStatusCode.OK) { Content = new StringContent("{}") }; }));
    var missing = new AchievementCatalog(missingClient, Path.Combine(scratch, "missing.json"), TimeSpan.Zero);
    await missing.RefreshAsync(largeLibrary.Take(12), null, CancellationToken.None);
    Check(missingCalls == 12, "Missing Store metadata does not stop a healthy connection");
    Check(!UserSettings.IsValidApiKey("not-a-key"), "Invalid personal key rejected");
    Check(!UserSettings.KeyPath.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase), "Personal key lives outside the release folder");

    var viewState = new LibraryState(); viewState.Favorites.Add(1);
    viewState.Progress[1] = new(3, 5, DateTimeOffset.UtcNow); viewState.Progress[2] = new(10, 10, DateTimeOffset.UtcNow);
    viewState.Progress[3] = new(1, 3, DateTimeOffset.UtcNow.AddDays(-2));
    var viewGames = new List<SteamGame> { new(1, "Started", false), new(2, "Complete", false), new(3, "Expired", false), new(4, "Unknown", false) };
    Check(LibraryViews.Apply(viewGames, viewState, 1).Single().AppId == 1, "Favorites view uses this account's favorites");
    Check(LibraryViews.Apply(viewGames, viewState, 2).Single().AppId == 1, "Started view excludes complete, expired and unknown progress");
    Check(LibraryViews.Apply(viewGames, viewState, 3).Single().AppId == 1, "Almost complete view requires fresh remaining counts");
    string favoritePath = LibraryState.PathForAccount(scratch, 999); viewState.Save(favoritePath);
    Check(LibraryState.Load(favoritePath).Favorites.SetEquals(new uint[] { 1 }) && LibraryState.Load(LibraryState.PathForAccount(scratch, 998)).Favorites.Count == 0, "Favorites persist without crossing account boundaries");
    new DesktopPreferences { TourSeen = true, View = 2, Left = -1200, Top = 50 }.Save(prefsFile);
    var tourPrefs = DesktopPreferences.Load(prefsFile);
    Check(tourPrefs.TourSeen && tourPrefs.View == 2 && tourPrefs.Left == -1200 && tourPrefs.Top == 50, "Tour dismissal, saved view and multi-monitor position survive restart");
    Check(!DesktopPreferences.Load(Path.Combine(scratch, "first-launch.json")).TourSeen, "A first launch schedules the guided tour");
    string snapshotFile = LibrarySnapshot.PathFor(scratch, 1000); LibrarySnapshot.Save(snapshotFile, 1000, viewGames.Concat(viewGames));
    Check(LibrarySnapshot.Load(snapshotFile, 1000)?.Games.Count == 4, "Offline library snapshot survives restart and deduplicates games");
    Check(LibrarySnapshot.Load(snapshotFile, 1001) is null && LibrarySnapshot.Load(snapshotFile, 0) is null, "Offline snapshot cannot be loaded for another or unknown account");
    File.WriteAllText(snapshotFile, "broken"); Check(LibrarySnapshot.Load(snapshotFile, 1000) is null, "Corrupt offline snapshot is ignored safely");
    Check(!FailureGuidance.Retryable("Timed out; outcome unknown.") && !FailureGuidance.Retryable("Save failed or unconfirmed.") && !FailureGuidance.Retryable("Steam account changed. Refresh the library before continuing."), "Uncertain writes and changed accounts never enter the retry queue");
    Check(FailureGuidance.Retryable("Another Steam session is running.") && FailureGuidance.Retryable("Steam stats unavailable. Try launching the game once."), "Only recognized recoverable failures are eligible for manual retry");
    Check(FailureGuidance.Explain("Save failed or unconfirmed.").Contains("read progress", StringComparison.OrdinalIgnoreCase), "Uncertain result guidance requires a fresh progress read");
    Check(artworkCache.CacheInfo().Count == 1 && artworkCache.CacheInfo().Bytes > 0, "Cover cache reports actual image count and disk usage");
    File.WriteAllText(Path.Combine(scratch, "artwork", "keep.txt"), "unrelated"); artworkCache.Reset(620);
    Check(!artworkCache.HasCached(620) && File.Exists(Path.Combine(scratch, "artwork", "keep.txt")), "Per-game cover reset removes only its image");
    using var redownloaded = await artworkCache.GetAsync(620, CancellationToken.None);
    Check(redownloaded is not null && artworkCalls == 4, "Reset cover is fetched again");
    restartedMissingArt.Reset(1); Check(!restartedMissingArt.MissingCached(1), "Explicit cover reset clears remembered missing result");
    var missingAfterReset = new SteamArtwork(missingArtHttp, Path.Combine(scratch, "missing-artwork"));
    Check(!missingAfterReset.MissingCached(1), "Missing-result reset persists across restart");
    artworkCache.Reset(); Check(artworkCache.CacheInfo().Count == 0 && File.Exists(Path.Combine(scratch, "artwork", "keep.txt")), "Clear cover cache leaves unrelated settings and files intact");
    string versionFile = Path.Combine(scratch, "versioned-preferences.json");
    File.WriteAllText(versionFile, "{\"Theme\":\"Light\",\"TourSeen\":true}");
    var migrated = DesktopPreferences.Load(versionFile);
    Check(migrated.Theme == "Light" && migrated.TourSeen && migrated.RecoveryNotice is not null, "Legacy settings retain preferences during version migration");
    migrated.Save(versionFile);
    Check(JsonDocument.Parse(File.ReadAllText(versionFile)).RootElement.GetProperty("SchemaVersion").GetInt32() == 1 && File.Exists(versionFile + ".bak"), "Migrated settings gain a version and backup");
    File.WriteAllText(versionFile, "broken"); var restoredPreferences = DesktopPreferences.Load(versionFile); restoredPreferences.Save(versionFile);
    Check(DesktopPreferences.Load(versionFile).Theme == "Light" && Directory.GetFiles(scratch, "versioned-preferences.json.rejected-*").Length == 1, "Recovery preserves the rejected primary and repairs the active file");
    string newerJson = "{\"SchemaVersion\":999,\"Theme\":\"Light\"}"; File.WriteAllText(versionFile, newerJson);
    var future = DesktopPreferences.Load(versionFile); bool futureSaveBlocked = false;
    try { future.Save(versionFile); } catch (InvalidOperationException) { futureSaveBlocked = true; }
    Check(future.ReadOnly && future.RecoveryNotice is not null && futureSaveBlocked && File.ReadAllText(versionFile) == newerJson, "Future settings are warned about and never overwritten");
    SettingsFile.PreserveAndReset(versionFile); new DesktopPreferences().Save(versionFile);
    Check(!DesktopPreferences.Load(versionFile).ReadOnly && Directory.GetFiles(scratch, "versioned-preferences.json.preserved-*").Any(p => File.ReadAllText(p) == newerJson), "Explicit reset retains an exact copy of future settings");
    string futureBackup = Path.Combine(scratch, "future-backup.json"); File.WriteAllText(futureBackup + ".bak", newerJson);
    var backupProtected = DesktopPreferences.Load(futureBackup); bool backupSaveBlocked = false;
    try { backupProtected.Save(futureBackup); } catch (InvalidOperationException) { backupSaveBlocked = true; }
    Check(backupProtected.ReadOnly && backupSaveBlocked && File.ReadAllText(futureBackup + ".bak") == newerJson && !File.Exists(futureBackup), "A future-version backup is preserved even when the primary is missing");
    string futureLibrary = LibraryState.PathForAccount(scratch, 444); File.WriteAllText(futureLibrary, "{\"SchemaVersion\":999}");
    Check(LibraryState.Load(futureLibrary).ReadOnly, "Future library preferences are protected too");
    var inventory = new LibraryInventory(); inventory.Update(new[] { new SteamGame(1, "One", true), new SteamGame(2, "Two", false) }, true);
    Check(inventory.Added.Count == 0 && inventory.Missing.Count == 0, "Initial inventory establishes a baseline without fake additions");
    inventory.Update(new[] { new SteamGame(1, "One", true), new SteamGame(3, "Three", false) }, true);
    Check(inventory.Added.Single().AppId == 3 && inventory.Missing.Single().AppId == 2, "Complete inventory scan records added and missing entries");
    inventory.Update(Array.Empty<SteamGame>(), false);
    Check(inventory.Current.Count == 2 && inventory.Missing.Single().AppId == 2, "Partial or offline scan never invents missing games");
    inventory.Update(new[] { new SteamGame(2, "Two", false) }, false);
    Check(inventory.Missing.Count == 0 && inventory.Added.Count == 0, "Returning missing game is restored without a duplicate addition");
    string invFile = Path.Combine(scratch, "inventory.json"); SettingsFile.Save(invFile, inventory, false, false);
    Check(SettingsFile.Load<LibraryInventory>(invFile).Value.Current.Count == 3, "Inventory persists records across restart");
    string report = DiagnosticReport.Create("1.11.0", "Dark", 2, 3, false, true, false);
    Check(report.Contains("1.11.0") && report.Contains("Library count: 2") && !report.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase) && !report.Contains(scratch, StringComparison.OrdinalIgnoreCase), "Diagnostic report contains useful allowlisted data without local identity or paths");
    string injectedReport = DiagnosticReport.Create(new string('f', 32), "C:/Users/Private/secret", -1, int.MaxValue, false, false, false);
    Check(!injectedReport.Contains(new string('f', 32)) && !injectedReport.Contains("C:/Users/Private") && injectedReport.Contains("History count: 2000"), "Diagnostic text fields reject injected keys and paths and bound counts");
    int setCalls = 0, storeCalls = 0; bool sameAccount = true;
    bool safeWrite = SteamWriteGuard.Run(new[] { "ONE", "TWO" }, () => sameAccount, _ => { setCalls++; return true; }, () => { storeCalls++; return true; });
    Check(safeWrite && setCalls == 2 && storeCalls == 1, "Write guard commits once when the session remains unchanged");
    setCalls = storeCalls = 0; sameAccount = false;
    Check(!SteamWriteGuard.Run(new[] { "ONE" }, () => sameAccount, _ => { setCalls++; return true; }, () => { storeCalls++; return true; }) && setCalls == 0 && storeCalls == 0, "Changed account is rejected before any write");
    sameAccount = true; setCalls = storeCalls = 0;
    Check(!SteamWriteGuard.Run(new[] { "ONE", "TWO" }, () => sameAccount, _ => { setCalls++; sameAccount = false; return true; }, () => { storeCalls++; return true; }) && setCalls == 1 && storeCalls == 0, "Account change during local preparation prevents subsequent writes and StoreStats");
    sameAccount = true;
    Check(!SteamWriteGuard.Run(new[] { "ONE" }, () => sameAccount, _ => true, () => { sameAccount = false; return true; }), "Account change during confirmation cannot report success");
    Check(!SteamWriteGuard.Run(new[] { "ONE" }, () => true, _ => true, () => false), "Unconfirmed store result never reports success");
    string boundPath = OperationJournal.PathForAccount(scratch, 333);
    var boundJournal = new OperationJournal { Account = 333 }; boundJournal.Begin(new[] { new SteamGame(1, "A", true) }, true); boundJournal.Save(boundPath);
    var mismatchJournal = OperationJournal.Load(boundPath, 444); bool mismatchBlocked = false;
    try { mismatchJournal.Save(boundPath); } catch (InvalidOperationException) { mismatchBlocked = true; }
    Check(mismatchJournal.ReadOnly && mismatchJournal.Pending is null && mismatchBlocked && OperationJournal.Load(boundPath, 333).Pending is not null, "A copied journal from another account cannot load or overwrite its queue");
    foreach (string checkpoint in new[] { "before-start", "in-flight", "after-remote-confirmation" })
    {
        string crashRoot = Path.Combine(scratch, "crash-" + checkpoint); Directory.CreateDirectory(crashRoot);
        var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(psi.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) psi.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()!.Location);
        psi.ArgumentList.Add("--crash-journal-child"); psi.ArgumentList.Add(crashRoot); psi.ArgumentList.Add(checkpoint);
        using var child = System.Diagnostics.Process.Start(psi)!;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!File.Exists(Path.Combine(crashRoot, "ready.txt"))) { if (child.HasExited) throw new Exception("Crash-test child exited early."); await Task.Delay(30, deadline.Token); }
            child.Kill(true); await child.WaitForExitAsync();
            var afterKill = OperationJournal.Load(OperationJournal.PathForAccount(crashRoot, 101), 101);
            Check(afterKill.Pending?.Games.Count == 1, "Real process termination preserves pending work: " + checkpoint);
            bool interrupted = afterKill.RecoverInterrupted();
            Check(checkpoint == "before-start" ? !interrupted && afterKill.History.Count == 0 : interrupted && afterKill.History.Single().Outcome == "Uncertain", "Crash recovery does not invent success: " + checkpoint);
            if (checkpoint == "after-remote-confirmation") Check(File.Exists(Path.Combine(crashRoot, "synthetic-server-confirmed.txt")) && afterKill.Pending?.Active == 10, "Simulated remote success before local commit still requires a fresh read");
        }
        finally { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } }
    }
    var scanState = new LibraryState();
    var scanGames = new List<SteamGame> { new(1, "Complete", true), new(2, "Fresh empty", false), new(3, "Expired", false), new(4, "Unknown", false), new(5, "Excluded", false), new(4, "Duplicate", false) };
    scanState.Progress[1] = new(5, 5, DateTimeOffset.UtcNow); scanState.Progress[2] = new(0, 0, DateTimeOffset.UtcNow); scanState.Progress[3] = new(1, 3, DateTimeOffset.UtcNow.AddDays(-2)); scanState.Excluded[5] = scanGames[4];
    Check(scanState.ProgressScan(scanGames, false).Select(g => g.AppId).SequenceEqual(new uint[] { 3, 4 }), "Library scan includes unknown and expired games without requiring selections");
    Check(scanState.ProgressScan(scanGames, true).Count == 4, "Full rescan includes fresh complete and zero-achievement games, excludes hidden-by-exclusion and duplicates");
    Check(scanState.ProgressScan(new[] { new SteamGame(99, "Family", false, true) }, true).Single().AppId == 99, "Family scan stays in its own active library");
    scanState.Record(4, new List<AchievementInfo> { new("DONE", "Done", true) });
    Check(!scanState.Filter(scanGames, "", true, false).Any(g => g.AppId == 4), "Scanning completed progress immediately enables the hide-completed filter");
    using (var trusted = NativeDllPolicy.OpenVerified(Path.Combine("lib", "steam_api64.dll"))) Check(trusted.Length > 0, "Trusted Steam DLL passes pre-load identity verification");
    string alteredDll = Path.Combine(scratch, "modified-steam.dll"); var dllBytes = File.ReadAllBytes(Path.Combine("lib", "steam_api64.dll")); dllBytes[^1] ^= 1; File.WriteAllBytes(alteredDll, dllBytes);
    bool modifiedRejected = false; try { using var unexpected = NativeDllPolicy.OpenVerified(alteredDll); } catch (BadImageFormatException) { modifiedRejected = true; }
    Check(modifiedRejected, "Modified Steam DLL is rejected before any native load");
    using (var publicHandler = PublicSteamHttp.CreateHandler()) Check(!publicHandler.AllowAutoRedirect && !publicHandler.UseCookies && publicHandler.ServerCertificateCustomValidationCallback is null, "Public Steam transport blocks redirects and cookies without bypassing TLS validation");
    using (var oversized = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[1025]) })
    {
        bool limitRejected = false; try { await PublicSteamHttp.ReadBoundedAsync(oversized, 1024, CancellationToken.None); } catch (IOException) { limitRejected = true; }
        Check(limitRejected, "Oversized Content-Length is rejected before response buffering");
    }
    using (var lying = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[1025]) })
    {
        lying.Content.Headers.ContentLength = 1; bool bodyRejected = false;
        try { await PublicSteamHttp.ReadBoundedAsync(lying, 1024, CancellationToken.None); } catch (IOException) { bodyRejected = true; }
        Check(bodyRejected, "Actual streamed size is bounded even with a false Content-Length");
    }
    string nullCachePath = Path.Combine(scratch, "null-support.json"); File.WriteAllText(nullCachePath, "{\"10\":null}");
    var nullCatalog = new AchievementCatalog(artworkHttp, nullCachePath); Check(nullCatalog.GetSupport(10) == AchievementSupport.Unknown && nullCatalog.GetName(10) is null, "Null cached support entries cannot crash catalog access");
    using var excessiveHttp = new HttpClient(new FakeHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[512 * 1024 + 1]) }));
    var excessiveCatalog = new AchievementCatalog(excessiveHttp, Path.Combine(scratch, "excessive-store.json"), TimeSpan.Zero);
    await excessiveCatalog.RefreshAsync(new[] { new SteamGame(10, "Sample", false) }, null, CancellationToken.None);
    Check(excessiveCatalog.GetSupport(10) == AchievementSupport.Unknown, "Oversized Store metadata remains unknown instead of hiding the game");
    if (dpapiAvailable)
    {
        string realVaultDir = Path.Combine(scratch, "real-vault"); Directory.CreateDirectory(realVaultDir); var realStore = new CredentialStore(realVaultDir);
        realStore.Save(new string('b', 32)); byte[] sealedBytes = File.ReadAllBytes(realStore.ProtectedPath); sealedBytes[sealedBytes.Length / 2] ^= 0x20; File.WriteAllBytes(realStore.ProtectedPath, sealedBytes);
        File.WriteAllText(realStore.LegacyPath, new string('c', 32));
        Check(realStore.Read() == "" && realStore.Error is not null && File.Exists(realStore.LegacyPath), "Real DPAPI tamper rejection does not fall back to remaining plaintext");
    }
    var iconCache = new AchievementIconCache(Path.Combine(scratch, "achievement-icons"));
    byte[] iconPng; using (var iconImage = new System.Drawing.Bitmap(64, 64)) using (var iconBytes = new MemoryStream()) { iconImage.Save(iconBytes, System.Drawing.Imaging.ImageFormat.Png); iconPng = iconBytes.ToArray(); }
    iconCache.Save(10, "SAME", false, iconPng);
    Check(iconCache.Read(10, "SAME", false)?.SequenceEqual(iconPng) == true, "Achievement icon persists and is returned without another download");
    Check(new AchievementIconCache(Path.Combine(scratch, "achievement-icons")).Read(10, "SAME", false) is not null, "Achievement icons survive cache-object restart");
    Check(iconCache.Read(10, "SAME", true) is null && iconCache.Read(11, "SAME", false) is null, "Icon cache separates locked/unlocked state and game IDs");
    string traversalIcon = iconCache.PathFor(10, "../../private/file", false);
    Check(Path.GetFullPath(traversalIcon).StartsWith(Path.GetFullPath(Path.Combine(scratch, "achievement-icons")) + Path.DirectorySeparatorChar) && !Path.GetFileName(traversalIcon).Contains(".."), "Untrusted achievement names cannot escape the icon cache");
    Check(!AchievementIconCache.Valid(new byte[131073]) && !AchievementIconCache.Valid(new byte[4]), "Oversized and malformed icon data are rejected");
    Check(!AchievementIconCache.Valid(iconPng[..33]), "A truncated PNG with a valid-looking header cannot block icon refetch");
    iconCache.Save(10, "BAD", false, new byte[4]); Check(iconCache.Read(10, "BAD", false) is null, "Invalid icon bytes are never cached");
    File.WriteAllText(iconCache.PathFor(10, "SAME", false), "corrupt"); Check(iconCache.Read(10, "SAME", false) is null, "Corrupt cached icon remains eligible for refetch");
    Check(GlobalAchievements.ReadCached(620, globalPath).GetValueOrDefault("FIRST") == 25.2, "Fresh global rates are available synchronously before first paint");
    bool realArtworkSample = args.Length == 2 && args[0] == "--artwork-sample";
    if (args.SequenceEqual(new[] { "--artwork-live-test" }) || realArtworkSample)
    {
        using var liveHttp = realArtworkSample ? new HttpClient(new FakeHandler(request => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(File.ReadAllBytes(Path.Combine(args[1], request.RequestUri!.Host == "store.steampowered.com" ? "metadata.json" : "header.jpg")))
        })) : new HttpClient();
        liveHttp.DefaultRequestHeaders.UserAgent.ParseAdd("SteamAchievementUnlocker/1.5.0");
        var liveArt = new SteamArtwork(liveHttp, Path.Combine(scratch, "live-artwork"));
        using var realCover = await liveArt.GetAsync(620, CancellationToken.None);
        Check(realCover is { Width: 184, Height: 86 }, realArtworkSample ? "Real Steam metadata and downloaded cover decode correctly" : "Live Steam artwork downloads and decodes without account credentials");
    }
    bool globalSample = args.Length == 2 && args[0] == "--global-sample";
    if (globalSample) Check(GlobalAchievements.Parse(File.ReadAllText(args[1])).Count > 0, "Real public Steam global percentages parse correctly");
    if (args.Length == 2 && !realArtworkSample && !globalSample)
    {
        Check(AchievementCatalog.ParseStoreResponse(File.ReadAllText(args[0]), 10) == AchievementSupport.None, "Live Valve response: Counter-Strike has no achievements");
        Check(AchievementCatalog.ParseStoreResponse(File.ReadAllText(args[1]), 620) == AchievementSupport.Available, "Live Valve response: Portal 2 has achievements");
    }
}
finally
{
    string resolved = Path.GetFullPath(scratch);
    string allowedPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar + "sau-tests-";
    if (!resolved.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid test cleanup path.");
    Directory.Delete(resolved, recursive: true);
}
Console.WriteLine($"\n{passed} checks passed. No Steam account was initialized or modified.");

sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
}

// Test double only: checks transactional behavior, not Windows encryption strength.
sealed class FakeProtector : ISecretProtector
{
    public bool FailProtect { get; set; }
    public bool FailRead { get; set; }
    public byte[] Protect(byte[] input) => FailProtect ? throw new System.Security.Cryptography.CryptographicException() : new byte[] { 0xAA }.Concat(input.Select(b => (byte)(b ^ 0x5A))).ToArray();
    public byte[] Unprotect(byte[] input) => FailRead || input.Length == 0 || input[0] != 0xAA ? throw new System.Security.Cryptography.CryptographicException() : input.Skip(1).Select(b => (byte)(b ^ 0x5A)).ToArray();
}
