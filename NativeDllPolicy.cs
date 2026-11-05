using System.Security.Cryptography;

namespace SteamAchievementUnlocker;

public static class NativeDllPolicy
{
    public const string ExpectedSha256 = "A44E5537939AE4EEBC69000589AA9B2437A667813A1657CC779198BAE9B815A9";
    public static FileStream OpenVerified(string path)
    {
        // Retain a read-only, non-delete-shared handle until the caller has loaded the DLL.
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (!Convert.ToHexString(SHA256.HashData(file)).Equals(ExpectedSha256, StringComparison.Ordinal))
                throw new BadImageFormatException("Steam library integrity check failed. Extract a trusted release again.");
            file.Position = 0; return file;
        }
        catch { file.Dispose(); throw; }
    }
}
