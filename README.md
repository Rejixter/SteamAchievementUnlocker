# Steam Achievement Unlocker v2.0.0

A portable Windows desktop application for browsing Steam games, reading achievement
progress and unlocking selected achievements. The interface and documentation are in English.

## Version 2.0.0

This release brings together the improvements developed after v1.4.0. The version change itself does not change achievement behavior or reset user data.

- Library-wide progress scanning, account-specific saved results, favorites and filtered views.
- Windows user-protected API keys, separate saved/verified states and account checks around Steam writes.
- Operation history, checkpoint recovery and last-readable settings backups.
- Persistent game covers, download controls, game details, Steam global rarity and a replayable guided tour.
- A true black desktop theme, consistent column headers and keyboard accessibility.

Windows x64 only. This is a portable ZIP release, not an installer. macOS and Linux are not supported. Extract the full archive and keep `lib`, `docs` and `licenses` beside the EXE. No personal API key or user settings are included.

## Development history: v1.11.3 progress scan

**Scan library progress** in Game actions reads the active Main library or Family / cached library without requiring row selections or opening individual achievement windows. Choose unchecked/expired games or a full rescan, then review the batch. Search and progress/support filters do not restrict the scan; excluded games are skipped. The other library is not scanned implicitly.

Results are saved and filters refresh after each game, including selected Read progress batches. Successful zero-achievement reads update achievement support; failed reads stay unknown, never zero. Stop after the current game, or review Resume pending after an interruption. Scans only send read requests; no achievements are changed. Steam must be running and may show you as in-game. Large libraries can take a long time, and family/cache candidates may be inaccessible.

## Development history: v1.11.2 visual fixes

- Column headers retain their theme colors when cells or columns are clicked; Windows blue selection no longer colors the active column header in either game list or achievement browser.
- Dark mode now uses a true black background. Tabs, collapsed dropdowns, input borders and the progress track render with the same palette instead of white native surfaces. Disabled buttons retain readable gray text. Light mode remains available.
- Windows dark title bars and native scrollbars are requested where supported by the OS. Tab keyboard navigation and existing achievement selection behavior remain unchanged.

## Development history: v1.11.1 reliability patch

- Steam session identity is checked before each achievement mutation, before StoreStats, during confirmation, and before reporting success. Stats callbacks must match the captured account. An account change stops the batch; a potentially completed write is recorded as uncertain.
- Refresh invalidates the previous session, selections, retry list, results, details and journal binding before loading another account. A failed refresh cannot leave an actionable stale account behind. Operations require a fully loaded session.
- Operation journals now carry an account identity and schema version. Journals from another account or a newer version are protected from use/overwrite. Legacy journals are bound when loaded. Last-readable backups and recovery notices remain available.
- Acceptance tests use fresh temporary settings and synthetic accounts only. Process-crash tests forcibly terminate a separate test process before work, during work, and after a simulated remote confirmation. These tests never initialize Steam or change real achievements.

### Reproducible local checks

Run the account-free suite with `dotnet run --project tests/SteamAchievementUnlocker.Tests.csproj -c Release`.
The packaged EXE supports `--ui-self-test`, `--worker-self-test` and `--acceptance-self-test`; the latter creates a fresh temporary settings scope and removes it after testing.
From the source package, run `./scripts/Validate-Portable.ps1 -AppDirectory <absolute-extracted-app-folder>` to check the bundled runtime with an unrelated working directory, isolated temporary/app-data folders, and no .NET runtime on PATH.

These are synthetic account tests and process-isolation checks on the current Windows installation. They are not evidence of successful live account switching, a clean Windows VM, or real OS scaling changes. Real Steam operations and real Windows 150–200% display scaling remain separate acceptance checks. Do not run achievement changes merely to validate packaging. DPAPI protects saved keys for the Windows user; it does not protect against malware already running as that user.

## Development history: v1.11.0 update

- **Game details:** a collapsible panel follows the selected row, showing cached cover, progress, source, last operation result and the three rarest known achievements. Public rarity loads only when requested; use Read progress to obtain achievement names. The panel never initiates Steam achievement reads or changes automatically. Toggle it in Library & history.
- **Library changes:** an account-specific baseline records added games and retains entries not found in a later complete owned-library scan. Open Library changes to inspect them. Partial/offline scans never infer that a game is missing; not found is not proof of lost ownership. The first scan establishes a baseline.
- **Keyboard/accessibility:** Space toggles the current game's selection (or Favorite in its column), arrow keys move inside the grid and Tab leaves it. The focused grid cell has a visible blue outline. Ctrl+F, F5 and F1 remain available. Disable Animations for reduced motion, including static rare-achievement borders.
- **Private diagnostics:** Settings offers an exact read-only report preview before an optional local save. Only app/runtime version, bitness, theme, counts and state flags are included. Keys, Steam IDs, user/machine names, paths, game names, raw logs and exception messages are never supplied to the report builder. Nothing is uploaded automatically.
- **Safer settings:** display and per-account library preferences now have a schema version, a readable backup and recovery notices. Legacy settings retain their values when upgraded. Rejected primaries are preserved before repair. Future versions are never overwritten; Steam operations are blocked if newer library preferences cannot be interpreted safely. Explicit reset actions keep exact preserved copies first. Resetting library preferences affects favorites, exclusions and cached counts, not Steam achievements or operation history.

