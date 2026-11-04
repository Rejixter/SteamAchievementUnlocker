namespace SteamAchievementUnlocker;

public sealed class LibraryInventory
{
    public int SchemaVersion { get; set; } = SettingsFile.CurrentVersion;
    public List<SteamGame> Current { get; set; } = new();
    public List<SteamGame> Missing { get; set; } = new();
    public List<SteamGame> Added { get; set; } = new();
    public bool Initialized { get; set; }
    public void Update(IEnumerable<SteamGame> games, bool complete)
    {
        Current = (Current ?? new()).Where(g => g is not null && g.AppId > 0).DistinctBy(g => g.AppId).Take(30000).ToList();
        Missing = (Missing ?? new()).Where(g => g is not null && g.AppId > 0).DistinctBy(g => g.AppId).Take(30000).ToList();
        var next = games.Where(g => g is not null && g.AppId > 0).DistinctBy(g => g.AppId).Take(30000).ToList(); var ids = next.Select(g => g.AppId).ToHashSet();
        var known = Current.Concat(Missing).Select(g => g.AppId).ToHashSet();
        Added = Initialized ? next.Where(g => !known.Contains(g.AppId)).ToList() : new();
        if (complete) Missing = Missing.Concat(Current.Where(g => !ids.Contains(g.AppId))).Where(g => !ids.Contains(g.AppId)).DistinctBy(g => g.AppId).ToList();
        else Missing.RemoveAll(g => ids.Contains(g.AppId));
        Current = complete ? next : next.Concat(Current.Where(g => !ids.Contains(g.AppId))).ToList(); Initialized = true;
    }
}
