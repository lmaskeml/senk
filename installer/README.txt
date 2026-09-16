SeND ANDROID MANAGER — Inno Setup
=================================

Gereksinim
  Inno Setup 6  https://jrsoftware.org/isinfo.php
  Yayın klasörü dist\AndroidManager-1.0.0-win-x64-framework
  Companion APK dist\apk\  (AndroidManagerCompanion-release.apk)

Derleme
  powershell -File installer\compile-inno.ps1

  veya Inno Compiler ile installer\AndroidManager.iss dosyasını açıp Build.

Çıktı
  dist\AndroidManager_Kurulum.exe

Kurulum ne yapar
  - Programı Program Files\SeND ANDROID MANAGER altına kurar
  - Masaüstüne "Android Manager" kısayolu koyar (görev işaretliyse)
  - Masaüstüne AM_Companion_Kurulum.apk kopyalar (görev işaretliyse)
  - Sihirbazda "Kurulum ve kullanım" bilgi sayfasını gösterir
  - .NET 10 Desktop Runtime yoksa indirme bağlantısını önerir
