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
    $restoreArgs = @('restore','SteamAchievementUnlocker.csproj','-r','win-x64','--locked-mode')
    if ($RestoreConfig) { $restoreArgs += @('--configfile', [IO.Path]::GetFullPath($RestoreConfig)) }
    & dotnet @restoreArgs
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    & dotnet publish 'SteamAchievementUnlocker.csproj' -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    & (Join-Path $publishDirectory 'SteamAchievementUnlocker.exe') --self-test
    if ($LASTEXITCODE -ne 0) { throw 'Native DLL smoke test failed.' }

    # Source archives use an explicit allowlist, not a ZIP of the working directory.
    $rootFiles = @('Program.cs','AchievementCatalog.cs','SteamLibraryScanner.cs','SteamRuntime.cs','SteamStatsManager.cs','UserSettings.cs',
        'SteamAchievementUnlocker.csproj','packages.lock.json','global.json','NuGet.Config','.gitignore','README.md','LICENSE','THIRD-PARTY-NOTICES.md','GITHUB-PUBLISHING.md')
    foreach ($file in $rootFiles) { Copy-Item -LiteralPath (Join-Path $repoRoot $file) -Destination $sourceDirectory }
    foreach ($file in @('lib\steam_api64.dll','lib\Steamworks.NET.LICENSE.txt','lib\DOTNET-LICENSE.txt','lib\DOTNET-THIRD-PARTY-NOTICES.txt','scripts\Publish-Release.ps1','tests\Program.cs','tests\SteamAchievementUnlocker.Tests.csproj','.github\workflows\release.yml')) {
        $target = Join-Path $sourceDirectory $file
        New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
        Copy-Item -LiteralPath (Join-Path $repoRoot $file) -Destination $target
    }
    $forbidden = Get-ChildItem -LiteralPath $publishDirectory,$sourceDirectory -Recurse -Force -File | Where-Object {
        $_.Name -like 'web_api_key*' -or $_.Name -eq 'steam_appid.txt' -or $_.Name -like 'achievement-cache*' -or $_.Name -like '.env*' -or $_.Extension -in '.pfx','.pem'
    }
    if ($forbidden) { throw 'Personal configuration detected in release staging.' }
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $publishDirectory 'SteamAchievementUnlocker.runtimeconfig.json') -Raw | ConvertFrom-Json
    if ($runtimeConfig.runtimeOptions.framework -or $runtimeConfig.runtimeOptions.frameworks -or !(Test-Path -LiteralPath (Join-Path $publishDirectory 'coreclr.dll'))) {
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
