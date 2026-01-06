param(
    [string]$Configuration = "Debug",
    [string]$TargetFramework = "net10.0"
)

$clientRoot = $PSScriptRoot
$repoRoot = Split-Path -Parent $clientRoot
$serverWwwroot = Join-Path $repoRoot "WebFsc.Server\\wwwroot"
$clientWwwroot = Join-Path $clientRoot "wwwroot"
$clientBuiltWwwroot = Join-Path $clientRoot ("bin\\{0}\\{1}\\wwwroot" -f $Configuration, $TargetFramework)
$clientOutputRoot = Join-Path $clientRoot ("bin\\{0}\\{1}" -f $Configuration, $TargetFramework)

if (-not (Test-Path $serverWwwroot)) {
    New-Item -ItemType Directory -Path $serverWwwroot | Out-Null
}

Get-ChildItem -Path $serverWwwroot -Force -ErrorAction SilentlyContinue |
    Remove-Item -Force -Recurse -ErrorAction SilentlyContinue

if (Test-Path $clientWwwroot) {
    Copy-Item -Path (Join-Path $clientWwwroot "*") -Destination $serverWwwroot -Recurse -Force
} else {
    Write-Error ("Client wwwroot not found: {0}" -f $clientWwwroot)
    exit 1
}

if (Test-Path $clientBuiltWwwroot) {
    Copy-Item -Path (Join-Path $clientBuiltWwwroot "*") -Destination $serverWwwroot -Recurse -Force
} else {
    Write-Error ("Built wwwroot not found: {0}" -f $clientBuiltWwwroot)
    exit 1
}

$dotnetRoot = $env:DOTNET_ROOT
if (-not $dotnetRoot) {
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($dotnetCommand) {
        $dotnetRoot = Split-Path -Parent $dotnetCommand.Source
    }
}

if ($dotnetRoot) {
    $runtimePackRoot = Join-Path $dotnetRoot "packs\\Microsoft.NETCore.App.Runtime.Mono.browser-wasm"
    if (Test-Path $runtimePackRoot) {
        $corelib = Get-ChildItem -Path $runtimePackRoot -Recurse -Filter "System.Private.CoreLib.dll" -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($corelib) {
            if (-not (Test-Path $clientOutputRoot)) {
                New-Item -ItemType Directory -Path $clientOutputRoot | Out-Null
            }
            Copy-Item -Path $corelib.FullName -Destination $clientOutputRoot -Force
        } else {
            Write-Warning "System.Private.CoreLib.dll not found under runtime pack."
        }
    } else {
        Write-Warning ("Runtime pack path not found: {0}" -f $runtimePackRoot)
    }
} else {
    Write-Warning "DOTNET_ROOT not set and dotnet not found; skipping System.Private.CoreLib.dll copy."
}
