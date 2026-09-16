@echo off
title vili flash_all - stock + TWRP ramdisk, sisteme acilir
if defined AM_PLATFORM_TOOLS set "PATH=%AM_PLATFORM_TOOLS%;%PATH%"
set PATH=C:\Users\Admin\AppData\Local\Android\Sdk\platform-tools;%PATH%
set "IMG=%~dp0images"
set "INJECT=%~dp0inject-twrp.sh"
set "MBPC=%~dp0tools\magiskboot"
set "MAGISK="
if exist "%~dp0Magisk-v30.7.zip" set "MAGISK=%~dp0Magisk-v30.7.zip"
if not defined MAGISK (
  for %%F in ("%~dp0Magisk*.zip") do set "MAGISK=%%~fF"
)

echo =======================================================
echo  FAZ 1: Stock flash
echo =======================================================

fastboot %* set_active a
fastboot %* erase boot_ab

fastboot %* flash xbl_ab           "%IMG%/xbl.elf"
fastboot %* flash xbl_config_ab    "%IMG%/xbl_config.elf"
fastboot %* flash abl_ab           "%IMG%/abl.elf"
fastboot %* flash aop_ab           "%IMG%/aop.mbn"
fastboot %* flash tz_ab            "%IMG%/tz.mbn"
fastboot %* flash featenabler_ab   "%IMG%/featenabler.mbn"
fastboot %* flash hyp_ab           "%IMG%/hypvm.mbn"
fastboot %* flash modem_ab         "%IMG%/NON-HLOS.bin"
fastboot %* flash bluetooth_ab     "%IMG%/BTFM.bin"
fastboot %* flash dsp_ab           "%IMG%/dspso.bin"
fastboot %* flash keymaster_ab     "%IMG%/km41.mbn"
fastboot %* flash devcfg_ab        "%IMG%/devcfg.mbn"
fastboot %* flash qupfw_ab         "%IMG%/qupv3fw.elf"
fastboot %* flash uefisecapp_ab    "%IMG%/uefi_sec.mbn"
fastboot %* erase imagefv_ab
fastboot %* flash imagefv_ab       "%IMG%/imagefv.elf"
fastboot %* flash shrm_ab          "%IMG%/shrm.elf"
fastboot %* flash multiimgoem_ab   "%IMG%/multi_image.mbn"
fastboot %* flash cpucp_ab         "%IMG%/cpucp.elf"
fastboot %* flash qweslicstore_ab  "%IMG%/qweslicstore.bin"
fastboot %* flash logfs            "%IMG%/logfs_ufs_8mb.bin"
fastboot %* flash rescue           "%IMG%/rescue.img"
fastboot %* flash misc             "%IMG%/misc.img"
fastboot %* flash storsec          "%IMG%/storsec.mbn"
fastboot %* flash spunvm           "%IMG%/spunvm.bin"
fastboot %* flash mdcompress       "%IMG%/mdcompress.mbn"
fastboot %* flash rtice            "%IMG%/rtice.mbn"
fastboot %* flash vendor_boot_ab   "%IMG%/vendor_boot.img"
fastboot %* flash super            "%IMG%/super.img"
fastboot %* flash cust             "%IMG%/cust.img"
fastboot %* flash dtbo_ab          "%IMG%/dtbo.img"

fastboot %* flash vbmeta_ab        "%IMG%/vbmeta.img" --disable-verity --disable-verification
fastboot %* flash vbmeta_system_ab "%IMG%/vbmeta_system.img" --disable-verity --disable-verification

fastboot %* erase metadata
fastboot %* flash metadata         "%IMG%/metadata.img"
fastboot %* flash userdata         "%IMG%/userdata.img"
fastboot %* flash boot_ab          "%IMG%/boot.img"
fastboot %* flash imagefv_ab       "%IMG%/imagefv.elf"
fastboot %* erase opcust
fastboot %* erase opconfig
fastboot %* set_active a

if not exist "%IMG%\twrp.img" (
  echo [UYARI] twrp.img yok. Sisteme reboot.
  fastboot %* reboot
  goto :end
)
if not exist "%INJECT%" (
  echo [UYARI] inject-twrp.sh yok. Sisteme reboot.
  fastboot %* reboot
  goto :end
)

echo.
echo =======================================================
echo  FAZ 2: TWRP RAM boot + inject + Magisk
echo =======================================================

echo TWRP RAM'den aciliyor...
fastboot %* boot "%IMG%\twrp.img"
if errorlevel 1 (
  echo [HATA] fastboot boot basarisiz!
  fastboot %* reboot
  goto :end
)

echo TWRP ADB bekleniyor...
set /a N=0
:WaitTwrp
timeout /t 5 >nul
set /a N+=1
adb devices | findstr /v "List" | findstr /i "recovery sideload device" >nul
if not errorlevel 1 goto TwrpOk
if %N% GEQ 36 (
  echo [HATA] TWRP ADB gelmedi!
  goto :end
)
goto WaitTwrp

:TwrpOk
echo [OK] TWRP baglandi!

adb shell "twrp mount data 2>/dev/null; mount /data 2>/dev/null; mkdir -p /data/local/tmp /sdcard; true"

if exist "%MBPC%" (
  echo magiskboot yedek gonderiliyor...
  adb push "%MBPC%" /data/local/tmp/magiskboot
  adb shell "chmod 755 /data/local/tmp/magiskboot"
)

echo inject-twrp.sh gonderiliyor...
adb push "%INJECT%" /tmp/inject-twrp.sh
adb shell "chmod 755 /tmp/inject-twrp.sh"

echo twrp.img gonderiliyor...
adb push "%IMG%\twrp.img" /data/local/tmp/twrp.img
if errorlevel 1 (
  echo /data basarisiz, /sdcard deneniyor...
  adb push "%IMG%\twrp.img" /sdcard/twrp.img
)

echo.
echo -------------------------------------------------------
echo  Inject calistiriliyor... Log:
echo -------------------------------------------------------
adb shell "sh /tmp/inject-twrp.sh 2>&1"
if errorlevel 1 (
  echo [HATA] Inject basarisiz!
  echo Stock boot diskte kaldi. Magisk atlanacak, data yine formatlanacak.
) else (
  echo -------------------------------------------------------
  echo [OK] Inject basarili!
  if defined MAGISK if exist "%MAGISK%" (
    echo.
    echo Magisk yukleniyor...
    adb push "%MAGISK%" /sdcard/Magisk.zip
    adb shell "twrp install /sdcard/Magisk.zip 2>&1"
    echo [OK] Magisk kuruldu.
  )
)

echo.
echo Format Data (sifreleme silinir, TWRP dongusu onlenir)...
adb shell "rm -f /cache/recovery/command /cache/recovery/openrecoveryscript /data/recovery/command 2>/dev/null; true"
adb shell "twrp wipe cache" 2>nul
adb shell "twrp wipe dalvik" 2>nul
adb shell "printf yes | twrp format data" 2>&1
if errorlevel 1 (
  echo format data yok, twrp wipe data...
  adb shell "twrp wipe data" 2>&1
)
echo [OK] Data formatlandi.

:RebootSys
echo.
echo Sisteme reboot ediliyor...
adb shell "twrp reboot system" 2>nul
timeout /t 3 >nul
adb reboot system 2>nul

echo.
echo =======================================================
echo  BITTI
echo  - Kurulum ekrani acilmali
echo  - TWRP: adb reboot recovery
echo  - Magisk: Uygulamadan ek kurulum isteyebilir
echo =======================================================

:end
if /i "%AM_NOPAUSE%"=="1" goto :eof
pause
