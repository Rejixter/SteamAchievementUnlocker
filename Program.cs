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
metadataHttp.DefaultRequestHeaders.UserAgent.ParseAdd("SteamAchievementUnlocker/1.1");
var catalog = new AchievementCatalog(metadataHttp, Path.Combine(UserSettings.DirectoryPath, "achievement-cache.json"));
bool hideWithoutAchievements = true;
List<SteamGame>? library = null;
bool scanRequested = true;

await RunMainMenu();

async Task RunMainMenu()
{
    while (true)
    {
        Console.Clear();
        Console.WriteLine("=== Steam Achievement Unlocker ===\n");
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
        var games = catalog.Filter(library, hideWithoutAchievements);
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
                string support = catalog.GetSupport(games[i].AppId) == AchievementSupport.Unknown ? " [?]" : "";
                Console.WriteLine($"{i + 1,3}) {games[i].Name}  (AppID: {games[i].AppId}){tag}{support}");
            }
        }

        Console.WriteLine("\n  0) Enter an AppID manually");
        Console.WriteLine("  f) Toggle achievement filter    r) Refresh library / continue scan");
        Console.WriteLine("  c) Clear achievement cache      k) Add/remove your own API key (optional)");
        Console.WriteLine("  q) Quit");
        Console.Write("\nChoice: ");

        string? choice = Console.ReadLine()?.Trim();

        if (choice is null || string.Equals(choice, "q", StringComparison.OrdinalIgnoreCase))
            return;
        switch (choice.ToLowerInvariant())
        {
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

        RunAchievementSession(appId, gameName);
    }
}

async Task<List<SteamGame>> GetGamesListAsync()
{
    var installed = SteamLibraryScanner.GetInstalledGames();

    string apiKey = UserSettings.ReadApiKey();
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        Console.WriteLine("Installed-games mode: no API key required.");
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

    var installedIds = installed.Select(g => g.AppId).ToHashSet();
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
        Console.WriteLine(" - Does this account own this game?");
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

        if (waitStopwatch.Elapsed > waitTimeout)
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

        if (string.Equals(input, "back", StringComparison.OrdinalIgnoreCase))
        {
            // Steam's client keeps treating this process as "in-game" for this
            // AppID until the process actually exits — SteamAPI.Shutdown()
            // alone isn't reliably enough to switch to a different AppID in
            // the same run. So instead of looping back in-process, we cleanly
            // shut down and relaunch the whole app as a new process, landing
            // back on the game list with a clean slate.
            steam.Dispose();
            RestartApp();
            return; // unreachable — RestartApp() ends the process
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
                Console.WriteLine(ok ? "\nAll sent." : "\nSome failed — see messages above.");
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

void RestartApp()
{
    string? exePath = Environment.ProcessPath;
    if (exePath is not null)
        Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });

    Environment.Exit(0);
}

void Pause()
{
    Console.WriteLine("\nPress any key to continue...");
    Console.ReadKey(true);
}

sealed class InlineProgress(Action<FilterProgress> report) : IProgress<FilterProgress>
{
    public void Report(FilterProgress value) => report(value);
}
