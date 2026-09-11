using System.Runtime.InteropServices;

namespace SteamAchievementUnlocker;

public static class SteamRuntime
{
    public static bool ValidateNativeLibrary(out string diagnostic)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "steam_api64.dll");
        nint handle = 0;
        try
        {
            if (!Environment.Is64BitProcess) throw new BadImageFormatException();
            handle = NativeLibrary.Load(path);
            foreach (string export in new[] { "SteamAPI_Init", "SteamAPI_Shutdown", "SteamAPI_RunCallbacks", "SteamAPI_ISteamUserStats_GetNumAchievements" })
                if (!NativeLibrary.TryGetExport(handle, export, out _))
                {
                    diagnostic = $"Incompatible steam_api64.dll: missing {export}. Restore the DLL from Steamworks.NET Standalone 20.2.0 (Windows-x64), then rebuild or re-extract the complete release ZIP.";
                    return false;
                }
            diagnostic = "OK: 64-bit process; Steam SDK 1.57 entry points present. Steam was not initialized and no achievements were changed.";
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
        {
            diagnostic = "Missing or invalid 64-bit steam_api64.dll. Extract the entire Windows-x64 release ZIP into a folder before running the EXE.";
            return false;
        }
        finally { if (handle != 0) NativeLibrary.Free(handle); }
    }
}
