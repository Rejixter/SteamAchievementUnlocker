using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SteamAchievementUnlocker;

public record SteamGame(uint AppId, string Name, bool IsInstalled, bool FromCache = false);

/// <summary>
/// Reads Steam's own local files to find installed games and the currently
/// logged-in user's SteamID64, and optionally calls the Steam Web API to get
/// a user's FULL owned-games list (including games that aren't installed).
///
/// The Web API call is entirely optional: without an API key configured, the
/// app just falls back to the locally-installed game list, which needs no
/// key, no extra login step, and no internet access.
/// </summary>
public static class SteamLibraryScanner
{
    // These are candidates, never proof of ownership or current Family access.
    // No login cookies, credentials, or other users' private config are read.
    public static List<SteamGame> GetCachedLibraryGames(string? steamPath = null)
    {
        steamPath ??= GetSteamInstallPath();
        if (steamPath is null) return new();
        var ids = new HashSet<uint>();
        ReadDirectory(Path.Combine(steamPath, "appcache", "librarycache"), true);
        ulong? user = GetCurrentUserSteamId64(steamPath);
        if (user is >= 76561197960265728UL)
            ReadDirectory(Path.Combine(steamPath, "userdata", (user.Value - 76561197960265728UL).ToString(), "config", "librarycache"), false);
        return ids.Order().Select(id => new SteamGame(id, $"AppID {id}", false, true)).ToList();

        void ReadDirectory(string path, bool shared)
        {
            try
            {
                if (!Directory.Exists(path)) return;
                foreach (string entry in Directory.EnumerateFileSystemEntries(path))
                {
                    string name = Path.GetFileName(entry);
                    if (shared)
                    {
                        // Current clients use numeric directories; older clients use appid_asset.jpg.
                        if (!Directory.Exists(entry))
                        {
                            int separator = name.IndexOf('_');
                            if (separator < 1) continue;
                            name = name[..separator];
                        }
                    }
                    else
                    {
                        if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                        name = Path.GetFileNameWithoutExtension(name);
                    }
                    if (uint.TryParse(name, out uint id) && id > 0) ids.Add(id);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public static List<SteamGame> GetInstalledGames()
    {
        var games = new List<SteamGame>();

        string? steamPath = GetSteamInstallPath();
        if (steamPath is null || !Directory.Exists(steamPath))
            return games;

        foreach (string libraryFolder in GetLibraryFolders(steamPath))
        {
            string steamAppsDir = Path.Combine(libraryFolder, "steamapps");
            if (!Directory.Exists(steamAppsDir))
                continue;

            foreach (string manifestFile in Directory.EnumerateFiles(steamAppsDir, "appmanifest_*.acf"))
            {
                var game = TryParseManifest(manifestFile);
                if (game is not null)
                    games.Add(game);
            }
        }

        return games
            .GroupBy(g => g.AppId)
            .Select(g => g.First())
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Calls IPlayerService/GetOwnedGames for the given account. Returns null
    /// on any failure (bad key, offline, private profile without a matching
    /// key, etc.) so the caller can fall back to the installed-only list.
    /// </summary>
    public static async Task<List<SteamGame>?> TryGetOwnedGamesAsync(string apiKey, ulong steamId64)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            string url = "https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/" +
                         $"?key={Uri.EscapeDataString(apiKey)}&steamid={steamId64}&format=json" +
                         "&include_appinfo=1&include_played_free_games=1";

            string json = await http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("response", out var response) ||
                !response.TryGetProperty("games", out var gamesArray))
            {
                return null;
            }

            var result = new List<SteamGame>();
            foreach (var g in gamesArray.EnumerateArray())
            {
                uint appId = (uint)g.GetProperty("appid").GetInt64();
                string name = g.TryGetProperty("name", out var n) ? (n.GetString() ?? $"AppID {appId}") : $"AppID {appId}";
                result.Add(new SteamGame(appId, name, IsInstalled: false));
            }

            return result;
        }
        catch
        {
            return null;
        }
    }

    public static string? GetSteamInstallPath()
    {
        try
        {
            object? path =
                Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null)
                ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null)
                ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath", null);

            if (path is string s && !string.IsNullOrWhiteSpace(s))
                return s.Replace('/', '\\');
        }
        catch
        {
            // fall through to the default path below
        }

        const string fallback = @"C:\Program Files (x86)\Steam";
        return Directory.Exists(fallback) ? fallback : null;
    }

    /// <summary>
    /// Reads config/loginusers.vdf to find the SteamID64 of whichever account
    /// most recently logged into this Steam client — this is what lets the
    /// app fetch your full library without you having to type your SteamID.
    /// </summary>
    public static ulong? GetCurrentUserSteamId64(string steamPath)
    {
        string path = Path.Combine(steamPath, "config", "loginusers.vdf");
        if (!File.Exists(path))
            return null;

        string content;
        try
        {
            content = File.ReadAllText(path);
        }
        catch
        {
            return null;
        }

        foreach (Match block in Regex.Matches(content, "\"(\\d{17})\"\\s*\\{([^{}]*)\\}"))
        {
            if (Regex.IsMatch(block.Groups[2].Value, "\"MostRecent\"\\s*\"1\""))
                return ulong.Parse(block.Groups[1].Value);
        }

        // Older Steam versions don't always set MostRecent — fall back to the first entry found.
        var first = Regex.Match(content, "\"(\\d{17})\"");
        return first.Success ? ulong.Parse(first.Groups[1].Value) : null;
    }

    private static IEnumerable<string> GetLibraryFolders(string steamPath)
    {
        yield return steamPath;

        string vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdfPath))
            yield break;

        string content;
        try
        {
            content = File.ReadAllText(vdfPath);
        }
        catch
        {
            yield break;
        }

        foreach (Match m in Regex.Matches(content, "\"path\"\\s*\"([^\"]+)\""))
            yield return m.Groups[1].Value.Replace("\\\\", "\\");
    }

    private static SteamGame? TryParseManifest(string manifestPath)
    {
        try
        {
            string content = File.ReadAllText(manifestPath);

            var appIdMatch = Regex.Match(content, "\"appid\"\\s*\"(\\d+)\"", RegexOptions.IgnoreCase);
            var nameMatch = Regex.Match(content, "\"name\"\\s*\"([^\"]+)\"");

            if (!appIdMatch.Success || !nameMatch.Success)
                return null;

            return new SteamGame(uint.Parse(appIdMatch.Groups[1].Value), nameMatch.Groups[1].Value, IsInstalled: true);
        }
        catch
        {
            return null;
        }
    }
}
