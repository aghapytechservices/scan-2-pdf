@echo off
rem Removes Scan 2 PDF Bridge (standalone version) for the current Windows user. Double-click to run.
set "EXE=%LOCALAPPDATA%\Scan2PdfBridge\Scan2PdfBridge.exe"
if exist "%EXE%" (
  start "" /wait "%EXE%" --uninstall
) else (
  reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v Scan2PdfBridge /f >nul 2>&1
  echo Scan 2 PDF Bridge was not installed for this user.
)
timeout /t 3 >nul
