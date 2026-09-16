# Build APK + Desktop, pack companion into desktop zip when available
param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release",
    [switch]$SelfContained,
    [switch]$SkipApk,
    [switch]$SkipDesktop
)

$ErrorActionPreference = "Stop"
$here = $PSScriptRoot

Write-Host ""
Write-Host "############################################" -ForegroundColor Cyan
Write-Host "# SeND ANDROID MANAGER — Full One-Click Build   #" -ForegroundColor Cyan
Write-Host "############################################" -ForegroundColor Cyan

if (-not $SkipApk) {
    & (Join-Path $here "Build-Apk.ps1") -Configuration $Configuration
    if ($LASTEXITCODE -ne 0 -and -not $?) { throw "APK build failed" }
}

if (-not $SkipDesktop) {
    $deskArgs = @{
        Configuration = $Configuration
        Version       = $Version
    }
    if ($SelfContained) { $deskArgs.SelfContained = $true }
    & (Join-Path $here "Build-Desktop.ps1") @deskArgs
}

$root = Split-Path $here -Parent
Write-Host ""
Write-Host "Artifacts:" -ForegroundColor Cyan
Get-ChildItem (Join-Path $root "dist") -Recurse -File -ErrorAction SilentlyContinue |
    ForEach-Object { Write-Host "  $($_.FullName.Substring($root.Length + 1))  ($([math]::Round($_.Length/1MB, 2)) MB)" }

Write-Host ""
Write-Host "ALL DONE." -ForegroundColor Green
