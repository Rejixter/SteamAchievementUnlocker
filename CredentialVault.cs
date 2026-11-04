using System.Security.Cryptography;
using System.Text;

namespace SteamAchievementUnlocker;

public interface ISecretProtector
{
    byte[] Protect(byte[] input);
    byte[] Unprotect(byte[] input);
}
internal sealed class WindowsSecretProtector : ISecretProtector
{
    public byte[] Protect(byte[] input) => ProtectedData.Protect(input, null, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] input) => ProtectedData.Unprotect(input, null, DataProtectionScope.CurrentUser);
}
public static class CredentialVault
{
    public static void Save(string path, string key, ISecretProtector? protector = null)
    {
        protector ??= new WindowsSecretProtector();
        if (!UserSettings.IsValidApiKey(key)) throw new ArgumentException("Invalid key format.");
        byte[] plain = Encoding.UTF8.GetBytes(key);
        byte[] encrypted;
        try { encrypted = protector.Protect(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, encrypted);
            if (Read(temp, protector) != key) throw new CryptographicException("Protected key verification failed.");
            // Preserve the old encrypted file until the new encrypted file has passed readback.
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static string Read(string path, ISecretProtector? protector = null)
    {
        protector ??= new WindowsSecretProtector();
        if (new FileInfo(path).Length > 16384) throw new CryptographicException("Invalid protected settings.");
        byte[] plain = protector.Unprotect(File.ReadAllBytes(path));
        try
        {
            string key = Encoding.UTF8.GetString(plain);
            if (!UserSettings.IsValidApiKey(key)) throw new CryptographicException("Invalid protected key format.");
            return key;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
