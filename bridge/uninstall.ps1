# Removes Scan 2 PDF Bridge for the current user (start-at-sign-in entry, legacy scheduled task, and files).
$exe = Join-Path $env:LOCALAPPDATA "Scan2PdfBridge\Scan2PdfBridge.exe"
if (Test-Path $exe) {
  Start-Process -FilePath $exe -ArgumentList "--uninstall", "--quiet" -Wait
} else {
  Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "Scan2PdfBridge" -ErrorAction SilentlyContinue
}
Unregister-ScheduledTask -TaskName "Scan 2 PDF Bridge" -Confirm:$false -ErrorAction SilentlyContinue
Write-Host "Scan 2 PDF Bridge removed."
