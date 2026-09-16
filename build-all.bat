@echo off
setlocal
cd /d "%~dp0"
title SeND ANDROID MANAGER — Full Build (APK + Desktop)
echo.
echo  Double-click build: APK + Desktop ZIP (Release)
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Build-All.ps1" -Configuration Release %*
set ERR=%ERRORLEVEL%
echo.
if %ERR% neq 0 (
  echo BUILD FAILED [%ERR%]
  pause
  exit /b %ERR%
)
echo Output: dist\
explorer "%~dp0dist" 2>nul
pause
exit /b 0
