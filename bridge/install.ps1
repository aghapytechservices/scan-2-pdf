<#
  Installs (or updates) Scan 2 PDF Bridge for the current Windows user and starts it at sign-in.

  You don't need this script: double-clicking Scan2PdfBridge.exe offers to install it.
  This script is for silent installs (IT / scripted deployment):

    .\install.ps1                     # uses Scan2PdfBridge.exe next to this script
    .\install.ps1 -SourceDir \\server\share\Scan2PdfBridge
#>
param(
  [string]$SourceDir = $(if (Test-Path (Join-Path $PSScriptRoot "Scan2PdfBridge.exe")) { $PSScriptRoot } else { Join-Path $PSScriptRoot "publish" })
)

$ErrorActionPreference = "Stop"
$exe = Join-Path $SourceDir "Scan2PdfBridge.exe"
if (-not (Test-Path $exe)) { throw "Scan2PdfBridge.exe not found in $SourceDir" }

# The exe copies itself to %LOCALAPPDATA%\Scan2PdfBridge, enables start at sign-in, and starts.
Start-Process -FilePath $exe -ArgumentList "--install", "--quiet" -Wait

for ($i = 0; $i -lt 10; $i++) {
  Start-Sleep -Seconds 1
  try {
    $r = Invoke-RestMethod -Uri "http://127.0.0.1:52801/health" -TimeoutSec 2
    Write-Host "Scan 2 PDF Bridge v$($r.version) is installed and running. It will start automatically at sign-in." -ForegroundColor Green
    exit 0
  } catch { }
}
Write-Warning "Installed, but the bridge didn't answer yet. Check the log: $env:LOCALAPPDATA\Scan2PdfBridge\logs\bridge.log"
