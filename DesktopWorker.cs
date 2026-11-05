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
            if (request is null || request.ExpectedAccount == 0 || request.Operation is not ("read" or "icons" or "unlock" or "unlock-all")) throw new ArgumentException();
            using var sessionLock = new Mutex(false, @"Local\SteamAchievementUnlocker.Session");
            bool acquired;
            try { acquired = sessionLock.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) result = new(id, false, "Another Steam session is running. Try again after it finishes.");
            else
            {
                try { result = Execute(id, request, a => { output.WriteLine(JsonSerializer.Serialize(new DesktopResult(id, true, "Icon ready", new() { a }))); output.Flush(); }); }
                finally { sessionLock.ReleaseMutex(); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        { result = new(id, false, "Could not read Steam data. Check Steam and access to the application folder."); }
        output.Write(JsonSerializer.Serialize(result));
        return result.Success ? 0 : 1;
    }

    private static DesktopResult Execute(uint id, DesktopRequest request, Action<AchievementInfo> iconReady)
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
        if (request.Operation == "icons") return ReadIcons(steam, request, achievements, iconReady);
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
        new AchievementInfo(a.ApiName, a.DisplayName, a.Unlocked, SteamUserStats.GetAchievementDisplayAttribute(a.ApiName, "desc"))).ToList();
    private static byte[]? ReadIcon(int id)
    {
        if (id <= 0 || !SteamUtils.GetImageSize(id, out uint width, out uint height) || width == 0 || height == 0 || width > 512 || height > 512) return null;
        var bytes = new byte[checked((int)(width * height * 4))];
        if (!SteamUtils.GetImageRGBA(id, bytes, bytes.Length)) return null;
        for (int i = 0; i < bytes.Length; i += 4) (bytes[i], bytes[i + 2]) = (bytes[i + 2], bytes[i]);
        using var image = new System.Drawing.Bitmap((int)width, (int)height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = image.LockBits(new System.Drawing.Rectangle(0, 0, image.Width, image.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try { System.Runtime.InteropServices.Marshal.Copy(bytes, 0, data.Scan0, bytes.Length); }
        finally { image.UnlockBits(data); }
        using var thumbnail = new System.Drawing.Bitmap(image, new System.Drawing.Size(64, 64));
        using var stream = new MemoryStream(); thumbnail.Save(stream, System.Drawing.Imaging.ImageFormat.Png); return stream.ToArray();
    }

    private static DesktopResult ReadIcons(SteamStatsManager steam, DesktopRequest request, List<AchievementInfo> achievements, Action<AchievementInfo> ready)
    {
        var requested = (request.AchievementNames ?? Array.Empty<string>()).ToHashSet(StringComparer.Ordinal);
        var pending = achievements.Where(a => requested.Contains(a.ApiName)).ToDictionary(a => a.ApiName, StringComparer.Ordinal);
        int delivered = 0;
        var cache = new AchievementIconCache(Path.Combine(UserSettings.DirectoryPath, "achievement-icons"));
        void Complete(string name, int handle)
        {
            if (!pending.TryGetValue(name, out var a)) return;
            var bytes = handle > 0 ? ReadIcon(handle) : null;
            pending.Remove(name);
            if (bytes is null) return;
            delivered++; ready(a with { IconPng = bytes });
        }
        using var callback = Callback<UserAchievementIconFetched_t>.Create(icon =>
        {
            if (icon.m_nGameID.AppID().m_AppId == steam.AppId && pending.TryGetValue(icon.m_rgchAchievementName, out var a) && icon.m_bAchieved == a.Unlocked)
                Complete(a.ApiName, icon.m_nIconHandle);
        });
        foreach (var a in pending.Values.ToArray())
        {
            if (!steam.IsSameSession) return new(steam.AppId, false, "Steam account changed. Icon loading stopped.");
            var bytes = cache.Read(steam.AppId, a.ApiName, a.Unlocked);
            if (bytes is not null) { pending.Remove(a.ApiName); delivered++; ready(a with { IconPng = bytes }); continue; }
            int handle = SteamUserStats.GetAchievementIcon(a.ApiName); if (handle > 0) Complete(a.ApiName, handle);
        }
        var timer = Stopwatch.StartNew();
        while (pending.Count > 0 && timer.Elapsed < TimeSpan.FromSeconds(12) && steam.IsSameSession)
        {
            steam.RunCallbacks();
            foreach (string name in pending.Keys.ToArray()) { int handle = SteamUserStats.GetAchievementIcon(name); if (handle > 0) Complete(name, handle); }
            Thread.Sleep(100);
        }
        return steam.IsSameSession ? new(steam.AppId, true, $"{delivered}/{requested.Count} icons loaded") : new(steam.AppId, false, "Steam account changed. Icon loading stopped.");
    }
    public static async Task<string> LoadIconsAsync(uint appId, ulong account, string[] names, Action<AchievementInfo> ready, CancellationToken token)
    {
        var info = CreateWorkerInfo(appId);
        using var process = Process.Start(info) ?? throw new InvalidOperationException();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(90));
        using var abort = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var error = process.StandardError.ReadToEndAsync(); string status = "Icons unavailable";
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new DesktopRequest(account, "icons", names))); process.StandardInput.Close();
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                var result = JsonSerializer.Deserialize<DesktopResult>(line);
                if (result is null || result.AppId != appId) continue;
                if (result.Success && result.Status == "Icon ready" && result.Achievements is { } icons) foreach (var icon in icons) ready(icon);
                else status = result.Status;
            }
            await process.WaitForExitAsync(timeout.Token); await error; return status;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or JsonException) { return "Icon loading stopped"; }
        finally
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
        }
    }
    private static ProcessStartInfo CreateWorkerInfo(uint appId)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException())
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(info.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        info.ArgumentList.Add("--desktop-worker"); info.ArgumentList.Add(appId.ToString());
        return info;
    }

    public static async Task<DesktopResult> StartAsync(uint appId, DesktopRequest request)
    {
        var info = CreateWorkerInfo(appId);
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
