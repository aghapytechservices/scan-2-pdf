<#
  Installs Scan 2 PDF Bridge for the current user and starts it at every logon.

  Runs as the logged-in user (not a Windows service) on purpose: many scanner
  WIA drivers don't work from Session 0 / LocalSystem.

  Usage (from the bridge folder):
    dotnet publish .\ScanBridge -c Release -o .\publish
    .\install.ps1                      # uses .\publish
    .\install.ps1 -SourceDir \\server\share\Scan2PdfBridge
#>
param(
  [string]$SourceDir = $(if (Test-Path (Join-Path $PSScriptRoot "Scan2PdfBridge.exe")) { $PSScriptRoot } else { Join-Path $PSScriptRoot "publish" }),
  [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Scan2PdfBridge")
)

$ErrorActionPreference = "Stop"
$taskName = "Scan 2 PDF Bridge"
$exe = Join-Path $InstallDir "Scan2PdfBridge.exe"

if (-not (Test-Path (Join-Path $SourceDir "Scan2PdfBridge.exe"))) {
  throw "Scan2PdfBridge.exe not found in $SourceDir. Run 'dotnet publish' first."
}

# Stop a running copy so files can be replaced
Get-Process Scan2PdfBridge -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -Path (Join-Path $SourceDir "*") -Destination $InstallDir -Recurse -Force

$action    = New-ScheduledTaskAction -Execute $exe -Argument "--background" -WorkingDirectory $InstallDir
$trigger   = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
               -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings `
  -Principal $principal -Force | Out-Null

Start-ScheduledTask -TaskName $taskName
Start-Sleep -Seconds 2

try {
  $r = Invoke-RestMethod -Uri "http://127.0.0.1:52801/health" -TimeoutSec 5
  Write-Host "Scan 2 PDF Bridge is running (v$($r.version)). Look for its icon in the system tray." -ForegroundColor Green
  Start-Process "http://127.0.0.1:52801/"
} catch {
  Write-Warning "Installed, but the health check failed. Check $InstallDir and try: & '$exe'"
}
