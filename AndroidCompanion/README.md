# Android Manager Companion

UDP **37020** · TCP pair **37021** · ADB **5555**

## Build

Open `AndroidCompanion` in Android Studio, or:

```bash
cd AndroidCompanion
./gradlew :app:assembleDebug
```

APK: `app/build/outputs/apk/debug/app-debug.apk`

## Features

- Shows LAN IP + 6-digit pair code + QR (`AndroidManagerConnect` JSON)
- UDP broadcast (`AndroidManagerCompanion`) every 2s
- TCP pair approval (same pair code as UI — `updatePairCode`)
- Android 11+: `WirelessDebuggingHelper` opens developer options + setup steps
- Optional `wirelessAdbPort` in UDP payload when TLS ADB port is readable

## PC side

Desktop WiFi dialog: **Otomatik** / **Manuel IP** / **QR** (camera, file, clipboard).
