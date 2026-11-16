@echo off
rem Installs (or updates) Scan 2 PDF Bridge for the current Windows user. Double-click to run.
cd /d "%~dp0"
if not exist "Scan2PdfBridge.exe" (
  echo Scan2PdfBridge.exe was not found next to this file.
  pause
  exit /b 1
)
start "" /wait "Scan2PdfBridge.exe" --install
echo.
echo Scan 2 PDF Bridge is installed. Look for its icon in the system tray.
timeout /t 5 >nul
