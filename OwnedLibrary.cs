using System.Net;
using System.Text.Json;

namespace SteamAchievementUnlocker;

public record OwnedLibraryResult(List<SteamGame>? Games, string Verification);
public static class OwnedLibrary
{
    public static HttpClientHandler CreateHandler() => new() { AllowAutoRedirect = false, UseCookies = false };
    public static HttpClient CreateClient() => new(CreateHandler()) { Timeout = TimeSpan.FromSeconds(15) };
    public static HttpRequestMessage Request(string key, ulong account)
    {
        if (!UserSettings.IsValidApiKey(key) || account == 0) throw new ArgumentException("Invalid library request.");
        var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/?steamid={account}&format=json&include_appinfo=1&include_played_free_games=1");
        request.Headers.Add("x-webapi-key", key); return request;
    }
    public static async Task<OwnedLibraryResult> FetchAsync(HttpClient http, string key, ulong account)
    {
        try
        {
            using var request = Request(key, account);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return new(null, "Rejected by Steam (key or access permissions).");
            if (!response.IsSuccessStatusCode) return new(null, "Not verified: Steam unavailable or request redirected.");
            using var input = await response.Content.ReadAsStreamAsync(); using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0) { if (output.Length + count > 10 * 1024 * 1024) return new(null, "Not verified: unexpected response size."); output.Write(buffer, 0, count); }
            using var doc = JsonDocument.Parse(output.ToArray());
            if (!doc.RootElement.TryGetProperty("response", out var data)) return new(null, "Not verified: no owned-library data.");
            if (!data.TryGetProperty("games", out var games))
                return data.TryGetProperty("game_count", out var total) && total.TryGetInt32(out int n) && n == 0 ? new(new(), "Verified by Steam for this library (empty).") : new(null, "Not verified: library unavailable or private.");
            if (games.ValueKind != JsonValueKind.Array) return new(null, "Not verified: malformed library data.");
            var result = new List<SteamGame>();
            foreach (var game in games.EnumerateArray())
            {
                if (!game.TryGetProperty("appid", out var id) || !id.TryGetUInt32(out uint appId) || appId == 0) continue;
                string name = game.TryGetProperty("name", out var title) && title.ValueKind == JsonValueKind.String ? title.GetString() ?? $"AppID {appId}" : $"AppID {appId}";
                result.Add(new(appId, name, false));
            }
            return new(result, "Verified by Steam for this library, this session.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or ArgumentException or InvalidOperationException or OperationCanceledException)
        { return new(null, "Not verified: connection or response error. Key contents are never included in diagnostics."); }
    }
}
