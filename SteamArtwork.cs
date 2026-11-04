using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net;
using System.Text.Json;

namespace SteamAchievementUnlocker;

// Public Store artwork only. Never sends an API key or Steam account identifier.
public sealed class SteamArtwork
{
    private readonly HttpClient _http;
    private readonly string _directory;
    private readonly Dictionary<uint, DateTimeOffset> _missing = new();
    private DateTimeOffset _cooldown;
    public SteamArtwork(HttpClient http, string directory)
    {
        _http = http; _directory = directory;
        try
        {
            string path = Path.Combine(directory, "missing.json");
            if (File.Exists(path) && new FileInfo(path).Length <= 2 * 1024 * 1024)
                foreach (var item in JsonSerializer.Deserialize<Dictionary<uint, DateTimeOffset>>(File.ReadAllText(path)) ?? new())
                    if (item.Key > 0 && item.Value > DateTimeOffset.UtcNow && item.Value < DateTimeOffset.UtcNow.AddDays(8)) _missing[item.Key] = item.Value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }
    public bool MissingCached(uint id) => _missing.GetValueOrDefault(id) > DateTimeOffset.UtcNow;
    public int MissingCount(IEnumerable<uint> ids) => ids.Distinct().Count(MissingCached);
    public bool CoolingDown => _cooldown > DateTimeOffset.UtcNow;
    public (int Count, long Bytes, DateTime? LastWrite) CacheInfo()
    {
        if (!Directory.Exists(_directory)) return (0, 0, null);
        var files = Directory.EnumerateFiles(_directory, "*.jpg", SearchOption.TopDirectoryOnly)
            .Where(p => uint.TryParse(Path.GetFileNameWithoutExtension(p), out _)).Select(p => new FileInfo(p)).ToList();
        return (files.Count, files.Sum(f => f.Length), files.Count == 0 ? null : files.Max(f => f.LastWriteTimeUtc));
    }
    // Call only after the active download finishes. Never deletes recursively or touches other settings.
    public void Reset(uint? id = null)
    {
        if (id.HasValue)
        {
            File.Delete(Path.Combine(_directory, id.Value + ".jpg")); _missing.Remove(id.Value);
        }
        else
        {
            if (Directory.Exists(_directory))
                foreach (var path in Directory.EnumerateFiles(_directory, "*.jpg", SearchOption.TopDirectoryOnly))
                    if (uint.TryParse(Path.GetFileNameWithoutExtension(path), out _)) File.Delete(path);
            _missing.Clear();
        }
        _cooldown = default; SaveMissing();
    }
    public Bitmap? ReadCached(uint id)
    {
        try { using var image = Image.FromFile(Path.Combine(_directory, id + ".jpg")); return Thumbnail(image); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException) { return null; }
    }
    public bool HasCached(uint id)
    {
        try { var file = new FileInfo(Path.Combine(_directory, id + ".jpg")); return id > 0 && file.Exists && file.Length > 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    public static List<uint> Queue(IEnumerable<uint> library, IEnumerable<uint> visible, ISet<uint> inMemory, ISet<uint> attempted, Func<uint, bool> cached) =>
        visible.Where(id => !inMemory.Contains(id)).Concat(library.Where(id => !cached(id)))
            .Where(id => id > 0 && !attempted.Contains(id)).Distinct().ToList();

    public static Uri? ImageUrl(string json, uint id)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(id.ToString(), out var app) ||
                !app.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True ||
                !app.TryGetProperty("data", out var data) || !data.TryGetProperty("header_image", out var image) ||
                image.ValueKind != JsonValueKind.String || !Uri.TryCreate(image.GetString(), UriKind.Absolute, out var uri)) return null;
            return uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
                (uri.Host == "steamstatic.com" || uri.Host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase)) ? uri : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return null; }
    }

    public async Task<Bitmap?> GetAsync(uint id, CancellationToken token)
    {
        if (id == 0) return null;
        string path = Path.Combine(_directory, id + ".jpg");
        try
        {
            if (File.Exists(path))
            {
                try { using var cached = Image.FromFile(path); return Thumbnail(cached); }
                catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException) { }
            }
            if (_cooldown > DateTimeOffset.UtcNow || _missing.GetValueOrDefault(id) > DateTimeOffset.UtcNow) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var metadata = await _http.GetAsync($"https://store.steampowered.com/api/appdetails?appids={id}&filters=basic", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (Limited(metadata.StatusCode)) return null;
            if (!metadata.IsSuccessStatusCode) return Missing(id, metadata.StatusCode == HttpStatusCode.NotFound);
            byte[] json = await ReadLimited(metadata, 512 * 1024, timeout.Token);
            var url = ImageUrl(System.Text.Encoding.UTF8.GetString(json), id);
            if (url is null) return Missing(id, true);
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (Limited(response.StatusCode)) return null;
            if (!response.IsSuccessStatusCode) return Missing(id, response.StatusCode == HttpStatusCode.NotFound);
            using var stream = new MemoryStream(await ReadLimited(response, 2 * 1024 * 1024, timeout.Token));
            using var original = Image.FromStream(stream);
            if (original.Width > 8192 || original.Height > 8192) return Missing(id, true);
            var thumbnail = Thumbnail(original);
            try
            {
                Directory.CreateDirectory(_directory);
                string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { thumbnail.Save(temp, ImageFormat.Jpeg); File.Move(temp, path, true); }
                finally { if (File.Exists(temp)) File.Delete(temp); }
                // Retain all downloaded library thumbnails across restarts, including large libraries.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException) { }
            return thumbnail;
        }
        catch (OperationCanceledException) { if (token.IsCancellationRequested) throw; _cooldown = DateTimeOffset.UtcNow.AddSeconds(30); return Missing(id); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
        { if (ex is HttpRequestException) _cooldown = DateTimeOffset.UtcNow.AddSeconds(30); return Missing(id); }
    }
    private bool Limited(HttpStatusCode code)
    {
        if (code is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)) return false;
        _cooldown = DateTimeOffset.UtcNow.AddMinutes(5); return true;
    }
    private Bitmap? Missing(uint id, bool persistent = false)
    {
        _missing[id] = persistent ? DateTimeOffset.UtcNow.AddDays(7) : DateTimeOffset.UtcNow.AddMinutes(5);
        if (!persistent) return null;
        SaveMissing(); return null;
    }
    private void SaveMissing()
    {
        try
        {
            Directory.CreateDirectory(_directory); string path = Path.Combine(_directory, "missing.json"), temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temp, JsonSerializer.Serialize(_missing.Where(p => p.Value > DateTimeOffset.UtcNow).Take(20000).ToDictionary())); File.Move(temp, path, true); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private static Bitmap Thumbnail(Image original)
    {
        var bitmap = new Bitmap(184, 86);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(35, 39, 48)); graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        double scale = Math.Min(184d / original.Width, 86d / original.Height);
        int w = Math.Max(1, (int)(original.Width * scale)), h = Math.Max(1, (int)(original.Height * scale));
        graphics.DrawImage(original, (184 - w) / 2, (86 - h) / 2, w, h);
        return bitmap;
    }
    private static async Task<byte[]> ReadLimited(HttpResponseMessage response, int limit, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > limit) throw new IOException("Artwork response is too large.");
        using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream(); var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        { if (output.Length + read > limit) throw new IOException("Artwork response is too large."); output.Write(buffer, 0, read); }
        return output.ToArray();
    }
}
