using System.Text.RegularExpressions;

namespace SteamAchievementUnlocker;

public static class UserSettings
{
    // User data deliberately lives outside both the checkout and portable release.
    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SteamAchievementUnlocker");
    public static string KeyPath => Path.Combine(DirectoryPath, "web_api_key.txt");

    public static string ReadApiKey()
    {
        try { return File.Exists(KeyPath) ? File.ReadAllText(KeyPath).Trim() : ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    public static bool IsValidApiKey(string key) => Regex.IsMatch(key, "\\A[0-9a-fA-F]{32}\\z");

    public static void ConfigureApiKey()
    {
        Console.WriteLine("\nOptional: your own Steam Web API key enables the full owned library.");
        Console.WriteLine("Stored only in your Windows user folder. Input is hidden.");
        Console.WriteLine("Paste a key and press Enter, or press Enter without a key to remove it.");
        Console.Write("Key: ");
        var input = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Escape) { Console.WriteLine("\nCancelled."); return; }
            if (key.Key == ConsoleKey.Backspace) { if (input.Length > 0) input.Length--; }
            else if (!char.IsControl(key.KeyChar) && input.Length < 256) input.Append(key.KeyChar);
        }
        Console.WriteLine();
        string value = input.ToString().Trim();
        if (value.Length != 0 && !IsValidApiKey(value))
        {
            Console.WriteLine("Expected a 32-character hexadecimal key. Nothing was changed.");
            return;
        }
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            if (value.Length == 0) File.Delete(KeyPath);
            else File.WriteAllText(KeyPath, value);
            Console.WriteLine(value.Length == 0 ? "Key removed. Installed-games mode enabled." : "Key saved in your Windows user folder.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine("Couldn't save settings. Check access to your Windows user folder.");
        }
    }
}
