using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SteamAchievementUnlocker;

public record AchievementInfo(string ApiName, string DisplayName, bool Unlocked, string Description = "", byte[]? IconPng = null);
public record GameProgress(int Unlocked, int Total, DateTimeOffset CheckedAt)
{
    public bool IsValid => Total >= 0 && Unlocked >= 0 && Unlocked <= Total;
    public bool IsFresh => IsValid && DateTimeOffset.UtcNow - CheckedAt is var age && age >= TimeSpan.Zero && age < TimeSpan.FromHours(24);
    public bool Complete => IsFresh && Total > 0 && Unlocked == Total;
    public string Label => !IsFresh ? "Not checked" : Total == 0 ? "No achievements" : $"{Unlocked}/{Total} ({(int)(100L * Unlocked / Total)}%)";
}

public sealed class LibraryState
{
    public int SchemaVersion { get; set; } = SettingsFile.CurrentVersion;
    [System.Text.Json.Serialization.JsonIgnore] public string? RecoveryNotice { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool ReadOnly { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool PreserveOriginal { get; set; }
    public Dictionary<uint, SteamGame> Excluded { get; set; } = new();
    public Dictionary<uint, GameProgress> Progress { get; set; } = new();
    public HashSet<uint> Favorites { get; set; } = new();

    public static string PathForAccount(string directory, ulong account) => Path.Combine(directory,
        "library-state-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account.ToString())))[..24] + ".json");

    public static LibraryState Load(string path)
    {
        try
        {
            var loaded = SettingsFile.Load<LibraryState>(path); var result = loaded.Value;
            result.SchemaVersion = SettingsFile.CurrentVersion; result.RecoveryNotice = loaded.Notice; result.ReadOnly = loaded.ReadOnly; result.PreserveOriginal = loaded.PreserveOriginal;
            result.Excluded = (result.Excluded ?? new()).Where(p => p.Key > 0 && p.Value is not null && p.Value.AppId == p.Key).ToDictionary();
            result.Progress = (result.Progress ?? new()).Where(p => p.Key > 0 && p.Value is not null && p.Value.IsValid).ToDictionary();
            result.Favorites = (result.Favorites ?? new()).Where(id => id > 0).ToHashSet();
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Save(string path)
    {
        SettingsFile.Save(path, this, ReadOnly, PreserveOriginal); PreserveOriginal = false;
    }

    public void Record(uint appId, IReadOnlyList<AchievementInfo> achievements) =>
        Progress[appId] = new(achievements.Count(a => a.Unlocked), achievements.Count, DateTimeOffset.UtcNow);

    public List<SteamGame> Filter(IEnumerable<SteamGame> source, string search, bool hideComplete, bool excludedView)
    {
        search = search.Trim();
        return source.Where(g => g.AppId > 0 && (excludedView || !Excluded.ContainsKey(g.AppId)))
            .Where(g => search.Length == 0 || g.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || g.AppId.ToString().Contains(search))
            .Where(g => excludedView || !hideComplete || !Progress.TryGetValue(g.AppId, out var p) || !p.Complete)
            .DistinctBy(g => g.AppId).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Always intersect with the visible list: hidden selections must never be submitted.
    public static List<SteamGame> SelectedVisible(IEnumerable<SteamGame> visible, ISet<uint> checkedIds) =>
        visible.Where(g => checkedIds.Contains(g.AppId)).DistinctBy(g => g.AppId).ToList();

    // Scan the active library itself, independently of filters, search and checkboxes.
    public List<SteamGame> ProgressScan(IEnumerable<SteamGame> library, bool refreshAll) => library
        .Where(g => g.AppId > 0 && !Excluded.ContainsKey(g.AppId))
        .Where(g => refreshAll || !Progress.TryGetValue(g.AppId, out var p) || !p.IsFresh)
        .DistinctBy(g => g.AppId).ToList();
}
