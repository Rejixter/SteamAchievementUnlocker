using System.Text.Json;

namespace SteamAchievementUnlocker;

public record SettingsLoad<T>(T Value, string? Notice, bool ReadOnly, bool PreserveOriginal);
public static class SettingsFile
{
    public const int CurrentVersion = 1;
    public static SettingsLoad<T> Load<T>(string path) where T : new()
    {
        var primary = Read<T>(path);
        if (primary.Future) return new(new(), "Settings were created by a newer app. Defaults are temporary; the original file will not be overwritten. Use the newer app or explicitly reset these settings.", true, true);
        if (primary.Value is { } value) return new(value, primary.Legacy ? "Older settings loaded. They will be upgraded with a backup on the next save." : null, false, false);
        var backup = Read<T>(path + ".bak");
        if (backup.Future) return new(new(), "The settings backup belongs to a newer app. Temporary defaults are in use; existing files will not be overwritten. Use the newer app or explicitly reset these settings.", true, true);
        if (backup.Value is { } recovered) return new(recovered, "Settings recovered from the last readable backup. The rejected primary will be preserved on save.", false, true);
        return new(new(), File.Exists(path) || File.Exists(path + ".bak") ? "Settings and backup could not be read. Temporary defaults are in use; existing files will be preserved on save." : null, false, true);
    }
    private static (T? Value, bool Future, bool Legacy) Read<T>(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 8 * 1024 * 1024) return (default, false, false);
            string json = File.ReadAllText(path); using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (default, false, false);
            int version = 0;
            if (doc.RootElement.TryGetProperty("SchemaVersion", out var v) && (!v.TryGetInt32(out version) || version < 0)) return (default, false, false);
            if (version > CurrentVersion) return (default, true, false);
            return (JsonSerializer.Deserialize<T>(json), false, version == 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return (default, false, false); }
    }
    public static void Save<T>(string path, T value, bool readOnly, bool preserveOriginal)
    {
        if (readOnly || Read<T>(path).Future || Read<T>(path + ".bak").Future) throw new InvalidOperationException("Newer settings are protected from overwrite. Reset explicitly in Settings.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value));
            bool valid = Read<T>(path).Value is not null;
            if (File.Exists(path) && (!valid || preserveOriginal)) File.Copy(path, path + ".rejected-" + Guid.NewGuid().ToString("N"));
            if (File.Exists(path) && valid && !preserveOriginal) File.Replace(temp, path, path + ".bak");
            else { File.Move(temp, path, true); if (!File.Exists(path + ".bak")) File.Copy(path, path + ".bak"); }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static void PreserveAndReset(string path)
    {
        // Explicit UI action only; keep an exact copy before removing the active files.
        foreach (string file in new[] { path, path + ".bak" })
            if (File.Exists(file)) { File.Copy(file, file + ".preserved-" + Guid.NewGuid().ToString("N")); File.Delete(file); }
    }
}
