param(
    [string]$OutputDirectory = "",
    [string]$RestoreConfig = ""
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'artifacts' }
$destination = [IO.Path]::GetFullPath($OutputDirectory)
$stagingRoot = Join-Path $repoRoot 'artifacts'
New-Item -ItemType Directory -Force -Path $destination,$stagingRoot | Out-Null
$stage = Join-Path $stagingRoot ('staging-' + [guid]::NewGuid().ToString('N'))
$publishDirectory = Join-Path $stage 'SteamAchievementUnlocker-win-x64'
$sourceDirectory = Join-Path $stage 'SteamAchievementUnlocker-source'
New-Item -ItemType Directory -Force -Path $publishDirectory,$sourceDirectory | Out-Null
Push-Location $repoRoot
try {
    $nativeHash = (Get-FileHash -LiteralPath 'lib\steam_api64.dll' -Algorithm SHA256).Hash
    if ($nativeHash -ne 'A44E5537939AE4EEBC69000589AA9B2437A667813A1657CC779198BAE9B815A9') {
        throw 'Wrong native DLL. Expected Windows-x64 from Steamworks.NET Standalone 20.2.0 (SDK 1.57).'
    }
    $restoreArgs = @('restore','SteamAchievementUnlocker.csproj','-r','win-x64','--locked-mode','-p:PublishSingleFile=true','-p:EnableSingleFileAnalyzer=false')
    if ($RestoreConfig) { $restoreArgs += @('--configfile', [IO.Path]::GetFullPath($RestoreConfig)) }
    & dotnet @restoreArgs
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    $buildDirectory = Join-Path $stage 'build'
    & dotnet publish 'SteamAchievementUnlocker.csproj' -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:EnableSingleFileAnalyzer=false -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false "-p:OutputPath=$buildDirectory/" -o $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    $docs = Join-Path $publishDirectory 'docs'
    $licenses = Join-Path $publishDirectory 'licenses'
    New-Item -ItemType Directory -Force -Path $docs,$licenses | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $docs
    foreach ($file in @('LICENSE','THIRD-PARTY-NOTICES.md','lib\Steamworks.NET.LICENSE.txt','lib\DOTNET-LICENSE.txt','lib\DOTNET-THIRD-PARTY-NOTICES.txt')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot $file) -Destination $licenses
    }
    @'
Steam Achievement Unlocker

1. Extract this entire folder.
2. Start Steam and sign in.
3. Run SteamAchievementUnlocker.exe.

Keep lib, docs and licenses beside the EXE. Do not move the EXE on its own.
No separate .NET installation is required. The runtime is bundled in the EXE;
Windows extracts native runtime components to its per-user temporary cache.

lib       Required Steam client library.
docs      Full instructions.
licenses  Application and third-party license notices.

