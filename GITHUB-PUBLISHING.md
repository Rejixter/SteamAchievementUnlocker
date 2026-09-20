# Publishing a release

The ready-to-run application is **SteamAchievementUnlocker-win-x64.zip**.
GitHub's **Code → Download ZIP** contains source code instead.

## Validate and package

Use Windows and the .NET 8 SDK:

```powershell
dotnet run --project tests/SteamAchievementUnlocker.Tests.csproj -c Release
./scripts/Publish-Release.ps1
```

The script creates a self-contained Windows archive, an allowlisted source archive,
and SHA256SUMS.txt in `artifacts/`. It runs native DLL, desktop UI and worker protocol
smoke tests without initializing a Steam account or changing achievements.

Review the source and archives before publishing. Keep personal API keys, Steam
account files, progress/exclusion settings, local batch reports, build caches,
debug symbols and private credentials out of the repository and release assets.
`.gitignore` does not remove files already tracked in Git history.

## Publish on GitHub

1. Merge the tested version change into the default branch.
2. Open **Releases → Draft a new release** and create a new version tag, such as `v1.4.0`.
3. Write the release notes in English and publish. GitHub Actions tests the source,
   builds the application and attaches the Windows ZIP, source ZIP and checksums.
4. Wait for all release workflows to finish, then download and verify the assets.

Alternatively, push a new version tag to trigger automatic release creation.
Use a new tag for each version. Both tag pushes and published releases trigger the
workflow; wait for both runs when creating a tag through the release page.
The workflow uses GitHub's temporary token. No personal access token or Steam API
key belongs in the workflow configuration.

End users only need the entire Windows ZIP and the Steam client. They do not need
the .NET SDK or a separate .NET runtime installation.
