libimobiledevice (Windows) — pakete gömülü
==========================================

Bu klasördeki dosyalar masaüstü build/publish ile otomatik olarak
uygulama yanındaki ios\ klasörüne kopyalanır. Son kullanıcının
ayrıca indirmesine gerek yoktur.

Zorunlu:
  idevice_id.exe
  idevicebackup2.exe
  (+ yanındaki tüm .dll dosyaları — usbmuxd, imobiledevice, plist, libssl, …)

İsteğe bağlı (isim zenginleştirme):
  ideviceinfo.exe
  idevicepair.exe

Kaynak (geliştirici güncellemesi için):
  https://github.com/libimobiledevice-win32/imobiledevice-net/releases

Not:
  iPhone USB bağlantısı için Windows'ta Apple Mobile Device Support
  (iTunes) veya Apple Devices uygulaması gerekir — bu sürücü pakete
  gömülemez; kullanıcı bir kez kurmalıdır.
