# One-click desktop publish -> dist/AndroidManager-*-win-x64.zip
param(
    [string]$Configuration = "Release",
    [string]$Version = "1.0.0",
    [switch]$SelfContained,
    [switch]$SkipZip
)

$ErrorActionPreference = "Stop"
$root = if ((Split-Path $PSScriptRoot -Leaf) -eq "scripts") {
    Split-Path $PSScriptRoot -Parent
} else {
    $PSScriptRoot
}

$publishDir = Join-Path $root "publish\$Configuration"
$distDir = Join-Path $root "dist"

Write-Host ""
Write-Host "=== SeND ANDROID MANAGER - Desktop Build ===" -ForegroundColor Cyan
Write-Host "Config: $Configuration | Version: $Version | SelfContained: $SelfContained"
Write-Host ""

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "dotnet SDK not found. https://dotnet.microsoft.com/download"
}

$shellProj = Join-Path $root "src\AndroidManager.Shell\AndroidManager.Shell.csproj"
if (-not (Test-Path $shellProj)) { throw "Shell project missing: $shellProj" }

if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
New-Item -ItemType Directory -Force -Path $distDir | Out-Null

$publishArgs = @(
    "publish", $shellProj,
    "-c", $Configuration,
    "-r", "win-x64",
    "-p:Version=$Version",
    "-p:PublishSingleFile=false",
    "-o", $publishDir
)

if ($SelfContained) {
    $publishArgs += @("--self-contained", "true", "-p:IncludeNativeLibrariesForSelfExtract=true")
} else {
    $publishArgs += @("--self-contained", "false")
}

Write-Host "dotnet publish..." -ForegroundColor Yellow
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "Publish failed ($LASTEXITCODE)" }

Write-Host "Copying tools..." -ForegroundColor Yellow

# App looks for: adb\, ios\, whatsapp\, tools\scrcpy\, tools\aapt\
function Copy-ToolsToPublish([string]$srcName, [string]$dstRelative) {
    $src = Join-Path $root "tools\$srcName"
    $dst = Join-Path $publishDir $dstRelative
    if (-not (Test-Path $src)) {
        Write-Host "  (skip) tools/$srcName missing" -ForegroundColor DarkYellow
        return $false
    }
    New-Item -ItemType Directory -Force -Path $dst | Out-Null
    Copy-Item "$src\*" $dst -Recurse -Force
    Write-Host "  + $dstRelative" -ForegroundColor Green
    return $true
}

Copy-ToolsToPublish "adb" "adb" | Out-Null
Copy-ToolsToPublish "ios" "ios" | Out-Null
Copy-ToolsToPublish "whatsapp" "whatsapp" | Out-Null
Copy-ToolsToPublish "scrcpy" "tools\scrcpy" | Out-Null
Copy-ToolsToPublish "aapt" "tools" | Out-Null

# iOS libimobiledevice — WhatsApp Android→iPhone için zorunlu
$ideviceId = Join-Path $publishDir "ios\idevice_id.exe"
$ideviceBackup2 = Join-Path $publishDir "ios\idevicebackup2.exe"
if (-not (Test-Path $ideviceId) -or -not (Test-Path $ideviceBackup2)) {
    throw @"
iOS araçları eksik (tools\ios).
Beklenen: tools\ios\idevice_id.exe ve tools\ios\idevicebackup2.exe (+ DLL'ler).
Kaynak: https://github.com/libimobiledevice-win32/imobiledevice-net/releases
"@
}
Write-Host "  OK ios: idevice_id.exe + idevicebackup2.exe" -ForegroundColor Green

$apkSrc = Join-Path $root "dist\apk\AndroidManagerCompanion.apk"
if (-not (Test-Path $apkSrc)) {
    $apkSrc = Join-Path $root "dist\apk\app-debug.apk"
}
$apkDstDir = Join-Path $publishDir "companion"
if (Test-Path $apkSrc) {
    New-Item -ItemType Directory -Force -Path $apkDstDir | Out-Null
    Copy-Item $apkSrc (Join-Path $apkDstDir "AndroidManagerCompanion.apk") -Force
    Write-Host "  + companion APK" -ForegroundColor Green
}

# Inno / son kullanıcı klasörü: dist\AndroidManager-*-win-x64-framework
$suffix = if ($SelfContained) { "selfcontained" } else { "framework" }
$frameworkDirName = "AndroidManager-$Version-win-x64-$suffix"
$frameworkDir = Join-Path $distDir $frameworkDirName
if (Test-Path $frameworkDir) { Remove-Item $frameworkDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $frameworkDir | Out-Null
Copy-Item (Join-Path $publishDir "*") $frameworkDir -Recurse -Force
Write-Host "  + dist\$frameworkDirName" -ForegroundColor Green

if (-not $SkipZip) {
    $zipPath = Join-Path $distDir "$frameworkDirName.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $frameworkDir "*") -DestinationPath $zipPath -Force
    Write-Host ""
    Write-Host "ZIP: $zipPath" -ForegroundColor Green
}

Write-Host "OUT: $publishDir" -ForegroundColor Green
Write-Host "Desktop build OK." -ForegroundColor Green