Inventory, preference backups and preserved files stay in your Windows user settings folder and are excluded from release archives. A display reset closes the app so the new defaults apply on the next launch.

## Development history: v1.10.0 update

- **First-run guided tour:** 11 English steps highlight real controls and explain library sources, account checks, selection, achievement reads/changes, covers, history and protected keys. Next/Back and Skip (Esc) are available. The owner window cannot start actions during the tour. Finish or skip once; replay from Settings, Library & history, or F1. This also appears once after updating from a version without the tour.
- **Session details:** Settings identifies the account found in local Steam settings, library sources and last load time. Local account detection is not a live connection check; each achievement operation still checks account/access through Steam.
- **Large libraries:** the game grid uses virtual rows and only enumerates displayed rows for artwork. Search waits 220 ms after typing, and pending search is applied before actions so hidden selections never enter a batch.
- **Actionable failures:** installation, account changes, Steam access, unavailable stats, busy sessions and uncertain writes have different guidance. Retry failed contains only recognized recoverable failures in the visible list; retries are always manual. Unlock retries require a fresh successful progress read. Unknown or unconfirmed writes are not blindly retried.
- **Favorites and remembered views:** toggle Favorite in a row or its context menu. In Library & history, choose All games, Favorites, Started/unfinished, or Almost complete (five or fewer left). Progress-based views require fresh counts. Favorites are account-specific. View, search, filters, sorting and an on-screen window position are remembered.
- **Cover cache tools:** Settings shows image count, disk usage and the most recent save time. Clear only cover images/missing-result records; downloads stay paused afterward. Right-click a game to reset/redownload one cover. Neither action touches credentials or operation history.
- **Offline library:** after a successful owned-library request, an account-specific local snapshot lets you browse those entries when the Web API is unavailable. A persistent banner and Source labels mark snapshot entries as stale. Cached artwork works offline. Account access and successful saves still require Steam; snapshots never prove ownership.

The offline snapshot contains game metadata, not an API key. Removing a saved key does not delete local library metadata. All settings, snapshots, favorites and caches stay outside release ZIPs.

## Development history: v1.9.0 update

- **API key security:** Windows current-user DPAPI storage; encrypted save/readback must succeed before replacing a key or deleting legacy plaintext. Failed migration never uses the plaintext key. Leftover legacy files are cleaned up only after successfully reading the protected key; cleanup failures are reported.
- **Saved versus verified:** Settings separately shows storage and Steam verification for this session. Keys are masked, never prefilled, and removed through an explicit action. Verification happens only after a successful authenticated library response.
- **Network handling:** the key is sent in the `x-webapi-key` header to Steam over HTTPS, never in the URL. Credential requests do not follow redirects or use cookies. Errors do not expose request details or keys.
- **Recovery:** history and pending queues keep a last-good backup. Recovery displays a notice; recovered or interrupted unlock queues require fresh successful progress reads before retrying. Nothing resumes automatically.
- **Cover downloads:** unavailable artwork is remembered for seven days across restarts; transient errors use shorter cooldowns. Pause/resume downloads independently, and see cached/unavailable counts. Cached covers remain visible while paused.
- **Navigation:** separate Library, History and Settings pages, a collapsible activity log, and scrollable layouts for smaller windows and higher scaling.

## Development history: v1.8.0 update

- Rare achievements with a known Steam global rate of **5% or less** have neon borders: cyan above 1%, purple at 1% or below. A real 0% qualifies; unknown rates do not. The 2.4-second glow cycle never flashes on/off. Disable **Animations** for a static border. **Rare only** filters the achievement browser.
- Game actions are grouped separately from **Library & history** tools.
- Operation history keeps an atomic `.bak` copy. If the primary is unreadable, the backup is restored with a visible notice. If neither copy is usable, the app warns instead of silently resuming an empty queue.
- New API keys use Windows DPAPI protection for the current Windows user. Legacy plaintext keys migrate after an encrypted save/readback succeeds; only then is the legacy copy removed. If migration cannot finish, the legacy file is kept and Settings reports that migration is pending. New saves never fall back to plaintext. Older versions cannot read the protected format; re-enter the key if returning to one.
- A copied encrypted key file is not a portable credential backup. Re-enter your key on another Windows account or computer. The application must briefly decrypt the key to send an authorized Steam request; local encryption is not protection against software already acting as your Windows user.

## Development history: v1.7.0 update

