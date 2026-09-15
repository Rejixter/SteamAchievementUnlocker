using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using SteamAchievementUnlocker;

Console.Title = "Steam Achievement Unlocker";

if (args.Contains("--self-test"))
{
    bool valid = SteamRuntime.ValidateNativeLibrary(out string diagnostic);
    Console.WriteLine(diagnostic);
    Environment.ExitCode = valid ? 0 : 1;
    return;
}

Directory.SetCurrentDirectory(AppContext.BaseDirectory);
using var metadataHttp = new System.Net.Http.HttpClient();
metadataHttp.DefaultRequestHeaders.UserAgent.ParseAdd("SteamAchievementUnlocker/1.2.0");
var catalog = new AchievementCatalog(metadataHttp, Path.Combine(UserSettings.DirectoryPath, "achievement-cache.json"));
bool hideWithoutAchievements = true;
bool includeCachedLibrary = false;
List<SteamGame>? library = null;
bool scanRequested = true;

if (args.Length == 3 && (args[0] == "--bulk-worker" || args[0] == "--game-worker") && uint.TryParse(args[1], out uint workerId) && workerId > 0)
{
    using var sessionLock = new Mutex(false, @"Local\SteamAchievementUnlocker.Session");
    bool acquired;
    try { acquired = sessionLock.WaitOne(0); }
    catch (AbandonedMutexException) { acquired = true; }
    if (!acquired) { Console.WriteLine("Another achievement session is running."); Environment.ExitCode = 4; return; }
    try
    {
        if (args[0] == "--bulk-worker") Environment.ExitCode = RunBulkGame(workerId);
        else RunAchievementSession(workerId, args[2]);
    }
    finally { sessionLock.ReleaseMutex(); }
    return;
}
if (args.Length > 0) { Console.WriteLine("Invalid arguments."); Environment.ExitCode = 8; return; }
await RunMainMenu();

async Task RunMainMenu()
{
    while (true)
    {
        Console.Clear();
        Console.WriteLine("=== Steam Achievement Unlocker v1.2.0 ===\n");
        library ??= await GetGamesListAsync();
        if (scanRequested && hideWithoutAchievements && library.Count > 0)
        {
            Console.WriteLine("Checking achievement support (cached for 7 days). Press Esc to skip.");
            Console.WriteLine("First scan can take a few minutes. Each pass is limited to 90 seconds; 'r' continues it.");
            using var scanTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var progress = new InlineProgress(p =>
            {
                if (p.Checked % 10 == 0 || p.Checked == p.Total)
                    Console.WriteLine($"  Checked {p.Checked}/{p.Total}");
            });
            Task scan = catalog.RefreshAsync(library, progress, scanTimeout.Token);
            while (!scan.IsCompleted)
            {
                if (!Console.IsInputRedirected && Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape)
                    scanTimeout.Cancel();
                await Task.WhenAny(scan, Task.Delay(100));
            }
            await scan;
            scanRequested = false;
        }
        var games = catalog.Filter(library, hideWithoutAchievements)
            .Select(g => g.FromCache ? g with { Name = catalog.GetName(g.AppId) ?? g.Name } : g)
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
        int unknown = library.Count(g => catalog.GetSupport(g.AppId) == AchievementSupport.Unknown);
        Console.WriteLine($"\nShowing {games.Count}/{library.Count} games. {library.Count - games.Count} without achievements hidden.");
        Console.WriteLine($"Filter: {(hideWithoutAchievements ? "ON" : "OFF")}. Unknown: {unknown} (kept visible).\n");

        if (games.Count == 0)
        {
            Console.WriteLine(library.Count > 0 ? "No games match the filter. Press 'f' to show all games." : "No games found (Steam may not be installed, or the scan failed).");
            Console.WriteLine("You can still type an AppID manually below.\n");
        }
        else
        {
            for (int i = 0; i < games.Count; i++)
            {
                string tag = games[i].IsInstalled ? " [installed]" : "";
                if (games[i].FromCache) tag += " [cached: access unverified]";
                string support = catalog.GetSupport(games[i].AppId) == AchievementSupport.Unknown ? " [?]" : "";
                Console.WriteLine($"{i + 1,3}) {games[i].Name}  (AppID: {games[i].AppId}){tag}{support}");
            }
        }

        Console.WriteLine("\n  0) Enter an AppID manually");
        Console.WriteLine("  f) Toggle achievement filter    r) Refresh library / continue scan");
        Console.WriteLine("  c) Clear achievement cache      k) Add/remove your own API key (optional)");
        Console.WriteLine($"  s) Include cached library / family candidates: {(includeCachedLibrary ? "ON" : "OFF")}");
        Console.WriteLine("  all) Unlock achievements in ALL listed games (confirmation required)");
        Console.WriteLine("  q) Quit");
        Console.Write("\nChoice: ");

        string? choice = Console.ReadLine()?.Trim();

        if (choice is null || string.Equals(choice, "q", StringComparison.OrdinalIgnoreCase))
            return;
        switch (choice.ToLowerInvariant())
        {
            case "s": includeCachedLibrary = !includeCachedLibrary; library = null; scanRequested = true; continue;
            case "all": await RunBatch(games); catalog = new AchievementCatalog(metadataHttp, Path.Combine(UserSettings.DirectoryPath, "achievement-cache.json")); Pause(); continue;
            case "f": hideWithoutAchievements = !hideWithoutAchievements; continue;
            case "r": library = null; scanRequested = true; continue;
            case "c": catalog.Clear(); scanRequested = true; continue;
            case "k": UserSettings.ConfigureApiKey(); library = null; scanRequested = true; Pause(); continue;
        }

        uint appId;
        string gameName;

        if (choice == "0")
        {
            Console.Write("AppID (numbers only, e.g. 2990): ");
            if (!uint.TryParse(Console.ReadLine(), out appId) || appId == 0)
            {
                Console.WriteLine("Invalid AppID.");
                Pause();
                continue;
            }
            gameName = $"AppID {appId}";
        }
        else if (int.TryParse(choice, out int index) && index >= 1 && index <= games.Count)
        {
            appId = games[index - 1].AppId;
            gameName = games[index - 1].Name;
        }
        else
        {
            Console.WriteLine("Invalid choice.");
            Pause();
            continue;
        }

        await BatchRunner.StartWorkerAsync(new SteamGame(appId, gameName, false), false);
        catalog = new AchievementCatalog(metadataHttp, Path.Combine(UserSettings.DirectoryPath, "achievement-cache.json"));
    }
}

