using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SteamAchievementUnlocker;

public sealed class AchievementIconCache(string directory)
{
    public string PathFor(uint appId, string name, bool unlocked) => Path.Combine(directory, appId.ToString(),
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name))) + (unlocked ? "-unlocked.png" : "-locked.png"));
    public static bool Valid(byte[] data)
    {
        if (data.Length is < 33 or > 131072 || !data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16, 4)) is 0 or > 128 || BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20, 4)) is 0 or > 128) return false;
        try
        {
            using var stream = new MemoryStream(data);
            using var image = System.Drawing.Image.FromStream(stream, false, true);
            return image.Width is > 0 and <= 128 && image.Height is > 0 and <= 128;
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException) { return false; }
    }
    public byte[]? Read(uint appId, string name, bool unlocked)
    {
        try
        {
            string path = PathFor(appId, name, unlocked);
            if (!File.Exists(path) || new FileInfo(path).Length > 131072) return null;
            var data = File.ReadAllBytes(path); return Valid(data) ? data : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
    public void Save(uint appId, string name, bool unlocked, byte[] data)
    {
        if (appId == 0 || !Valid(data)) return;
        string path = PathFor(appId, name, unlocked), temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            try { File.WriteAllBytes(temp, data); File.Move(temp, path, true); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
