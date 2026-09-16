# lpmake.exe -> tools\lpmake\lpmake.exe
param([switch]$Download)

$ErrorActionPreference = "Stop"
$destDir = Join-Path $PSScriptRoot "lpmake"
$dest = Join-Path $destDir "lpmake.exe"
New-Item -ItemType Directory -Force -Path $destDir | Out-Null

if (Test-Path $dest) {
    Write-Host "Zaten var: $dest" -ForegroundColor Green
    exit 0
}

function Find-LpmakeInSdk {
    $roots = @(
        $env:ANDROID_HOME,
        $env:ANDROID_SDK_ROOT,
        (Join-Path $env:LOCALAPPDATA "Android\Sdk")
    ) | Where-Object { $_ -and (Test-Path $_) }

    foreach ($root in $roots) {
        $bt = Join-Path $root "build-tools"
        if (-not (Test-Path $bt)) { continue }
        $found = Get-ChildItem -Path $bt -Directory | Sort-Object Name -Descending | ForEach-Object {
            Join-Path $_.FullName "lpmake.exe"
        } | Where-Object { Test-Path $_ } | Select-Object -First 1
        if ($found) { return $found }
    }
    return $null
}

$source = Find-LpmakeInSdk
if (-not $source) {
    $prebuiltUrl = "https://raw.githubusercontent.com/Rprop/aosp15_partition_tools/main/windows_x86/lpmake.exe"
    if ($Download -or -not $source) {
        Write-Host "SDK'da lpmake yok; onceden derlenmis surum indiriliyor..." -ForegroundColor Yellow
        Write-Host "  $prebuiltUrl"
        Invoke-WebRequest -Uri $prebuiltUrl -OutFile $dest -UseBasicParsing
        if (Test-Path $dest) {
            Write-Host "Hazir: $dest" -ForegroundColor Green
            exit 0
        }
    }
}

if (-not $source) {
    Write-Host "lpmake.exe kurulamadi." -ForegroundColor Red
    Write-Host "  .\tools\install-lpmake.ps1 -Download"
    exit 1
}

Copy-Item -Path $source -Destination $dest -Force
Write-Host "Hazir: $dest" -ForegroundColor Green
