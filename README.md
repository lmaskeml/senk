# Android Manager

Dr. Fone tarzı **modüler WPF** Android yönetim uygulaması. USB veya Wi‑Fi ADB ile cihaz bağlar; dosya, uygulama, galeri, SMS, yedekleme, root/recovery/ROM flash, güvenlik taraması ve ekran yansıtmayı tek konsoldan yönetir.

**Stack:** .NET 10 · WPF · Prism 9 (DryIoc) · CommunityToolkit.Mvvm · Material Design · AdvancedSharpAdbClient · Serilog · WebView2 · ImageSharp · SQLite

**UI dili:** Türkçe

---

## Özellikler

### Bağlantı

| Yöntem | Açıklama |
|--------|----------|
| **USB** | USB hata ayıklama; ADB ile otomatik algılama |
| **Wi‑Fi ADB (manuel)** | IP + bağlantı portu; Android 11+ için isteğe bağlı eşleştirme portu/kodu |
| **Wi‑Fi ADB (otomatik)** | mDNS (`_adb-tls-connect` / `_adb-tls-pairing`) keşfi |
| **Companion APK** | Aynı ağda UDP **37020** + TCP eşleştirme **37021**; 6 haneli kod / QR |
| **QR** | Kamera, dosya veya panodan `AndroidManagerConnect` okuma |
| **USB → Wi‑Fi** | `adb tcpip` sonrası LAN üzerinden bağlanma (varsayılan port **5555**) |
| **Kayıtlı bağlantılar** | Son uç noktaları saklama, yeniden bağlanma, silme |

Çoklu cihaz seçimi, Wi‑Fi oturumları için yeniden bağlanma (watchdog), sistem tepsisi ve toast bildirimleri.

### Cihaz bilgisi

- Üretici, model, Android sürümü, API seviyesi, seri numarası
- Pil %, sıcaklık, şarj durumu; depolama; RAM; CPU kullanımı
- Tanı: pil sağlığı / durumu / teknolojisi / çevrim sayısı; sensör özeti
- Yenile · tanı raporunu kaydet

### Dosya yöneticisi

- Çift panel: **Android** ↔ **Bilgisayar**
- Breadcrumb gezinme; gizli dosya tercihi (Ayarlar)
- **İndir / Gönder** — dosya ve **klasör** (özyinelemeli)
- Sürükle-bırak (her iki yön); klasör sürükleme destekli
- Yeni klasör, sil, yeniden adlandır
- Transfer ilerlemesi (%, hız)
- Güvenilir aktarım: `adb pull` / `adb push` + yedek yollar (tmp stage, exec-out, sync)
- APK/XAPK/APKS/APKM dosyasını Android paneline bırakınca kurulum
- Android `ls -la` tarih formatları (ISO + `Aug 19 12:34`) ile uyumlu listeleme

### Depolama analizi

- `/sdcard` (veya özel yol) altında klasör kullanımı (`du`)
- Birim özeti: kullanılan / boş alan
- ≥50 MB büyük dosya listesi (`find`)
- Wi‑Fi veya USB ADB ile çalışır

### Uygulamalar

- Liste, arama; sistem uygulamalarını göster/gizle
- İkonlar: **aapt** veya APK içi `ic_launcher` yedeği
- Kurulum: `.apk` / `.xapk` / `.apks` / `.apkm`
- Kaldır · **APK yedekle** · zorla durdur · veri temizle
- **DeBloater:** kök olmadan `pm uninstall -k --user 0`, geri yükleme, kategori/gerekçe

### Sistem — Recovery & ROM

#### Recovery Manager

- TWRP / OrangeFox / stok recovery algılama (ADB)
- **Recovery imajı yükleme:**
  - `fastboot flash boot` (Xiaomi / A-B — önerilen)
  - `fastboot flash recovery`
  - `fastboot boot` (geçici RAM boot)
  - Root `dd` (canlı sistem)
