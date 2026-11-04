using System.Text.Json;

namespace SteamAchievementUnlocker;

// Local account-scoped fallback, never evidence of ownership or permission to write stats.
public sealed class LibrarySnapshot
{
    public ulong Account { get; set; }
    public DateTimeOffset SavedAt { get; set; }
    public List<SteamGame> Games { get; set; } = new();
    public static string PathFor(string directory, ulong account) => LibraryState.PathForAccount(directory, account).Replace("library-state-", "library-snapshot-");
    public static LibrarySnapshot? Load(string path, ulong account)
    {
        if (account == 0) return null;
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 8 * 1024 * 1024) return null;
            var data = JsonSerializer.Deserialize<LibrarySnapshot>(File.ReadAllText(path));
            if (data is null || data.Account != account || data.Games is null || data.SavedAt > DateTimeOffset.UtcNow.AddMinutes(5)) return null;
            data.Games = data.Games.Where(g => g is not null && g.AppId > 0 && g.Name is { Length: > 0 and <= 1000 }).DistinctBy(g => g.AppId).Take(30000).ToList();
            return data;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    public static void Save(string path, ulong account, IEnumerable<SteamGame> games)
    {
        if (account == 0) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new LibrarySnapshot { Account = account, SavedAt = DateTimeOffset.UtcNow, Games = games.DistinctBy(g => g.AppId).ToList() }));
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public static class LibraryViews
{
    public static IEnumerable<SteamGame> Apply(IEnumerable<SteamGame> games, LibraryState state, int view) => games.Where(g => view switch
    {
        1 => state.Favorites.Contains(g.AppId),
        2 => state.Progress.TryGetValue(g.AppId, out var p) && p.IsFresh && p.Unlocked > 0 && p.Unlocked < p.Total,
        3 => state.Progress.TryGetValue(g.AppId, out var p) && p.IsFresh && p.Total > p.Unlocked && p.Total - p.Unlocked <= 5,
        _ => true
    });
}

public static class FailureGuidance
{
    public static bool Retryable(string status) => status.StartsWith("Another Steam session", StringComparison.Ordinal)
        || status.StartsWith("Steam stats unavailable", StringComparison.Ordinal)
        || status.StartsWith("No Steam access", StringComparison.Ordinal);
    public static string Explain(string status) => status.StartsWith("Steam account changed", StringComparison.Ordinal) ? "Account changed: refresh the library. This request will not be retried."
        : status.Contains("unconfirmed", StringComparison.OrdinalIgnoreCase) || status.Contains("unknown", StringComparison.OrdinalIgnoreCase) ? "Uncertain result: read progress before deciding whether to retry. No automatic retry."
        : status.StartsWith("Native Steam DLL", StringComparison.Ordinal) ? "Installation problem: extract the complete ZIP including the lib folder."
        : status.StartsWith("Another Steam session", StringComparison.Ordinal) ? "Steam is busy: wait for the other operation, then retry."
        : status.StartsWith("Steam stats unavailable", StringComparison.Ordinal) ? "Stats unavailable: launch the game once, close it, then retry."
        : status.StartsWith("No Steam access", StringComparison.Ordinal) ? "Steam access unavailable: sign in, check access to this game and refresh the library."
        : status;
}
