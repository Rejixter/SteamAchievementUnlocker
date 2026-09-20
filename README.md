# Steam Achievement Unlocker v1.4.0

A portable Windows desktop application for browsing Steam games, reading achievement
progress and unlocking selected achievements. The interface and documentation are in English.

## Download and run

1. Open this repository's **Releases** section and download **SteamAchievementUnlocker-win-x64.zip**.
2. Extract the **entire ZIP** to a writable folder. Keep the EXE, DLLs and runtime files together.
3. Open Steam, sign in and run **SteamAchievementUnlocker.exe**. A desktop window opens; no console commands are needed.

Windows x64 is required. No separate .NET installation or API key is required to start.
GitHub's **Code → Download ZIP** contains source code, not the ready-to-run application.

## New in v1.4.0

- **Desktop interface:** a Windows Forms application replaces the default console menu.
- **Search:** filter the active list by game name or AppID.
- **Multiple selection:** check individual games or use **Select visible** to choose a batch.
- **Achievement progress:** **Read progress** displays counts such as `18/25 (72%)`. **Hide 100% complete** hides games with fresh, fully completed progress.
- **Excluded games:** **Exclude selected** keeps games out of both library lists. Restore them from the **Excluded games** tab.

## Library tabs and selection

**Main library** contains installed games and, with an optional API key, owned games
that are not installed. An installed shared game can already appear in this tab.

**Family / cached** contains additional candidates discovered in Steam's local library
cache. Games already in the main library are excluded from this list, including games
hidden by a filter. Cache entries are not proof of family ownership or current access:
they may be stale, associated with another account, or refer to a non-game app.
This is best-effort discovery, not a complete Steam Families API integration.

**Excluded games** lists games you have hidden. Search works here, while the achievement
filters are ignored so excluded games remain restorable. Restore a game before processing it.

Search and filters only affect the active tab. Selections that become hidden are cleared;
switching tabs clears selection. **Unlock selected** only processes checked games in the
current visible list. Use **Select visible** after applying your filters to process that list.
**Clear selection** unchecks the current selection.

**Add AppID** adds a game for the current session. **Refresh library** reloads discovery
and the detected Steam account; manually added entries may need to be entered again.

## Read progress and use filters

1. Check the games you want to inspect, or click **Select visible**.
2. Click **Read progress**. Each game is read in a separate process, sequentially.
3. Turn on **Hide 100% complete** to hide games whose checked progress is complete.

Reading progress initializes a Steam game session and may show you as in-game on Steam.
It does not unlock achievements. Counts are cached locally for the detected account for
24 hours; read them again after playing or changing achievements elsewhere. Expired,
missing or failed progress reads show **Not checked** and never hide a game as complete.
Games with zero achievements are not treated as 100% completed games.
Refresh the library after switching Steam accounts. Workers reject a different logged-in
account before submitting achievement changes.

**Hide games without achievements** is a different filter: it checks whether a game
supports achievements at all. Use **Check achievement support** to fetch Store metadata
for the current visible list and resolve cached game names. Each pass is limited to
90 seconds; run it again to continue. Known checks last seven days, failed checks retry
after ten minutes, and unknown games stay visible. The Store endpoint is not a versioned
Steamworks API and may change. Reading a game's actual achievements also updates this filter.

## Unlock achievements

**Unlock selected** opens a confirmation window listing the selected games. Type
`UNLOCK ALL` to start. Each game runs separately; failures are reported and the queue
continues. **Stop after current game** stops before starting the next game. Closing the
window during an operation can stop after the current operation and then close.

For individual achievements, select a game row and click **Open achievements**, or
double-click the row. The window shows its unlocked count and a checklist of remaining
locked achievements. Check the ones you want and click **Unlock selected**; confirmation
is required. **Select all locked** selects all remaining achievements in that game.

A successful save requires Steam's asynchronous `UserStatsStored_t` OK callback.
Failed or timed-out saves remain **unconfirmed**; read progress again before retrying.
Steam must grant access to each game. Some games manage achievements on their own servers
or impose additional restrictions, so successful changes are not guaranteed.
Tests do not validate live-account unlocking or every shared game.

Batch results are displayed and written locally to `batch-*.json`. There is no automatic
resume feature in this version. Confirmed changes affect your Steam profile.

## Optional: full owned library

Without an API key, the main list uses installed-game manifests. For owned, uninstalled
games, get **your own** key from [Steam](https://steamcommunity.com/dev/apikey) and open
**Settings**. Entry is masked; saving an empty value removes the key. Cancel keeps existing
settings. If the request fails, the app falls back to installed games.

The account is detected from Steam's local login metadata. Sign in to the intended Steam
account and refresh before working with it.

## Local data and privacy

Settings live under `%LOCALAPPDATA%\SteamAchievementUnlocker\`:

- `web_api_key.txt`: your optional key, sent only to Steam's Web API for owned-game discovery.
- `achievement-cache.json`: Store support metadata and names.
- `library-state-*.json`: account-separated progress and excluded games.
- `batch-*.json`: local batch results.

The key is stored as text, not encrypted against other processes running as your Windows
user. These settings, reports and credentials are excluded from source and release
archives. Do not distribute your settings folder. No API key is bundled with the app.
Demo mode uses sample data without reading an account or writing user settings.

## Build and test

Install the .NET 8 SDK on Windows:

```powershell
dotnet restore -r win-x64 --locked-mode
dotnet build -c Release -r win-x64 --no-restore
dotnet run --project tests/SteamAchievementUnlocker.Tests.csproj -c Release
./scripts/Publish-Release.ps1
```

The release script creates self-contained Windows/source ZIPs and SHA-256 checksums
under `artifacts/`, runs native DLL, desktop UI and worker protocol smoke tests,
and rejects personal configuration in the staging folder.

The EXE also accepts `--demo` for an account-free sample library, `--ui-self-test` for
desktop behavior checks, and `--worker-self-test` for the account-free worker protocol test.
The previous console interface remains available with `--console`; new desktop features
are accessed through the graphical interface.

**Keep Steamworks.NET 20.2.0 and the included SDK 1.57 DLL together.** Replacing the native
DLL with an arbitrary game's or newer SDK's DLL can cause missing entry-point errors.

## Troubleshooting

- **Missing DLL/runtime:** extract the full Windows ZIP and keep all files together.
- **No Steam access:** check Steam login and access to the selected game.
- **Stats unavailable:** try starting the actual game once, then read progress again.
- **Missing game:** check search, both filters and Excluded games; try Add AppID.
- **Family game missing:** open your Steam library and refresh. Uncached games may not be discovered.
- **Counts look old:** select the game and read progress again; refresh after switching accounts.
- **Settings do not persist:** check write access to your Windows user settings folder.

See [GITHUB-PUBLISHING.md](GITHUB-PUBLISHING.md), [LICENSE](LICENSE) and
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
Steam references: [access and Family Sharing](https://partner.steamgames.com/doc/api/ISteamApps),
[stats and save callbacks](https://partner.steamgames.com/doc/api/ISteamUserStats).