- **Recovery ZIP kurulum:**
  - TWRP/OrangeFox ADB: `twrp install` / `fox install`
  - Root canlı (OTA-command): Magisk vb. zip → recovery command → reboot recovery
- Recovery'ye yeniden başlat

#### Custom ROM Sihirbazı

İki yükleme yolu:

| Yöntem | Ne zaman | Akış |
|--------|----------|------|
| **TWRP sideload** | Klasik recovery zip | Wipe → `twrp sideload` → `adb sideload` |
| **Fastboot / Fastbootd** | `payload.bin` A/B ROM | Zip ayıkla → C# payload dumper → `fastbootd` → sıralı flash |

**CustomRomFlashingEngine** (`AndroidManager.Core/Services`):

1. Async zip ayıklama → `temp/extracted_rom`
2. Dahili **payload.bin** dumper (Python yok) → `boot.img`, `super.img`, …
3. `fastboot reboot fastboot` → fastbootd (userspace)
4. Sıralı flash: `boot`, `vendor_boot`, `dtbo`, `vbmeta`, `super`, `cust`, `userdata` → `set_active a` → `reboot`

- İlerleme: `IProgress<FlashingProgressReport>` (% + fastboot log)
- `CancellationToken` ile güvenli iptal
- `vbmeta` için otomatik `--disable-verity --disable-verification`
- Bootloader açık olmalı; TWRP gerekmez
- payload.bin: CrAU big-endian başlık, REPLACE / REPLACE_BZ / REPLACE_XZ destekli

#### Anti-Brick Deposu

- TWRP `dd` veya root ile canlı partition yedekleme (`exec-out`)
- Fastboot ile **seçili partition geri yükleme** (recovery, modem, boot vb.)
- `boot_a` → `boot` slot eşlemesi; Volume Full uyarıları

#### Rescue Center

- ADB / fastboot / recovery durum özeti
- Bootloader / sistem yeniden başlatma kısayolları

#### Root Manager

- Magisk / KernelSU / APatch profil analizi
- Root ön kontrol, önerilen yöntem, derin tarama

### Güvenlik

- Hızlı / derin sistem taraması
- VirusTotal entegrasyonu
- Kalıntı temizleme (orphan data, cache, APK artıkları)

### Ekran yansıtma

- Tercihen **scrcpy**; yoksa screencap yedeği
- FPS, bitrate, max boyut; uyanık tut; dokunuşları göster; salt okunur
- Geri · Ana ekran · Son uygulamalar · ses · güç
- Ekran görüntüsü · ekran kaydı

### Galeri

- MediaStore üzerinden fotoğraf / video listesi
- Sanallaştırılmış küçük resimler; önizleme (arka plan indirme)
- PC’ye kaydet
- WebP ve büyük medya için optimize edilmiş codec yolu

### SMS

- Konuşma listesi, arama
- Mesaj yaz / gönder  
  > Not: sessiz gönderim birçok cihazda kısıtlıdır; telefonda onay gerekebilir.

### WhatsApp Web

- Embedded **WebView2** → `web.whatsapp.com` (ADB gerekmez)
- Yenile · oturumu temizle · önbelleği aç

### Yedekleme

- Yedeklenebilir: SMS, rehber, DCIM fotoğraflar, videolar, APK’lar
- İsteğe bağlı ZIP; not; hedef klasör seçimi
- Seçici geri yükleme (SMS için root/companion gerekebilir); uygulama üzerine yazma seçeneği
- **Geçmiş:** SQLite; klasörü aç / sil

### Ayarlar

- `adb` / `fastboot` / `scrcpy` / `aapt` yolları; ADB bağlantı testi
- Tema, dil, yazı boyutu; gizli dosyaları göster
- Varsayılan indirme / yedek klasörleri; aktarımda üzerine yaz
- Yansıtma varsayılanları (FPS, bitrate, uyanık tut)
- Bildirimler: cihaz / transfer / yedek

