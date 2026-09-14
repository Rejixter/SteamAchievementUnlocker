using System;
using System.Collections.Generic;
using System.IO;
using Steamworks;

namespace SteamAchievementUnlocker;

/// <summary>
/// Thin wrapper around the parts of the Steamworks *client* SDK needed to read
/// and toggle achievements for a single AppID.
///
/// This talks to the Steam client already running on this machine, using the
/// same ISteamUserStats calls a game itself would make while you play it.
/// There is no public Steam Web API endpoint that lets a normal account do
/// this for an arbitrary game.
/// </summary>
public class SteamStatsManager : IDisposable
{
    public uint AppId { get; }
    public bool StatsLoaded { get; private set; }
    public bool StatsFailed { get; private set; }
    private EResult? _storeResult;

    private Callback<UserStatsReceived_t>? _statsReceivedCallback;
    private Callback<UserStatsStored_t>? _statsStoredCallback;
    private bool _initialized;

    public SteamStatsManager(uint appId)
    {
        AppId = appId;

        // Steam decides "which app is this process" from this file, read at
        // SteamAPI_Init() time. It has to sit next to the executable — NOT
        // wherever the process's current working directory happens to be
        // (those can differ, e.g. when launched from a debugger) — so we
        // resolve the path explicitly via AppContext.BaseDirectory.
        string appIdFilePath = Path.Combine(AppContext.BaseDirectory, "steam_appid.txt");
        File.WriteAllText(appIdFilePath, appId.ToString());
    }

    /// <summary>
    /// Connects to the running Steam client and kicks off the stats request.
    /// Returns false immediately if Steam isn't running, you're not logged
    /// in, or (on some setups) you don't own this AppID.
    /// </summary>
    public bool Init()
    {
        if (!Packsize.Test())
            Console.WriteLine("WARNING: Packsize.Test() failed — 32/64-bit mismatch between this build and the native Steamworks binary.");

        if (!DllCheck.Test())
            Console.WriteLine("WARNING: DllCheck.Test() failed — wrong native Steamworks binary for this platform/architecture.");

        try
        {
            if (!SteamAPI.Init()) return false;
            _initialized = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Console.WriteLine("Steam DLL could not be initialized. Re-extract the full Windows-x64 release ZIP, or rebuild with the matching SDK 1.57 DLL.");
            return false;
        }

        if (SteamUtils.GetAppID().m_AppId != AppId || !SteamUser.BLoggedOn() ||
            (!SteamApps.BIsSubscribed() && !SteamApps.BIsSubscribedFromFamilySharing()))
        {
            Console.WriteLine("Steam did not grant access to the requested game.");
            return false;
        }
        if (SteamApps.BIsSubscribedFromFamilySharing()) Console.WriteLine("Using Steam Family Sharing access.");
        _statsReceivedCallback = Callback<UserStatsReceived_t>.Create(OnUserStatsReceived);
        _statsStoredCallback = Callback<UserStatsStored_t>.Create(OnUserStatsStored);

        return SteamUserStats.RequestCurrentStats();
    }

    /// <summary>
    /// Must be called on a short loop (e.g. every ~100ms). Steamworks is
    /// callback-driven — nothing above fires unless you pump this yourself.
    /// Skipping this loop is the single most common reason "unlock" appears
    /// to do nothing.
    /// </summary>
    public void RunCallbacks() => SteamAPI.RunCallbacks();

    private void OnUserStatsReceived(UserStatsReceived_t cb)
    {
        if (cb.m_nGameID != AppId)
            return; // stats for some other app currently loaded — ignore

        if (cb.m_eResult != EResult.k_EResultOK)
        {
            StatsFailed = true;
            Console.WriteLine($"RequestCurrentStats failed: {cb.m_eResult}. " +
                               "Common causes: this account doesn't own the AppID, " +
                               "or Steam hasn't cached that game's stat schema yet " +
                               "(launching the real game once, even to the main menu, fixes this).");
            return;
        }

        StatsLoaded = true;
    }

    private void OnUserStatsStored(UserStatsStored_t cb)
    {
        if (cb.m_nGameID != AppId) return;

        _storeResult = cb.m_eResult;
        Console.WriteLine(cb.m_eResult == EResult.k_EResultOK
            ? "StoreStats confirmed by Steam's servers."
            : $"StoreStats reported: {cb.m_eResult}");
    }

    public IEnumerable<(string ApiName, string DisplayName, bool Unlocked)> ListAchievements()
    {
        uint count = SteamUserStats.GetNumAchievements();
        for (uint i = 0; i < count; i++)
        {
            string apiName = SteamUserStats.GetAchievementName(i);
            SteamUserStats.GetAchievement(apiName, out bool unlocked);
            string displayName = SteamUserStats.GetAchievementDisplayAttribute(apiName, "name");
            yield return (apiName, displayName, unlocked);
        }
    }

    /// <summary>
    /// The two-step call people usually get half-right: SetAchievement only
    /// flips a LOCAL flag in memory. Nothing reaches Steam (or your profile)
    /// until StoreStats() is called and succeeds.
    /// </summary>
    public bool Unlock(string apiName)
    {
        if (!StatsLoaded)
        {
            Console.WriteLine("Stats haven't finished loading yet — wait a moment and try again.");
            return false;
        }

        if (!SteamUserStats.SetAchievement(apiName))
        {
            Console.WriteLine($"SetAchievement('{apiName}') returned false — check the API name is exact (it's case-sensitive and often isn't the display name).");
            return false;
        }

        return StoreAndWait();
    }

    /// <summary>
    /// Sets every achievement locally first, then calls StoreStats() exactly
    /// once at the end — much friendlier to Steam's servers than calling
    /// Unlock() in a loop, which would StoreStats() after each individual one.
    /// </summary>
    public bool UnlockAll(IEnumerable<string> apiNames)
    {
        if (!StatsLoaded)
        {
            Console.WriteLine("Stats haven't finished loading yet — wait a moment and try again.");
            return false;
        }

        bool allSetOk = true;
        foreach (string apiName in apiNames)
        {
            if (!SteamUserStats.SetAchievement(apiName))
            {
                Console.WriteLine($"SetAchievement('{apiName}') returned false — skipping it.");
                allSetOk = false;
            }
        }

        bool stored = StoreAndWait();
        return allSetOk && stored;
    }

    private bool StoreAndWait()
    {
        _storeResult = null;
        if (!SteamUserStats.StoreStats()) return false;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (_storeResult is null && timer.Elapsed < TimeSpan.FromSeconds(20))
        { RunCallbacks(); System.Threading.Thread.Sleep(100); }
        if (_storeResult is null) Console.WriteLine("StoreStats confirmation timed out; outcome unknown.");
        return _storeResult == EResult.k_EResultOK;
    }

    public void Dispose()
    {
        _statsReceivedCallback?.Dispose();
        _statsStoredCallback?.Dispose();
        _statsReceivedCallback = null;
        _statsStoredCallback = null;
        if (_initialized) SteamAPI.Shutdown();
        _initialized = false;
        StatsLoaded = false;
    }
}
