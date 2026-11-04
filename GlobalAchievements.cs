using System.Globalization;
using System.Text.Json;

namespace SteamAchievementUnlocker;

public sealed class GlobalAchievements
{
    public static Dictionary<string, double> Parse(string json)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("achievementpercentages", out var root) || !root.TryGetProperty("achievements", out var items) || items.ValueKind != JsonValueKind.Array) return result;
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(n.GetString()) || !item.TryGetProperty("percent", out var p)) continue;
                double percent;
                bool valid = p.ValueKind == JsonValueKind.Number ? p.TryGetDouble(out percent) : double.TryParse(p.ValueKind == JsonValueKind.String ? p.GetString() : null, NumberStyles.Float, CultureInfo.InvariantCulture, out percent);
                if (valid && double.IsFinite(percent) && percent >= 0 && percent <= 100) result[n.GetString()!] = percent;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        return result;
    }
    public static async Task<Dictionary<string, double>> LoadAsync(HttpClient http, uint appId, string directory, CancellationToken token)
    {
        string path = Path.Combine(directory, appId + ".json");
        try
        {
            if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) is var age && age >= TimeSpan.Zero && age < TimeSpan.FromHours(24))
            { var cached = Parse(await File.ReadAllTextAsync(path, token)); if (cached.Count > 0) return cached; }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await http.GetAsync($"https://api.steampowered.com/ISteamUserStats/GetGlobalAchievementPercentagesForApp/v2/?gameid={appId}", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) return new();
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token); using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
            while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0) { if (output.Length + count > 2 * 1024 * 1024) return new(); output.Write(buffer, 0, count); }
            string json = System.Text.Encoding.UTF8.GetString(output.ToArray()); var parsed = Parse(json);
            if (parsed.Count > 0)
            {
                try
                {
                    Directory.CreateDirectory(directory); string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try { await File.WriteAllTextAsync(temp, json, token); File.Move(temp, path, true); }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return parsed;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException) { return new(); }
    }
}
