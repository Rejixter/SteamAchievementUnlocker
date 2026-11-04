using System.Text.RegularExpressions;

namespace SteamAchievementUnlocker;

// Deliberate allowlist: never accepts credentials, account IDs, log text, games or paths.
public static class DiagnosticReport
{
    public static string Create(string version, string theme, int gameCount, int historyCount, bool busy, bool snapshot, bool readOnlySettings) =>
        "Steam Achievement Unlocker diagnostic report\n" +
        "App version: " + (Regex.IsMatch(version, @"\A\d+\.\d+\.\d+\z") ? version : "Unknown") + "\n" +
        $"Runtime: {Environment.Version}\n64-bit process: {Environment.Is64BitProcess}\n" +
        "Theme: " + (theme is "Dark" or "Light" or "System" ? theme : "Unknown") + "\n" +
        $"Library count: {Math.Clamp(gameCount, 0, 1000000)}\nHistory count: {Math.Clamp(historyCount, 0, 2000)}\nOperation active: {busy}\nOffline snapshot: {snapshot}\nNewer settings protected: {readOnlySettings}\n" +
        "Excluded: API keys, Steam/account IDs, user names, machine names, file paths, game names, raw logs and exception messages.\n" +
        "This report is not sent anywhere. Save it locally only if you choose to.\n";
}
