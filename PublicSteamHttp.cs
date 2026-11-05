namespace SteamAchievementUnlocker;

public static class PublicSteamHttp
{
    // Public endpoints never receive API credentials; prevent redirecting into other hosts/protocols.
    public static HttpClientHandler CreateHandler() => new() { AllowAutoRedirect = false, UseCookies = false };
    public static HttpClient CreateClient() => new(CreateHandler()) { Timeout = TimeSpan.FromSeconds(15) };
    public static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int limit, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > limit) throw new IOException("Response exceeds the allowed size.");
        using var input = await response.Content.ReadAsStreamAsync(token); using var output = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        { if (output.Length + count > limit) throw new IOException("Response exceeds the allowed size."); output.Write(buffer, 0, count); }
        return output.ToArray();
    }
}
