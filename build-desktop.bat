@echo off
setlocal
cd /d "%~dp0"
title SeND ANDROID MANAGER — Desktop Build
echo.
echo  Double-click build: Desktop (WPF, Release)
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Build-Desktop.ps1" -Configuration Release %*
set ERR=%ERRORLEVEL%
echo.
if %ERR% neq 0 (
  echo BUILD FAILED [%ERR%]
  pause
  exit /b %ERR%
)
echo Output: dist\ and publish\
explorer "%~dp0dist" 2>nul
pause
exit /b 0
