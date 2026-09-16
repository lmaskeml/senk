# SeND ANDROID MANAGER — publish + optional WiX MSI

param(
    [string]$Configuration = "Release",
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$publishDir = Join-Path $root "publish\$Configuration"
$distDir = Join-Path $root "dist"

Write-Host "SeND ANDROID MANAGER Installer Build" -ForegroundColor Cyan
Write-Host "Version: $Version | Config: $Configuration"

Write-Host "`nPublishing Shell..." -ForegroundColor Yellow
dotnet publish (Join-Path $root "src\AndroidManager.Shell\AndroidManager.Shell.csproj") `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained false `
    -p:Version=$Version `
    -o $publishDir

if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

function Copy-Tools($name) {
    $src = Join-Path $root "tools\$name"
    $dst = Join-Path $publishDir "tools\$name"
    if (Test-Path $src) {
        New-Item -ItemType Directory -Force -Path $dst | Out-Null
        Copy-Item "$src\*" $dst -Recurse -Force
        Write-Host "  Copied tools/$name"
    } else {
        Write-Host "  Missing tools/$name (skipped)" -ForegroundColor DarkYellow
    }
}

Write-Host "`nCopying tools..." -ForegroundColor Yellow
Copy-Tools "adb"
Copy-Tools "scrcpy"
Copy-Tools "aapt"

New-Item -ItemType Directory -Force -Path $distDir | Out-Null
$zipPath = Join-Path $distDir "AndroidManager-$Version-win-x64.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -Force
Write-Host "`nZip ready: $zipPath" -ForegroundColor Green

$wixProj = Join-Path $PSScriptRoot "AndroidManager.Installer\AndroidManager.Installer.wixproj"
if (Test-Path $wixProj) {
    Write-Host "`nBuilding WiX MSI (requires WiX Toolset v4)..." -ForegroundColor Yellow
    dotnet build $wixProj -c $Configuration -p:Version=$Version -p:PublishDir="$publishDir\" -o $distDir
    if ($LASTEXITCODE -ne 0) {
        Write-Host "WiX build failed — zip package is still available." -ForegroundColor DarkYellow
    }
} else {
    Write-Host "`nWiX project not found; shipping zip only." -ForegroundColor DarkYellow
}

Write-Host "`nDone." -ForegroundColor Green