Personal settings stay in your Windows user profile.
'@ | Set-Content -LiteralPath (Join-Path $publishDirectory 'START-HERE.txt') -Encoding utf8
    $expectedRoot = @('SteamAchievementUnlocker.exe','START-HERE.txt','lib','docs','licenses')
    $unexpected = Get-ChildItem -LiteralPath $publishDirectory -Force | Where-Object { $_.Name -notin $expectedRoot }
    if ($unexpected) { throw 'Unexpected files in application root. Packaging layout needs review.' }
    # Isolate smoke-test extraction from caches owned by other build accounts.
    $previousBundleCache = $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $stage 'runtime-cache'
    try {
    foreach ($check in @('--self-test', '--ui-self-test', '--worker-self-test', '--acceptance-self-test')) {
        Write-Host "Running smoke test: $check"
        $testOutput = Join-Path $stage ($check.TrimStart('-') + '.out.log')
        $testError = Join-Path $stage ($check.TrimStart('-') + '.err.log')
        $testProcess = Start-Process -FilePath (Join-Path $publishDirectory 'SteamAchievementUnlocker.exe') -ArgumentList $check -PassThru -WindowStyle Hidden -RedirectStandardOutput $testOutput -RedirectStandardError $testError
        if (!$testProcess.WaitForExit(60000)) {
            $testProcess.Kill()
            throw "Release smoke test timed out: $check"
        }
        if ($testProcess.ExitCode -ne 0) {
            Get-Content -LiteralPath $testOutput,$testError | Write-Host
            throw "Release smoke test failed: $check"
        }
    }
    }
    finally { $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $previousBundleCache }


    # Source archives use an explicit allowlist, not a ZIP of the working directory.
    $rootFiles = @('Program.cs','DesktopForm.cs','DesktopAcceptance.cs','SteamWriteGuard.cs','DesktopQol.cs','DesktopEnhancements.cs','SettingsFile.cs','LibraryInventory.cs','DiagnosticReport.cs','GuidedTour.cs','LibrarySnapshot.cs','SteamArtwork.cs','OperationJournal.cs','GlobalAchievements.cs','RarityStyle.cs','CredentialVault.cs','CredentialStore.cs','OwnedLibrary.cs','DesktopPreferences.cs','DesktopTheme.cs','ThemeControls.cs','AchievementForm.cs','DesktopWorker.cs','LibraryState.cs','BatchRunner.cs','AchievementCatalog.cs','SteamLibraryScanner.cs','SteamRuntime.cs','SteamStatsManager.cs','UserSettings.cs',
        'SteamAchievementUnlocker.csproj','packages.lock.json','global.json','NuGet.Config','.gitignore','README.md','LICENSE','THIRD-PARTY-NOTICES.md','GITHUB-PUBLISHING.md')
    foreach ($file in $rootFiles) { Copy-Item -LiteralPath (Join-Path $repoRoot $file) -Destination $sourceDirectory }
    foreach ($file in @('lib\steam_api64.dll','lib\Steamworks.NET.LICENSE.txt','lib\DOTNET-LICENSE.txt','lib\DOTNET-THIRD-PARTY-NOTICES.txt','scripts\Publish-Release.ps1','scripts\Validate-Portable.ps1','tests\Program.cs','tests\SteamAchievementUnlocker.Tests.csproj','.github\workflows\release.yml')) {
        $target = Join-Path $sourceDirectory $file
        New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
        Copy-Item -LiteralPath (Join-Path $repoRoot $file) -Destination $target
    }
    $forbidden = Get-ChildItem -LiteralPath $publishDirectory,$sourceDirectory -Recurse -Force -File | Where-Object {
        $_.Name -like 'library-snapshot-*' -or $_.Name -like 'operations-*' -or $_.Name -like 'desktop-preferences.json*' -or $_.Name -like 'library-inventory-*' -or $_.Name -like 'SteamAchievementUnlocker-diagnostic*' -or $_.Name -like 'web_api_key*' -or $_.Name -eq 'steam_appid.txt' -or $_.Name -like 'batch-*.json' -or $_.Name -like 'library-state-*' -or $_.Name -like 'achievement-cache*' -or $_.Name -like '.env*' -or $_.Extension -in '.pfx','.pem'
    }
    if ($forbidden) { throw 'Personal configuration detected in release staging.' }
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $buildDirectory 'SteamAchievementUnlocker.runtimeconfig.json') -Raw | ConvertFrom-Json
    if ($runtimeConfig.runtimeOptions.framework -or $runtimeConfig.runtimeOptions.frameworks -or !(Test-Path -LiteralPath (Join-Path $publishDirectory 'lib\steam_api64.dll'))) {
        throw 'The output is not self-contained.'
    }
    $windowsZip = Join-Path $destination 'SteamAchievementUnlocker-win-x64.zip'
    $sourceZip = Join-Path $destination 'SteamAchievementUnlocker-source.zip'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    foreach ($archive in @(@($publishDirectory,$windowsZip),@($sourceDirectory,$sourceZip))) {
        if (Test-Path -LiteralPath $archive[1]) { Remove-Item -LiteralPath $archive[1] }
        [IO.Compression.ZipFile]::CreateFromDirectory($archive[0],$archive[1],[IO.Compression.CompressionLevel]::Optimal,$true)
    }
    $checksums = foreach ($zip in @($windowsZip,$sourceZip)) {
        $hash = Get-FileHash -LiteralPath $zip -Algorithm SHA256
        '{0}  {1}' -f $hash.Hash.ToLowerInvariant(), [IO.Path]::GetFileName($zip)
    }
    $checksums | Set-Content -LiteralPath (Join-Path $destination 'SHA256SUMS.txt') -Encoding ascii
    Write-Host "Release ZIPs and SHA256SUMS.txt created in $destination"
}
finally {
    Pop-Location
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    $expectedPrefix = [IO.Path]::GetFullPath($stagingRoot).TrimEnd('\') + '\staging-'
    if ($resolvedStage.StartsWith($expectedPrefix,[StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedStage)) {
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}

