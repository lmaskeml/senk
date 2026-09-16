# One-click builds

| File | What it builds |
|------|----------------|
| **`build-all.bat`** | Companion APK + Desktop ZIP |
| **`build-desktop.bat`** | WPF app → `dist\AndroidManager-*-win-x64-*.zip` |
| **`build-apk.bat`** | Companion APK → `dist\apk\` |

PowerShell (same):

```powershell
.\scripts\Build-All.ps1
.\scripts\Build-Desktop.ps1
.\scripts\Build-Desktop.ps1 -SelfContained
.\scripts\Build-Apk.ps1
.\scripts\Build-Apk.ps1 -Configuration Release
.\scripts\Build-Apk.ps1 -Install   # adb install after build
```

## Requirements

**Desktop:** .NET 10 SDK  
**APK:** Android SDK (`%LOCALAPPDATA%\Android\Sdk`) + JDK 17+ (Android Studio JBR is fine)

First APK build downloads Gradle 8.2.1 automatically.

## Output

```
dist/
  apk/
    app-debug.apk
    AndroidManagerCompanion.apk
  AndroidManager-1.0.0-win-x64-framework.zip
publish/Release/          # unpacked desktop bits
```
