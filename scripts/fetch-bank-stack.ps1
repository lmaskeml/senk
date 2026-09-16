# Bank root hide stack — Shamiko + PIF + LSPosed zip indir
$ErrorActionPreference = "Stop"
$out = Join-Path $PSScriptRoot "..\tools\bank-stack\embedded"
New-Item -ItemType Directory -Force -Path $out | Out-Null

$files = @(
  @{
    Name = "Shamiko.zip"
    Url  = "https://github.com/LSPosed/LSPosed.github.io/releases/download/shamiko-414/Shamiko-v1.2.5-414-release.zip"
  },
  @{
    Name = "PlayIntegrityFork.zip"
    Url  = "https://github.com/osm0sis/PlayIntegrityFork/releases/download/v17/PlayIntegrityFork-v17.zip"
  },
  @{
    Name = "LSPosed.zip"
    Url  = "https://github.com/LSPosed/LSPosed/releases/download/v1.9.2/LSPosed-v1.9.2-7024-zygisk-release.zip"
  }
)

foreach ($f in $files) {
  $path = Join-Path $out $f.Name
  Write-Host "GET $($f.Url)"
  Invoke-WebRequest -Uri $f.Url -OutFile $path -UseBasicParsing
  Write-Host "OK $path"
}

# LSPosed manager.apk — bildirim parasitik modu calismazsa bunu kur
$lspZip = Join-Path $out "LSPosed.zip"
$mgrOut = Join-Path (Split-Path $out -Parent) "manager.apk"
if (Test-Path $lspZip) {
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $z = [System.IO.Compression.ZipFile]::OpenRead($lspZip)
  $e = $z.GetEntry("manager.apk")
  if ($e) {
    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($e, $mgrOut, $true)
    Write-Host "OK extracted manager.apk -> $mgrOut"
  }
  $z.Dispose()
}

Write-Host ""
Write-Host "senk > Root Modu > Banka gizleme > Stack modul kur ile secin:"
Get-ChildItem $out -Filter *.zip | ForEach-Object { Write-Host "  $($_.FullName)" }