async Task<List<SteamGame>> GetGamesListAsync()
{
    var installed = SteamLibraryScanner.GetInstalledGames();
    if (includeCachedLibrary)
    {
        var cached = SteamLibraryScanner.GetCachedLibraryGames();
        var known = installed.Select(g => g.AppId).ToHashSet();
        installed.AddRange(cached.Where(g => known.Add(g.AppId)));
        Console.WriteLine("Cached candidates can include family games and stale entries. Steam access is checked per game.");
    }

    string apiKey = UserSettings.ReadApiKey();
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        Console.WriteLine("Local-library mode: no API key required.");
        Console.WriteLine("Press 'k' to optionally add your own key for the full owned library.\n");
        return installed;
    }

    string? steamPath = SteamLibraryScanner.GetSteamInstallPath();
    ulong? steamId64 = steamPath is null ? null : SteamLibraryScanner.GetCurrentUserSteamId64(steamPath);

    if (steamId64 is null)
    {
        Console.WriteLine("Couldn't detect your SteamID64 automatically — showing installed games only.\n");
        return installed;
    }

    var owned = await SteamLibraryScanner.TryGetOwnedGamesAsync(apiKey, steamId64.Value);
    if (owned is null)
    {
        Console.WriteLine("Couldn't reach the Steam Web API (check your key / internet connection) — showing installed games only.\n");
        return installed;
    }

    var installedIds = installed.Where(g => g.IsInstalled).Select(g => g.AppId).ToHashSet();
    var merged = owned.Select(g => g with { IsInstalled = installedIds.Contains(g.AppId) }).ToList();

    var mergedIds = merged.Select(g => g.AppId).ToHashSet();
    merged.AddRange(installed.Where(g => !mergedIds.Contains(g.AppId)));

    return merged.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
}

