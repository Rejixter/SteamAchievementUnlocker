using System.Text.Json;

namespace SteamAchievementUnlocker;

public record OperationEntry(DateTimeOffset Time, uint AppId, string Game, string Operation, string Outcome, string Detail);
public sealed class PendingBatch
{
    public bool Unlock { get; set; }
    public List<SteamGame> Games { get; set; } = new();
    public uint? Active { get; set; }
    public bool InterruptionRecorded { get; set; }
    public bool RecheckAll { get; set; }
}
public sealed class OperationJournal
{
    public int SchemaVersion { get; set; } = SettingsFile.CurrentVersion;
    public ulong Account { get; set; }
    public List<OperationEntry> History { get; set; } = new();
    public PendingBatch? Pending { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string? RecoveryNotice { get; private set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool ReadOnly { get; private set; }
    [System.Text.Json.Serialization.JsonIgnore] private bool PreserveBackup { get; set; }
    public static string PathForAccount(string directory, ulong account) => LibraryState.PathForAccount(directory, account).Replace("library-state-", "operations-");
    public static OperationJournal Load(string path, ulong account = 0)
    {
        var loaded = SettingsFile.Load<OperationJournal>(path); var journal = loaded.Value;
        journal.RecoveryNotice = loaded.Notice; journal.ReadOnly = loaded.ReadOnly; journal.PreserveBackup = loaded.PreserveOriginal;
        if (account != 0 && journal.Account != 0 && journal.Account != account)
            return new() { Account = account, ReadOnly = true, RecoveryNotice = "Operation history belongs to a different account. No history or pending batch was loaded; the file was not changed." };
        if (account != 0) journal.Account = account;
        journal.History = (journal.History ?? new()).Where(e => e is not null && e.AppId > 0).TakeLast(2000).ToList();
        if (journal.Pending is { } pending)
        {
            pending.Games = (pending.Games ?? new()).Where(g => g is not null && g.AppId > 0).DistinctBy(g => g.AppId).Take(30000).ToList();
            if (loaded.PreserveOriginal) pending.RecheckAll = true;
        }
        return journal;
    }
    public void Save(string path)
    {
        SettingsFile.Save(path, this, ReadOnly, PreserveBackup); PreserveBackup = false;
    }
    public void Begin(IEnumerable<SteamGame> games, bool unlock) => Pending = new() { Games = games.Where(g => g.AppId > 0).DistinctBy(g => g.AppId).ToList(), Unlock = unlock };
    public void Start(uint id) { if (Pending is not null) { Pending.Active = id; Pending.InterruptionRecorded = false; } }
    public void Complete(SteamGame game, string operation, bool success, string detail)
    {
        string outcome = success ? operation == "Read progress" ? "Read" : detail is "Already complete" or "No achievements" ? "No changes" : "Confirmed"
            : detail.Contains("unknown", StringComparison.OrdinalIgnoreCase) || detail.Contains("unconfirmed", StringComparison.OrdinalIgnoreCase) ? "Uncertain" : "Failed";
        Add(new(DateTimeOffset.UtcNow, game.AppId, game.Name, operation, outcome, detail));
        if (Pending is not null) { Pending.Games.RemoveAll(g => g.AppId == game.AppId); Pending.Active = null; if (Pending.Games.Count == 0) Pending = null; }
    }
    public bool RecoverInterrupted()
    {
        if (Pending is not { Active: uint id, InterruptionRecorded: false } p) return false;
        var game = p.Games.FirstOrDefault(g => g.AppId == id);
        if (game is not null) Add(new(DateTimeOffset.UtcNow, id, game.Name, p.Unlock ? "Unlock all" : "Read progress", "Uncertain", "Application closed during this game. Read progress before deciding to retry."));
        p.InterruptionRecorded = true; return true;
    }
    public void Add(OperationEntry entry) { History.Add(entry); if (History.Count > 2000) History.RemoveRange(0, History.Count - 2000); }
    public List<SteamGame> Resumable(IEnumerable<SteamGame> library, LibraryState state)
    {
        var ids = library.Select(g => g.AppId).ToHashSet();
        return Pending?.Games.Where(g => ids.Contains(g.AppId) && !state.Excluded.ContainsKey(g.AppId)).ToList() ?? new();
    }
}

public record LibrarySummary(int Total, int Completed, int Incomplete, int Unknown, int NoAchievements, int Unlocked, int KnownAchievements)
{
    public static LibrarySummary Calculate(IEnumerable<SteamGame> games, LibraryState state)
    {
        var source = games.Where(g => g.AppId > 0 && !state.Excluded.ContainsKey(g.AppId)).DistinctBy(g => g.AppId).ToList();
        var known = source.Select(g => state.Progress.GetValueOrDefault(g.AppId)).Where(p => p?.IsFresh == true).Cast<GameProgress>().ToList();
        return new(source.Count, known.Count(p => p.Complete), known.Count(p => p.Total > 0 && !p.Complete), source.Count - known.Count,
            known.Count(p => p.Total == 0), known.Sum(p => p.Unlocked), known.Sum(p => p.Total));
    }
}
