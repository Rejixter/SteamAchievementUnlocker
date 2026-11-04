param([Parameter(Mandatory=$true)][string]$AppDirectory)
$ErrorActionPreference = 'Stop'
$app = [IO.Path]::GetFullPath($AppDirectory)
$exe = Join-Path $app 'SteamAchievementUnlocker.exe'
if (!(Test-Path -LiteralPath $exe) -or !(Test-Path -LiteralPath (Join-Path $app 'lib\steam_api64.dll'))) { throw 'Extract the entire Windows ZIP first.' }
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
$scratch = Join-Path $temporaryRoot ('sau-portable-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
try {
    foreach ($directory in @('temp','appdata','local','bundle','working')) { New-Item -ItemType Directory -Path (Join-Path $scratch $directory) | Out-Null }
    foreach ($check in @('--acceptance-self-test','--worker-self-test','--self-test')) {
        $info = New-Object System.Diagnostics.ProcessStartInfo
        $info.FileName = $exe; $info.Arguments = $check
        $info.WorkingDirectory = Join-Path $scratch 'working'; $info.UseShellExecute = $false; $info.CreateNoWindow = $true
        $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
        $info.EnvironmentVariables['PATH'] = Join-Path $env:WINDIR 'System32'
        $info.EnvironmentVariables['DOTNET_ROOT'] = Join-Path $scratch 'no-dotnet-installed'
        $info.EnvironmentVariables['DOTNET_ROOT_X64'] = Join-Path $scratch 'no-dotnet-installed'
        $info.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP'] = '0'
        $info.EnvironmentVariables['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = Join-Path $scratch 'bundle'
        $info.EnvironmentVariables['TEMP'] = Join-Path $scratch 'temp'; $info.EnvironmentVariables['TMP'] = Join-Path $scratch 'temp'
        $info.EnvironmentVariables['APPDATA'] = Join-Path $scratch 'appdata'; $info.EnvironmentVariables['LOCALAPPDATA'] = Join-Path $scratch 'local'
        $process = [Diagnostics.Process]::Start($info)
        try {
            $output = $process.StandardOutput.ReadToEndAsync(); $errors = $process.StandardError.ReadToEndAsync()
            if (!$process.WaitForExit(45000)) { $process.Kill(); throw "Portable check timed out: $check" }
            $output.GetAwaiter().GetResult() | Write-Output
            if ($process.ExitCode -ne 0) { $errors.GetAwaiter().GetResult() | Write-Output; throw "Portable check failed: $check" }
            Write-Output "PASS: portable process $check with isolated settings/temp, unrelated working directory and no .NET on PATH."
        }
        finally { $process.Dispose() }
    }
    Write-Output 'This is process isolation on the current Windows installation, not a clean VM or a different Windows account. No Steam achievements were read or changed.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($scratch)
    if ([IO.Path]::GetDirectoryName($resolved) -ne $temporaryRoot -or ![IO.Path]::GetFileName($resolved).StartsWith('sau-portable-')) { throw 'Invalid portable-test cleanup path.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