### Companion (Android APK)

- Jetpack Compose · **AM Companion** (minSdk 23, targetSdk 34)
- LAN IP, cihaz bilgisi, 6 haneli kod, QR
- UDP yayın (~2 sn); TCP eşleştirme onayı
- Android 11+: kablosuz hata ayıklama yardımcısı, port göster/kopyala, sistem ayarlarına git

> Companion eşleştirmesi tek başına ADB değildir; kablosuz hata ayıklama portları gerekir.

---

## Gereksinimler

**Masaüstü**

- Windows 10 / 11
- [.NET 10 SDK](https://dotnet.microsoft.com/)
- `adb` — `tools/adb/`, `ADB_PATH` veya PATH
- `fastboot` — Ayarlar, `FASTBOOT_PATH`, Minimal ADB, SDK platform-tools veya `adb` ile aynı klasör

**Opsiyonel araçlar**

- `tools/scrcpy/` — ekran yansıtma (yoksa screencap)
- `tools/aapt/` — uygulama ikonları (yoksa APK içi arama)

**Companion APK**

- Android SDK + JDK 17+ (Android Studio JBR uygun)
- Gradle 8.2.1 (ilk derlemede indirilir)

---

## ADB kurulumu

1. [Android platform-tools](https://developer.android.com/tools/releases/platform-tools) indirin.
2. `adb.exe`, `AdbWinApi.dll`, `AdbWinUsbApi.dll` dosyalarını `tools/adb/` altına kopyalayın.
3. Alternatif: `ADB_PATH` ortam değişkenini `adb.exe` yoluna ayarlayın veya PATH’te `adb` olsun.

## fastboot (Recovery / ROM flash)

1. [Android platform-tools](https://developer.android.com/tools/releases/platform-tools) içindeki `fastboot.exe`'yi kullanın (genelde `adb.exe` ile aynı klasör).
2. **Minimal ADB and Fastboot** kuruluysa: Ayarlar → `fastboot.exe` yolunu aynı klasöre ayarlayın.
3. Alternatif: `FASTBOOT_PATH` ortam değişkeni veya Ayarlar'daki Fastboot yolu.
4. Komut satırında `fastboot devices` çalışıyorsa uygulama da aynı binary'yi kullanmalıdır.

> **Xiaomi / A-B cihazlar:** Büyük recovery imajları için `fastboot flash boot` tercih edin; `fastboot flash recovery` çoğu modelde başarısız olur.

## scrcpy (ekran yansıtma)

1. [scrcpy](https://github.com/Genymobile/scrcpy) indirin veya `winget install Genymobile.scrcpy`.
2. `scrcpy.exe` ve bağımlılıklarını `tools/scrcpy/` altına kopyalayın.
3. Yoksa Mirror modülü screencap yedeğini kullanır (daha yavaş).

## AAPT (ikon çıkarma)

Opsiyonel: `tools/aapt/aapt.exe` (Android SDK build-tools). Yoksa APK içinden `ic_launcher` aranır.

---

## Çalıştırma

```bash
dotnet restore AndroidManager.slnx
dotnet build AndroidManager.slnx
dotnet run --project src/AndroidManager.Shell
```

## Test

```bash
dotnet test AndroidManager.slnx
```

## Tek tık paketleme

```powershell
.\build-all.bat          # Companion APK + masaüstü ZIP
.\build-desktop.bat      # → dist\AndroidManager-*-win-x64-*.zip
.\build-apk.bat          # → dist\apk\

.\scripts\Build-Desktop.ps1 [-SelfContained]
.\scripts\Build-Apk.ps1 [-Configuration Release] [-Install]
.\installer\build.ps1 -Configuration Release -Version 1.0.0
```

Çıktı örneği: `dist/AndroidManager-1.0.0-win-x64.zip` (WiX MSI iskeleti opsiyonel).

### Companion APK (manuel)

```bash
cd AndroidCompanion
.\gradlew.bat :app:assembleDebug
```

APK: `app/build/outputs/apk/debug/app-debug.apk`  
Ayrıntılar: [AndroidCompanion/README.md](AndroidCompanion/README.md)

---

## Proje yapısı

```
senk/
├── AndroidManager.slnx
├── src/
│   ├── AndroidManager.Shell      # WPF host, Wi‑Fi UI, tray, toast
│   ├── AndroidManager.Core       # Contract’lar, modeller, CustomRomFlashingEngine
│   ├── AndroidManager.Device     # ADB, keşif, sync, cihaz bilgisi, terminal, logcat
│   ├── AndroidManager.Files      # Dual-panel dosya yöneticisi + depolama analizi
│   ├── AndroidManager.Apps       # APK yönetimi + DeBloater
│   ├── AndroidManager.Mirror     # scrcpy / screencap
│   ├── AndroidManager.Gallery    # MediaStore galeri (ImageSharp 3.x)
│   ├── AndroidManager.Messages   # SMS, kişiler, arama + WhatsApp Web
│   ├── AndroidManager.Backup     # Yedek / geri yükle + SQLite
│   ├── AndroidManager.Security   # Root, recovery, ROM flash, güvenlik taraması
│   └── AndroidManager.Settings   # JSON ayarlar
├── tests/
├── AndroidCompanion/             # Kotlin Compose companion APK
├── tools/                        # adb, scrcpy, aapt
├── scripts/                      # Build-*.ps1
├── installer/                    # Zip + WiX scaffold
└── dist/                         # Yayın çıktıları
```

| Proje | Rol |
|-------|-----|
| Shell | Prism host, navigasyon, Wi‑Fi diyalogları, tray/toast |
| Core | Contract'lar, modeller, **CustomRomFlashingEngine**, platform-tools yol çözümlemesi |
| Device | ADB istemcisi, cihaz keşfi, dosya sync, terminal, logcat |
| Files | Dual-panel dosya/klasör aktarımı, depolama analizi |
| Apps | Kurulum, kaldırma, ikon, DeBloater, APK analiz |
| Mirror | Ekran yansıtma ve giriş |
| Gallery | Fotoğraf / video galerisi |
| Messages | SMS, rehber, arama kaydı, WhatsApp Web |
| Backup | Yedekleme, geri yükleme, geçmiş |
| Security | Root, recovery, custom ROM, anti-brick, tarama, VirusTotal |
| Settings | Tema, yollar (adb/fastboot), bildirim tercihleri |

### Core — CustomRomFlashingEngine dosyaları

```
src/AndroidManager.Core/
├── Abstractions/ICustomRomFlashingEngine.cs
├── Models/CustomRomFlashingModels.cs
└── Services/
    ├── CustomRomFlashingEngine.cs   # Orkestrasyon
    ├── RomZipExtractor.cs           # Async zip ayıklama
    ├── PayloadBinDumper.cs          # payload.bin → .img
    ├── PayloadManifestParser.cs     # Protobuf manifest
    ├── ProtobufWireReader.cs
    └── FastbootFlashRunner.cs       # fastboot.exe süreç yönetimi
```

---

## Modül menüsü (UI)

**Cihaz:** Genel Bakış · ADB Terminal · Logcat · Boot · Dosyalar · Depolama · Uygulamalar · APK · Ekran Yansıtma · Galeri · SMS · Kişiler · Arama · WhatsApp Web

**Sistem:** Root Manager · Recovery Manager · Custom ROM · Anti-Brick Deposu · Rescue Center · Debloater

**Güvenlik:** Hızlı Tarama · VirusTotal · Kalıntı Temizleme

**Yedekleme:** Backup · **Ayarlar**

Wi‑Fi bağlantısı: kenar çubuğunda **WiFi Bağlan** · **Cihazları Yenile**. TWRP/recovery bağlıyken durum rozeti **TWRP / Recovery** olarak güncellenir.