void RunAchievementSession(uint appId, string gameName)
{
    Console.WriteLine($"\nConnecting to Steam as AppID {appId} ('{gameName}')...");

    if (!SteamRuntime.ValidateNativeLibrary(out string diagnostic))
    {
        Console.WriteLine(diagnostic);
        Pause();
        return;
    }
    using var steam = new SteamStatsManager(appId);

    if (!steam.Init())
    {
        Console.WriteLine("\nConnection failed. Checklist:");
        Console.WriteLine(" - Is Steam running and are you logged in?");
        Console.WriteLine(" - Does this account have access to this game (owned or family shared)?");
        Console.WriteLine(" - Is lib\\steam_api64.dll present (see README)?");
        Pause();
        return; // no session was established, safe to just return to the menu
    }

    Console.WriteLine("Connected. Loading stats (you should now show as 'in-game' on Steam)...");

    var waitTimeout = TimeSpan.FromSeconds(20);
    var waitStopwatch = Stopwatch.StartNew();

    while (!steam.StatsLoaded)
    {
        steam.RunCallbacks();

        if (steam.StatsFailed || waitStopwatch.Elapsed > waitTimeout)
        {
            Console.WriteLine("\nTimed out waiting for Steam to send stats for this game (20s).");
            Console.WriteLine("Possible causes:");
            Console.WriteLine(" - This account doesn't actually own/hasn't launched this AppID before");
            Console.WriteLine(" - Steam is temporarily slow to respond — worth trying again");
            Console.WriteLine(" - A leftover steam_appid.txt from a previous run is confusing the client");
            steam.Dispose();
            Pause();
            return;
        }

        Thread.Sleep(100);
    }

    while (true)
    {
        var achievements = steam.ListAchievements().ToList();
        catalog.RecordFromClient(appId, achievements.Count > 0);

        Console.Clear();
        Console.WriteLine($"=== {gameName} — {achievements.Count} achievement(s) ===\n");

        if (achievements.Count == 0)
        {
            Console.WriteLine("This game doesn't seem to have any Steam achievements (or the schema hasn't loaded yet).");
        }
        else
        {
            for (int i = 0; i < achievements.Count; i++)
            {
                var a = achievements[i];
                Console.WriteLine($"{i + 1,3}) [{(a.Unlocked ? "x" : " ")}] {a.ApiName,-30} {a.DisplayName}");
            }
        }

        Console.WriteLine("\nType a number   -> unlock just that achievement");
        Console.WriteLine("Type 'all'      -> unlock ALL of them");
        Console.WriteLine("Type 'back'     -> return to the game list");
        Console.Write("\n> ");

        string? input = Console.ReadLine();
        steam.RunCallbacks();

        if (input is null || string.Equals(input.Trim(), "back", StringComparison.OrdinalIgnoreCase))
        {
            return; // The worker exits; the parent keeps the library menu.

        }

        if (string.Equals(input, "all", StringComparison.OrdinalIgnoreCase))
        {
            if (achievements.Count == 0)
            {
                Pause();
                continue;
            }

            Console.Write($"Unlock ALL {achievements.Count} achievements? (y/n): ");
            if (string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
            {
                bool ok = steam.UnlockAll(achievements.Select(a => a.ApiName));
                Console.WriteLine(ok ? "\nAll changes confirmed by Steam." : "\nSome failed — see messages above.");
            }
            Pause();
            continue;
        }

        if (int.TryParse(input, out int num) && num >= 1 && num <= achievements.Count)
        {
            string apiName = achievements[num - 1].ApiName;
            bool ok = steam.Unlock(apiName);
            Console.WriteLine(ok ? $"\n'{apiName}' unlocked." : "\nFailed — see message above.");
            Pause();
            continue;
        }

        Console.WriteLine("Not recognized.");
        Pause();
    }
}

int RunBulkGame(uint appId)
{
    if (!SteamRuntime.ValidateNativeLibrary(out string diagnostic)) { Console.WriteLine(diagnostic); return 4; }
    using var steam = new SteamStatsManager(appId);
    if (!steam.Init()) return 4;
    var timer = Stopwatch.StartNew();
    while (!steam.StatsLoaded && !steam.StatsFailed && timer.Elapsed < TimeSpan.FromSeconds(20))
    { steam.RunCallbacks(); Thread.Sleep(100); }
    if (!steam.StatsLoaded) return 5;
    var achievements = steam.ListAchievements().ToList();
    catalog.RecordFromClient(appId, achievements.Count > 0);
    if (achievements.Count == 0) return 3;
    var locked = achievements.Where(a => !a.Unlocked).ToList();
    if (locked.Count == 0) return 2;
    return steam.UnlockAll(locked.Select(a => a.ApiName)) ? 0 : 6;
}

async Task RunBatch(List<SteamGame> games)
{
    if (games.Count == 0) { Console.WriteLine("No games to process."); return; }
    Console.WriteLine($"\nThis will unlock every locked achievement in the {games.Count} games listed above.");
    Console.WriteLine("Cached entries may be unavailable. Failed games will be reported and skipped.");
    Console.Write("Type UNLOCK ALL to start, or anything else to cancel: ");
    if (Console.ReadLine()?.Trim() != "UNLOCK ALL") return;
    Console.WriteLine("Press Q to stop after the current game. Completed changes stay saved.");
    int completed = 0;
    var results = await BatchRunner.RunAsync(games, game =>
    {
        Console.WriteLine($"\n[{++completed}/{games.Count}] {game.Name} ({game.AppId})");
        return BatchRunner.StartWorkerAsync(game, true);
    }, () =>
    {
        while (!Console.IsInputRedirected && Console.KeyAvailable)
            if (Console.ReadKey(true).Key == ConsoleKey.Q) return true;
        return false;
    }, result => Console.WriteLine($"  {result.Status}"));
    Console.WriteLine($"\nProcessed {results.Count}/{games.Count} games.");
    foreach (var group in results.GroupBy(r => r.Status)) Console.WriteLine($"  {group.Key}: {group.Count()}");
    try { Console.WriteLine($"Report: {BatchRunner.SaveReport(results)}"); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    { Console.WriteLine("Could not save the report; results are shown above."); }
}

void Pause()
{
    Console.WriteLine("\nPress any key to continue...");
    if (!Console.IsInputRedirected) Console.ReadKey(true);
}

sealed class InlineProgress(Action<FilterProgress> report) : IProgress<FilterProgress>
{
    public void Report(FilterProgress value) => report(value);
}
