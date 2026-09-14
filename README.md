# Steam Achievement Unlocker v1.2.0

A Windows console app for viewing and unlocking Steam achievements for games in your library.

## Download and run 

1. Open this repository's **Releases** section and download **SteamAchievementUnlocker-win-x64.zip**.
2. Extract the **entire ZIP** into a folder. Do not launch the EXE from inside the ZIP viewer.
3. Open Steam and sign in, then run **SteamAchievementUnlocker.exe**.

**No .NET installation and no API key are required to start.** Windows x64 is required.
The EXE, DLLs and runtime files must stay together. GitHub's green **Code → Download ZIP**
downloads source code; use the Windows ZIP under **Releases** for the ready-to-run app.

## New in v1.2.0 / Yenilikler

- Main menu `all`: unlock locked achievements across **all games currently listed**, with one `UNLOCK ALL` confirmation. Check the displayed list first. Each game uses a fresh worker process; failed games do not stop the queue. Press `Q` to stop after the current game. Already completed games and games with no achievements are skipped.
- Main menu `s`: include additional local library-cache candidates, including cached family-shared games that are not installed. Run the Steam library first, then refresh with `r`. This is best-effort discovery, not a complete Steam Families API integration. Shared cache entries may belong to another account, a removed game, or a non-game app; they are marked **cached: access unverified**. Uncached games can still be entered by AppID using `0`. Names load during the metadata scan; unresolved names stay as AppIDs.
- Each game session checks the requested AppID, logged-in account, and subscription/family access reported by Steam. It does not require direct ownership when Steam grants Family Sharing access. Games blocked by Steam are reported as unavailable.
- A save is successful only after Steam's `UserStatsStored_t` OK callback. Timeouts are **unconfirmed**, never success; some changes may still have reached Steam.
- Batch results are displayed and saved to `%LOCALAPPDATA%\SteamAchievementUnlocker\batch-*.json`. Reports stay on your computer and are excluded from release packages.

Türkçe: Ana menüde `s` ile aile oyunları dahil önbellekteki ek oyunları listele.
Listeyi kontrol edip `all` yaz; ardından `UNLOCK ALL` ile toplu işlemi başlat.
`Q`, mevcut oyun tamamlanınca işlemi durdurur. Sonuçlar her oyun için ayrı raporlanır.
Aile desteği Steam'in verdiği erişime ve yerel önbelleğe bağlıdır; tüm aile oyunlarının
bulunacağı veya her oyunun başarım kaydını kabul edeceği garanti edilmez.

References: [Steam access and Family Sharing](https://partner.steamgames.com/doc/api/ISteamApps),
[Steam stats and save callbacks](https://partner.steamgames.com/doc/api/ISteamUserStats).

## Achievement filter

Games that Store metadata reports as having no Steam achievements are hidden by default.
This checks whether the game has achievements at all, **not** whether you have already unlocked them.
Games with all achievements unlocked remain listed.

- `f`: show all games / turn the filter back on.
- `r`: refresh the library and continue checking unresolved games.
- `c`: clear cached metadata and check again.
- `0`: enter an AppID directly, including games hidden by the filter.
- `s`: include/exclude cached library and family candidates.
- `all`: process all currently listed games after confirmation.
- `k`: add or remove your own optional API key.
- `q`: quit.

The first scan needs internet access and can take a few minutes for large libraries.
Press **Esc** to skip it. Each pass has a 90-second limit; press `r` to continue.
Successful checks are cached for seven days; failed checks retry after ten minutes.
Games with missing metadata, delisted Store pages, failed requests or no completed check
remain visible with **[?]**. If Steam limits requests, the scan stops and preserves unresolved games.
The exact number removed depends on your library; this is a best-effort metadata filter.
The public Store endpoint is not a versioned Steamworks API and may change.

Cache location: `%LOCALAPPDATA%\SteamAchievementUnlocker\achievement-cache.json`.
Loading a game's actual Steam achievement list updates its cached result.

## Optional: full owned library

Without a key the app discovers installed games from local Steam library manifests.
For owned but uninstalled games, get **your own** key at https://steamcommunity.com/dev/apikey
and press `k` in the menu. Key entry is hidden. Empty input removes the saved key;
Esc cancels. If the Web API cannot return your library, the app falls back to installed games.
The account is detected from Steam's local login metadata; with multiple accounts, sign into
the intended account in Steam first.

The key is stored as a local text file at:
`%LOCALAPPDATA%\SteamAchievementUnlocker\web_api_key.txt`.
It is sent only to Steam's Web API to request your owned games. It is not encrypted against
other processes running as your Windows user. No key is shared with other users or included
in the repository or release ZIPs. Legacy key files beside the EXE are no longer loaded;
enter your key once through `k` instead. Do not distribute your user settings folder.

## Using achievements

Choose a game, wait for its stats, then select a numbered achievement or type `all`.
Unlocking all requires confirmation. `back` closes the game's worker and returns to the existing library menu.
Steam must be running and the account must have access to the selected game.
Some games manage achievements on their own servers or impose additional restrictions.
Only Steam's asynchronous StoreStats confirmation reports whether a submitted update succeeded.
Changes affect your Steam profile; select achievements deliberately.

## Build from source

Install the .NET 8 SDK on Windows, then run in this folder:

```powershell
dotnet restore -r win-x64 --locked-mode
dotnet build -c Release -r win-x64 --no-restore
dotnet run --project tests/SteamAchievementUnlocker.Tests.csproj -c Release
./scripts/Publish-Release.ps1
```

The script produces the self-contained Windows ZIP, a clean source ZIP and SHA-256
checksums under `artifacts/`. It validates the native DLL, runs an account-free smoke test,
checks for personal configuration files and builds the source archive from an explicit allowlist.
The project only publishes explicitly allowed content files.

**Keep Steamworks.NET 20.2.0 and the included SDK 1.57 DLL together.**
Replacing the native DLL with a random game's or newer SDK's DLL can cause
`EntryPointNotFoundException: SteamAPI_Init`. The packaging script rejects a mismatched DLL.

## Troubleshooting

- **Missing DLL / runtime files:** extract the full Windows ZIP, keeping all files together.
- **SteamAPI_Init missing:** use the matched native DLL from this release.
- **Connection fails:** check Steam login and ownership/access to the selected game.
- **Stats time out:** try starting the actual game once, then retry.
- **A game is unexpectedly hidden:** press `f`; use `c` to discard cached metadata.
- **Only installed games appear:** configure your own optional API key using `k`.

For repository and Release setup, see [GITHUB-PUBLISHING.md](GITHUB-PUBLISHING.md).
See [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for component notices.