- **Batch review:** both progress reads and unlock batches show the exact games before starting. Uncheck games to remove them. Locked counts are labeled as cached or unknown; unlock batches still require `UNLOCK ALL`.
- **History:** search the last 2,000 account-specific entries. Read, Confirmed, No changes, Failed, Uncertain, and Stopped are distinct outcomes. No API keys are recorded.
- **Achievement browser:** descriptions and icons (when available from Steam), name/description/API-name search, Locked/Unlocked/All filters, selection count, and rarest-first sorting. Hidden selections are removed from the action.
- **Steam global unlock rates:** the public global-percentages API is requested only when opening a game's achievements. Values are cached for 24 hours. Missing data displays Unavailable; a real zero stays 0.0%. This statistic is separate from your personal progress.
- **Library overview:** fresh completed, incomplete, not-checked/expired, and zero-achievement counts. Unknown data is never counted as zero progress. Excluded games are omitted; family/cache entries remain unverified candidates.
- **Resume pending:** account-specific checkpoints are saved before each game request. Remaining games survive restart and require another review/confirmation. An interrupted active game is marked Uncertain and its progress must be read successfully before an unlock retry. Excluded and missing-library games are not resumed. Nothing resumes automatically.

History and pending batches live in your Windows user settings as `operations-<account-hash>.json`. Public global-rate cache lives in `global-achievements`. These files are excluded from release archives. Keep the settings folder writable; a batch cannot start without saving its recovery checkpoint.

## Download and run

1. Open [Releases](https://github.com/Rejixter/SteamAchievementUnlocker/releases) and download **SteamAchievementUnlocker-win-x64.zip**.
2. Extract the **entire ZIP** to a writable folder. Keep `lib`, `docs`, and `licenses` beside the EXE.
3. Open Steam, sign in and run **SteamAchievementUnlocker.exe**. A desktop window opens; no console commands are needed.

Windows x64 is required. No separate .NET installation or API key is required to start.
The application and .NET runtime are bundled in the EXE. Native runtime components are
extracted automatically into the per-user .NET temporary cache on first launch.
The root folder contains only `SteamAchievementUnlocker.exe`, `START-HERE.txt`,
`lib` (Steam library), `docs` (instructions), and `licenses` (license notices).
Publishing instructions are included only in the source package.
GitHub's **Code → Download ZIP** contains source code, not the ready-to-run application.

## Desktop features introduced in v1.4.0

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

- `web_api_key.dat`: your optional key, encrypted for the current Windows user and decrypted only when needed for owned-game discovery. Legacy `web_api_key.txt` migrates when protection succeeds.
- `achievement-cache.json`: Store support metadata and names.
- `library-state-*.json`: account-separated progress and excluded games.
- `batch-*.json`: local batch results.

The protected key is bound to your Windows user account. Software acting as the same
Windows user may still access it. These settings, reports and credentials are excluded from source and release
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

The Windows package includes license notices in `../licenses/`.
Source packages also include `GITHUB-PUBLISHING.md`, `LICENSE` and `THIRD-PARTY-NOTICES.md`.
Steam references: [access and Family Sharing](https://partner.steamgames.com/doc/api/ISteamApps),
[stats and save callbacks](https://partner.steamgames.com/doc/api/ISteamUserStats).
## Development history: v1.7.0 quality-of-life update

### Steam artwork and motion

**Show covers** displays Steam Store header artwork next to each game. On library load,
all main-library and family/cache games that are not excluded are queued for background
download, including games hidden by search or progress filters. Visible games have
priority. Existing thumbnails load from disk immediately, without downloading again.
Downloads are sequential and use public App IDs, with no API key or account identifier.
Missing images never block game selection. Steam rate limits or connection failures
pause the queue; use Refresh library to retry after the cooldown. Closing the app stops
downloads; the next launch resumes missing images.

Thumbnails are retained in your user settings folder (`artwork-cache`) across restarts,
including libraries larger than 1,000 games. Only a bounded number of decoded images is
kept in memory. The Covers counter shows cached images for the current library. Turn off
Show covers to pause background downloads. Artwork is excluded from release archives.

**Animations** controls the short window fade, cover fade-in, and animated loading
placeholders. Disable it for a static interface. Both display options are remembered.

This local build adds Dark (default), Light, and System themes. The System theme follows Windows when the app starts or regains focus. Windows-managed dialogs may retain the operating system's appearance.

Use the progress filter for **Not checked**, **Incomplete**, or **Completed** games. Expired counts are treated as not checked. Sort by name, completion percentage, or remaining achievements; unknown counts stay last in progress sorts. The Excluded games tab ignores progress filters so games remain restorable.

The app remembers theme, search, filters, sort, window size/maximized state, and column proportions when closed. These preferences stay in your user settings folder and are excluded from packages. Selections and retry queues are session-only.

Right-click a game to open achievements, exclude/restore it, open its Steam store page, or copy its App ID. **Ctrl+F** focuses search; **F5** refreshes the library.

**Retry failed** repeats the last batch operation only for attempted failures visible in the current tab and account. Hidden, excluded, successful, and unprocessed games are not included. Unlock retries still require confirmation. Clear filters or change tabs to see other failed games.
