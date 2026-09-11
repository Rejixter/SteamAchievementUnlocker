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
