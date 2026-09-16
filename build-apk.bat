@echo off
setlocal
cd /d "%~dp0"
title SeND ANDROID MANAGER — Companion APK Build
echo.
echo  Double-click build: Companion APK (Release)
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Build-Apk.ps1" -Configuration Release %*
set ERR=%ERRORLEVEL%
echo.
if %ERR% neq 0 (
  echo BUILD FAILED [%ERR%]
  pause
  exit /b %ERR%
)
echo Output: dist\apk\
explorer "%~dp0dist\apk" 2>nul
pause
exit /b 0
