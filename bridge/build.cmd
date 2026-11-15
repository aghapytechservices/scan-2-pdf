@echo off
rem Builds Scan2PdfBridge.exe (self-contained, single file, no .NET needed on target PCs).
rem Requires the .NET 8 SDK on the build machine: https://dotnet.microsoft.com/download/dotnet/8.0
cd /d "%~dp0"
dotnet publish ScanBridge\ScanBridge.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true ^
  -o publish || exit /b 1
copy /y install.ps1 publish\ >nul
copy /y uninstall.ps1 publish\ >nul
echo.
echo Built: %~dp0publish\Scan2PdfBridge.exe
echo Install on this PC:  powershell -ExecutionPolicy Bypass -File publish\install.ps1 -SourceDir publish
