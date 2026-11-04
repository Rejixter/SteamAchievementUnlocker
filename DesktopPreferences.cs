using System.Text.Json;

namespace SteamAchievementUnlocker;

public sealed class DesktopPreferences
{
    public int SchemaVersion { get; set; } = SettingsFile.CurrentVersion;
    [System.Text.Json.Serialization.JsonIgnore] public string? RecoveryNotice { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool ReadOnly { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool PreserveOriginal { get; set; }
    public string Theme { get; set; } = "Dark";
    public string Search { get; set; } = "";
    public int Filter { get; set; }
    public int Sort { get; set; }
    public bool HideComplete { get; set; }
    public bool HideWithout { get; set; } = true;
    public bool ShowDetails { get; set; } = true;
    public bool ShowCovers { get; set; } = true;
    public bool Animations { get; set; } = true;
    public int Width { get; set; } = 1200;
    public int Height { get; set; } = 880;
    public bool Maximized { get; set; }
    public bool TourSeen { get; set; }
    public int View { get; set; }
    public int? Left { get; set; }
    public int? Top { get; set; }
    public float[] ColumnWeights { get; set; } = Array.Empty<float>();

    public static DesktopPreferences Load(string path)
    {
        try
        {
            var loaded = SettingsFile.Load<DesktopPreferences>(path); var p = loaded.Value;
            p.SchemaVersion = SettingsFile.CurrentVersion; p.RecoveryNotice = loaded.Notice; p.ReadOnly = loaded.ReadOnly; p.PreserveOriginal = loaded.PreserveOriginal;
            p.Theme = p.Theme is "Dark" or "Light" or "System" ? p.Theme : "Dark";
            p.Search = (p.Search ?? "")[..Math.Min(p.Search?.Length ?? 0, 200)];
            p.Filter = Math.Clamp(p.Filter, 0, 3); p.Sort = Math.Clamp(p.Sort, 0, 4);
            p.View = Math.Clamp(p.View, 0, 3);
            p.Width = Math.Clamp(p.Width, 780, 3840); p.Height = Math.Clamp(p.Height, 560, 2160);
            p.ColumnWeights = p.ColumnWeights is { Length: 6 or 7 or 8 } && p.ColumnWeights.All(w => float.IsFinite(w) && w >= 1 && w <= 1000) ? p.ColumnWeights : Array.Empty<float>();
            return p;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public void Save(string path)
    {
        SettingsFile.Save(path, this, ReadOnly, PreserveOriginal); PreserveOriginal = false;
    }
    public static List<SteamGame> Arrange(IEnumerable<SteamGame> games, LibraryState state, int filter, int sort)
    {
        GameProgress? Fresh(SteamGame g) => state.Progress.TryGetValue(g.AppId, out var p) && p.IsFresh ? p : null;
        var selected = games.Where(g => filter switch
        {
            1 => Fresh(g) is null,
            2 => Fresh(g) is { Total: > 0 } p && p.Unlocked < p.Total,
            3 => Fresh(g)?.Complete == true,
            _ => true
        });
        var ordered = sort switch
        {
            1 => selected.OrderByDescending(g => g.Name, StringComparer.OrdinalIgnoreCase),
            2 => selected.OrderBy(g => Fresh(g) is { Total: > 0 } p ? (double)p.Unlocked / p.Total : double.MaxValue),
            3 => selected.OrderByDescending(g => Fresh(g) is { Total: > 0 } p ? (double)p.Unlocked / p.Total : -1),
            4 => selected.OrderByDescending(g => Fresh(g) is { } p ? p.Total - p.Unlocked : -1),
            _ => selected.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
        };
        return ordered.ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.AppId).ToList();
    }
}

// Retry only attempted failures from the last batch, never skipped or hidden games.
public sealed class RetryQueue
{
    public ulong Account { get; private set; }
    public bool Unlock { get; private set; }
    private readonly HashSet<uint> _failed = new();
    public void Begin(ulong account, bool unlock) { Account = account; Unlock = unlock; _failed.Clear(); }
    public void Record(uint id, bool success) { if (success) _failed.Remove(id); else _failed.Add(id); }
    public List<SteamGame> Visible(ulong account, IEnumerable<SteamGame> visible, LibraryState state) => account != Account ? new() :
        visible.Where(g => _failed.Contains(g.AppId) && !state.Excluded.ContainsKey(g.AppId)).DistinctBy(g => g.AppId).ToList();
}
