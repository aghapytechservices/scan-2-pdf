$ErrorActionPreference = "SilentlyContinue"
Unregister-ScheduledTask -TaskName "Scan 2 PDF Bridge" -Confirm:$false
Get-Process Scan2PdfBridge | Stop-Process -Force
Remove-Item -Recurse -Force (Join-Path $env:LOCALAPPDATA "Scan2PdfBridge")
Write-Host "Scan 2 PDF Bridge removed."
