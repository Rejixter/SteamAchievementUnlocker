using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace SteamAchievementUnlocker;

public record BatchResult(uint AppId, string Name, string Status);

public static class BatchRunner
{
    public static string Status(int exitCode) => exitCode switch
    {
        0 => "Confirmed", 2 => "Already complete", 3 => "No achievements",
        4 => "No access / connection failed", 5 => "Stats unavailable",
        6 => "Save failed or unconfirmed", 7 => "Worker timed out; outcome unknown",
        _ => "Worker failed; outcome unknown"
    };

    public static async Task<List<BatchResult>> RunAsync(IEnumerable<SteamGame> games,
        Func<SteamGame, Task<int>> run, Func<bool> stop, Action<BatchResult> report)
    {
        var results = new List<BatchResult>();
        foreach (var game in games.DistinctBy(g => g.AppId))
        {
            if (stop()) break;
            int code;
            try { code = await run(game); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { code = 8; }
            var result = new BatchResult(game.AppId, game.Name, Status(code));
            results.Add(result);
            report(result);
        }
        return results;
    }

    public static ProcessStartInfo CreateStartInfo(uint appId, string gameName, bool bulk)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("No executable path."))
        {
            UseShellExecute = false,
            // Inherit the parent's console so interactive workers can read keys.
            CreateNoWindow = false,
            WorkingDirectory = AppContext.BaseDirectory
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(info.FileName), "dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        info.ArgumentList.Add(bulk ? "--bulk-worker" : "--game-worker");
        info.ArgumentList.Add(appId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        info.ArgumentList.Add(gameName);
        return info;
    }

    public static async Task<int> StartWorkerAsync(SteamGame game, bool bulk)
    {
        using var process = Process.Start(CreateStartInfo(game.AppId, game.Name, bulk))
            ?? throw new InvalidOperationException("Could not start game worker.");
        using var timeout = new CancellationTokenSource();
        if (bulk) timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            return 7;
        }
        return process.ExitCode;
    }

    public static string SaveReport(IReadOnlyList<BatchResult> results)
    {
        Directory.CreateDirectory(UserSettings.DirectoryPath);
        string path = Path.Combine(UserSettings.DirectoryPath, $"batch-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }
}
