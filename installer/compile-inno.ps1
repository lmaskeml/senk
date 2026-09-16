# SeND ANDROID MANAGER — Inno Setup derleyici
# Kullanım:  powershell -File installer\compile-inno.ps1

param(
    [string]$IssPath = (Join-Path $PSScriptRoot "AndroidManager.iss")
)

$ErrorActionPreference = "Stop"

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    throw "Inno Setup 6 bulunamadı. https://jrsoftware.org/isinfo.php adresinden kurun."
}

$publishDir = Join-Path (Split-Path $PSScriptRoot -Parent) "dist\AndroidManager-1.0.0-win-x64-framework"
$exe = Join-Path $publishDir "AndroidManager.Shell.exe"
if (-not (Test-Path $exe)) {
    throw "Yayın klasörü eksik: $publishDir`nÖnce scripts\Build-Desktop.ps1 çalıştırın."
}

Write-Host "ISCC: $iscc" -ForegroundColor Cyan
Write-Host "ISS : $IssPath"
& $iscc $IssPath
if ($LASTEXITCODE -ne 0) { throw "Inno Setup derlemesi başarısız ($LASTEXITCODE)" }

$out = Join-Path (Split-Path $PSScriptRoot -Parent) "dist\AndroidManager_Kurulum.exe"
Write-Host ""
Write-Host "Kurulum paketi: $out" -ForegroundColor Green
