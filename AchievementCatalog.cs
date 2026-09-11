using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace SteamAchievementUnlocker;

public enum AchievementSupport { Unknown, None, Available }
public record AchievementCacheEntry(AchievementSupport Support, DateTimeOffset CheckedAt);
public record FilterProgress(int Checked, int Total);

/// <summary>
/// Best-effort Store metadata, independent of a user's unlocked achievements.
/// An unavailable/delisted app or a failed request is never treated as zero achievements.
/// </summary>
public sealed class AchievementCatalog
{
    private readonly HttpClient _http;
    private readonly string _cachePath;
    private readonly TimeSpan _spacing;
    private readonly ConcurrentDictionary<uint, AchievementCacheEntry> _cache = new();
    private static readonly TimeSpan KnownLifetime = TimeSpan.FromDays(7);
    private static readonly TimeSpan UnknownLifetime = TimeSpan.FromMinutes(10);

    public AchievementCatalog(HttpClient http, string cachePath, TimeSpan? spacing = null)
    {
        _http = http;
        _cachePath = cachePath;
        _spacing = spacing ?? TimeSpan.FromMilliseconds(650);
        try
        {
            if (File.Exists(cachePath))
                foreach (var pair in JsonSerializer.Deserialize<Dictionary<uint, AchievementCacheEntry>>(File.ReadAllText(cachePath)) ?? new())
                    _cache[pair.Key] = pair.Value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    public AchievementSupport GetSupport(uint appId)
    {
        if (!_cache.TryGetValue(appId, out var entry) || !Enum.IsDefined(entry.Support)) return AchievementSupport.Unknown;
        var age = DateTimeOffset.UtcNow - entry.CheckedAt;
        var lifetime = entry.Support == AchievementSupport.Unknown ? UnknownLifetime : KnownLifetime;
        return age >= TimeSpan.Zero && age < lifetime ? entry.Support : AchievementSupport.Unknown;
    }

    private bool IsFresh(uint appId)
    {
        if (!_cache.TryGetValue(appId, out var entry) || !Enum.IsDefined(entry.Support)) return false;
        var age = DateTimeOffset.UtcNow - entry.CheckedAt;
        return age >= TimeSpan.Zero && age < (entry.Support == AchievementSupport.Unknown ? UnknownLifetime : KnownLifetime);
    }

    public List<SteamGame> Filter(IEnumerable<SteamGame> games, bool hideWithoutAchievements) =>
        games.Where(g => !hideWithoutAchievements || GetSupport(g.AppId) != AchievementSupport.None).ToList();

    public void RecordFromClient(uint appId, bool hasAchievements)
    {
        _cache[appId] = new(hasAchievements ? AchievementSupport.Available : AchievementSupport.None, DateTimeOffset.UtcNow);
        Save();
    }

    public void Clear()
    {
        _cache.Clear();
        Save();
    }

    public async Task RefreshAsync(IEnumerable<SteamGame> games, IProgress<FilterProgress>? progress, CancellationToken cancellationToken)
    {
        var pending = games.Select(g => g.AppId).Distinct().Where(id => !IsFresh(id)).ToArray();
        int completed = 0, failures = 0, stopped = 0;
        try
        {
            await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cancellationToken }, async (id, token) =>
            {
                if (Volatile.Read(ref stopped) != 0) return;
                AchievementSupport support = AchievementSupport.Unknown;
                bool failedRequest = false;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(8));
                    using var response = await _http.GetAsync($"https://store.steampowered.com/api/appdetails?appids={id}&filters=categories,achievements", timeout.Token);
                    if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
                        Interlocked.Exchange(ref stopped, 1);
                    if (response.IsSuccessStatusCode)
                        support = ParseStoreResponse(await response.Content.ReadAsStringAsync(timeout.Token), id);
                    else failedRequest = true;
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException) { failedRequest = true; }
                if (token.IsCancellationRequested) return;
                _cache[id] = new(support, DateTimeOffset.UtcNow);
                // Bound an offline or unavailable-service scan instead of waiting on hundreds of timeouts.
                if (failedRequest && Interlocked.Increment(ref failures) >= 6)
                    Interlocked.Exchange(ref stopped, 1);
                else if (!failedRequest) Interlocked.Exchange(ref failures, 0);
                progress?.Report(new(Interlocked.Increment(ref completed), pending.Length));
                await Task.Delay(_spacing, token);
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { Save(); }
    }

    public static AchievementSupport ParseStoreResponse(string json, uint appId)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(appId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var app) ||
            app.ValueKind != JsonValueKind.Object || !app.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True ||
            !app.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return AchievementSupport.Unknown;

        int? total = null;
        if (data.TryGetProperty("achievements", out var achievements) && achievements.ValueKind == JsonValueKind.Object &&
            achievements.TryGetProperty("total", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out int number) && number >= 0)
            total = number;
        if (total > 0) return AchievementSupport.Available;

        bool hasCategories = false;
        if (data.TryGetProperty("categories", out var categories) && categories.ValueKind == JsonValueKind.Array)
        {
            foreach (var category in categories.EnumerateArray())
            {
                if (category.ValueKind != JsonValueKind.Object || !category.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out int value))
                    return AchievementSupport.Unknown;
                if (value == 22) return AchievementSupport.Available; // Steam Achievements
                hasCategories = true;
            }
        }
        return total == 0 || hasCategories ? AchievementSupport.None : AchievementSupport.Unknown;
    }

    private void Save()
    {
        string temporary = _cachePath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_cachePath))!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(_cache));
            File.Move(temporary, _cachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
