using System.Security.Cryptography;

namespace SteamAchievementUnlocker;

public sealed class CredentialStore
{
    private readonly ISecretProtector? _protector;
    public string LegacyPath { get; }
    public string ProtectedPath { get; }
    public string? Error { get; private set; }
    public bool Exists => File.Exists(ProtectedPath) || File.Exists(LegacyPath);
    public string Status => Error ?? (File.Exists(ProtectedPath) ? "Saved: Windows user-protected key. Verification is separate." : File.Exists(LegacyPath) ? "Legacy key found. Protected migration required before use." : "No API key saved.");
    public CredentialStore(string directory, ISecretProtector? protector = null)
    {
        LegacyPath = Path.Combine(directory, "web_api_key.txt"); ProtectedPath = Path.Combine(directory, "web_api_key.dat"); _protector = protector;
    }
    public string Read()
    {
        Error = null;
        try
        {
            if (File.Exists(ProtectedPath))
            {
                string protectedKey = CredentialVault.Read(ProtectedPath, _protector);
                try { if (File.Exists(LegacyPath)) File.Delete(LegacyPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { Error = "Protected key loaded, but legacy plaintext cleanup failed. Remove the legacy file from your user settings folder."; }
                return protectedKey;
            }
            if (!File.Exists(LegacyPath)) return "";
            if (new FileInfo(LegacyPath).Length > 1024) throw new CryptographicException();
            string key = File.ReadAllText(LegacyPath).Trim();
            if (!UserSettings.IsValidApiKey(key)) throw new CryptographicException();
            Save(key); // No legacy plaintext fallback if protection/readback fails.
            return key;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        { Error = "Saved key could not be protected/read. It will not be used. Re-enter it in Settings or continue without a key."; return ""; }
    }
    public void Save(string value)
    {
        value = value.Trim();
        if (value.Length == 0) { File.Delete(ProtectedPath); File.Delete(LegacyPath); Error = null; return; }
        CredentialVault.Save(ProtectedPath, value, _protector);
        if (File.Exists(LegacyPath)) File.Delete(LegacyPath);
        Error = null;
    }
}
