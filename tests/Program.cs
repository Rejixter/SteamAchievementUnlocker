using System.Net;
using System.Text.Json;
using SteamAchievementUnlocker;

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

    if (args.Length == 2)
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
