using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Steamworks;

namespace SteamAchievementUnlocker;

public record DesktopRequest(ulong ExpectedAccount, string Operation, string[]? AchievementNames = null);
public record DesktopResult(uint AppId, bool Success, string Status, List<AchievementInfo>? Achievements = null);

public static class DesktopWorker
{
    public static int Run(string[] args)
    {
        var output = Console.Out;
        Console.SetOut(TextWriter.Null); // Only the structured response goes to the parent.
        DesktopResult result;
        uint id = 0;
        try
        {
            if (args.Length != 2 || !uint.TryParse(args[1], out id) || id == 0) throw new ArgumentException();
            var request = JsonSerializer.Deserialize<DesktopRequest>(Console.ReadLine() ?? "");
            if (request is null || request.ExpectedAccount == 0 || request.Operation is not ("read" or "unlock" or "unlock-all")) throw new ArgumentException();
            using var sessionLock = new Mutex(false, @"Local\SteamAchievementUnlocker.Session");
            bool acquired;
            try { acquired = sessionLock.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) result = new(id, false, "Another Steam session is running. Try again after it finishes.");
            else
            {
                try { result = Execute(id, request); }
                finally { sessionLock.ReleaseMutex(); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        { result = new(id, false, "Could not read Steam data. Check Steam and access to the application folder."); }
        output.Write(JsonSerializer.Serialize(result));
        return result.Success ? 0 : 1;
    }

    private static DesktopResult Execute(uint id, DesktopRequest request)
    {
        if (!SteamRuntime.ValidateNativeLibrary(out _)) return new(id, false, "Native Steam DLL is missing or incompatible. Extract the full release ZIP.");
        using var steam = new SteamStatsManager(id, request.ExpectedAccount);
        if (!steam.Init()) return new(id, false, steam.SessionChanged ? "Steam account changed. Refresh the library before continuing." : "No Steam access / connection failed.");
        if (SteamUser.GetSteamID().m_SteamID != request.ExpectedAccount)
            return new(id, false, "Steam account changed. Refresh the library before continuing.");
        var timer = Stopwatch.StartNew();
        while (!steam.StatsLoaded && !steam.StatsFailed && steam.IsSameSession && timer.Elapsed < TimeSpan.FromSeconds(20))
        { steam.RunCallbacks(); Thread.Sleep(100); }
        if (!steam.StatsLoaded) return new(id, false, steam.SessionChanged ? "Steam account changed. Refresh the library before continuing." : "Steam stats unavailable. Try launching the game once.");
        if (!SteamUser.BLoggedOn() || SteamUser.GetSteamID().m_SteamID != request.ExpectedAccount)
            return new(id, false, "Steam account changed. Refresh the library before continuing.");
        var achievements = ReadAchievements(steam);
        if (!steam.IsSameSession) return new(id, false, "Steam account changed. Refresh the library before continuing.");
        if (request.Operation == "read") return new(id, true, "Progress read", achievements);
        var selected = request.AchievementNames?.ToHashSet(StringComparer.Ordinal) ?? new();
        if (request.Operation == "unlock" && (selected.Count == 0 || selected.Any(name => achievements.All(a => a.ApiName != name))))
            return new(id, false, "Achievement selection is no longer valid. Reopen the game.");
        var locked = achievements.Where(a => !a.Unlocked && (request.Operation == "unlock-all" || selected.Contains(a.ApiName))).ToList();
        if (locked.Count == 0) return new(id, true, achievements.Count == 0 ? "No achievements" : "Already complete", achievements);
        if (!steam.UnlockAll(locked.Select(a => a.ApiName)))
            return new(id, false, steam.SessionChanged ? "Steam account changed. Outcome unknown; read progress before retrying." : "Save failed or unconfirmed. Read progress again before retrying.");
        var confirmed = ReadAchievements(steam);
        return steam.IsSameSession ? new(id, true, "Confirmed by Steam", confirmed) : new(id, false, "Steam account changed. Outcome unknown; read progress before retrying.");
    }

    private static List<AchievementInfo> ReadAchievements(SteamStatsManager steam) => steam.ListAchievements().Select(a =>
        new AchievementInfo(a.ApiName, a.DisplayName, a.Unlocked, SteamUserStats.GetAchievementDisplayAttribute(a.ApiName, "desc"), ReadIcon(a.ApiName))).ToList();
    private static byte[]? ReadIcon(string name)
    {
        int id = SteamUserStats.GetAchievementIcon(name);
        if (id <= 0 || !SteamUtils.GetImageSize(id, out uint width, out uint height) || width == 0 || height == 0 || width > 128 || height > 128) return null;
        var bytes = new byte[checked((int)(width * height * 4))];
        if (!SteamUtils.GetImageRGBA(id, bytes, bytes.Length)) return null;
        for (int i = 0; i < bytes.Length; i += 4) (bytes[i], bytes[i + 2]) = (bytes[i + 2], bytes[i]);
        using var image = new System.Drawing.Bitmap((int)width, (int)height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = image.LockBits(new System.Drawing.Rectangle(0, 0, image.Width, image.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try { System.Runtime.InteropServices.Marshal.Copy(bytes, 0, data.Scan0, bytes.Length); }
        finally { image.UnlockBits(data); }
        using var stream = new MemoryStream(); image.Save(stream, System.Drawing.Imaging.ImageFormat.Png); return stream.ToArray();
    }

    public static async Task<DesktopResult> StartAsync(uint appId, DesktopRequest request)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException())
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(info.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        info.ArgumentList.Add("--desktop-worker"); info.ArgumentList.Add(appId.ToString());
        try
        {
            using var process = Process.Start(info) ?? throw new InvalidOperationException();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request));
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(true);
                await process.WaitForExitAsync();
                return new(appId, false, "Timed out; outcome unknown. Read progress again before retrying.");
            }
            await error;
            var result = JsonSerializer.Deserialize<DesktopResult>(await output);
            if (result is null || result.AppId != appId || (process.ExitCode != 0 && result.Success)) throw new JsonException();
            return result;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or JsonException)
        { return new(appId, false, "Game worker failed; outcome unknown."); }
    }
}
