param(
    [string]$Configuration = "Debug",
    [string]$TargetFramework = "net10.0"
)

$clientRoot = $PSScriptRoot
$repoRoot = Split-Path -Parent $clientRoot
$serverWwwroot = Join-Path $repoRoot "WebFsc.Server\\wwwroot"
$clientWwwroot = Join-Path $clientRoot "wwwroot"
$clientBuiltWwwroot = Join-Path $clientRoot ("bin\\{0}\\{1}\\wwwroot" -f $Configuration, $TargetFramework)

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
