using System.Runtime.InteropServices;

namespace SteamAchievementUnlocker;

public static class SteamRuntime
{
    private static string NativePath => File.Exists(Path.Combine(AppContext.BaseDirectory, "lib", "steam_api64.dll"))
        ? Path.Combine(AppContext.BaseDirectory, "lib", "steam_api64.dll")
        : Path.Combine(AppContext.BaseDirectory, "steam_api64.dll");

    static SteamRuntime()
    {
        NativeLibrary.SetDllImportResolver(typeof(Steamworks.SteamAPI).Assembly, (name, assembly, searchPath) =>
            name is "steam_api64" or "steam_api64.dll" ? LoadTrusted(NativePath) : 0);
    }
    private static nint LoadTrusted(string path)
    {
        using var verified = NativeDllPolicy.OpenVerified(path);
        return NativeLibrary.Load(path);
    }

    public static bool ValidateNativeLibrary(out string diagnostic)
    {
        string path = NativePath;
        nint handle = 0;
        try
        {
            if (!Environment.Is64BitProcess) throw new BadImageFormatException();
            handle = LoadTrusted(path);
            foreach (string export in new[] { "SteamAPI_Init", "SteamAPI_Shutdown", "SteamAPI_RunCallbacks", "SteamAPI_ISteamUserStats_GetNumAchievements" })
                if (!NativeLibrary.TryGetExport(handle, export, out _))
                {
                    diagnostic = $"Incompatible steam_api64.dll: missing {export}. Restore the DLL from Steamworks.NET Standalone 20.2.0 (Windows-x64), then rebuild or re-extract the complete release ZIP.";
                    return false;
                }
            // Exercise the actual Steamworks.NET import resolver without initializing Steam.
            _ = Steamworks.SteamAPI.IsSteamRunning();
            diagnostic = "OK: 64-bit process; Steam SDK 1.57 entry points and managed import resolver verified. Steam was not initialized and no achievements were changed.";
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            diagnostic = "Missing, modified or invalid steam_api64.dll. Extract the entire trusted Windows-x64 release ZIP again.";
            return false;
        }
        finally { if (handle != 0) NativeLibrary.Free(handle); }
    }
}
