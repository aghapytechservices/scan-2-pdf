<#
  Installs the TEST build of the Store package (Scan2PdfBridge-Test.msix) on this PC so you can try the
  Microsoft Store edition before submitting it. Run from an elevated (Administrator) PowerShell, in the folder
  that contains Scan2PdfBridge-Test.msix and Scan2PdfBridge-Test.cer.

  The test certificate is only used to let Windows install the package locally. The real Store package is
  signed by Microsoft. Remove the test version (Settings → Apps) before installing from the Store.
#>
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot

$cer = Join-Path $here "Scan2PdfBridge-Test.cer"
$msix = Join-Path $here "Scan2PdfBridge-Test.msix"

# Stop the standalone (non-Store) bridge if it's running, so both don't fight over port 52801.
Get-Process Scan2PdfBridge -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "Scan2PdfBridge" -ErrorAction SilentlyContinue

Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
Add-AppxPackage -Path $msix -ForceApplicationShutdown
Write-Host "Installed. Start 'Scan 2 PDF Bridge' from the Start menu." -ForegroundColor Green
