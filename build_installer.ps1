$ErrorActionPreference = "Stop"
$env:PATH = "C:\Users\jahoda\scoop\apps\dotnet-sdk\current;$env:PATH"

$root = $PSScriptRoot
$publishAppDir = Join-Path $root "artifacts\app_publish"
$payloadZip = Join-Path $root "src\GPhotosSync.Installer\payload.zip"
$installerOutDir = Join-Path $root "artifacts\installer"
$finalSetupExe = Join-Path $root "GPhotosSync_Setup.exe"

Write-Host "1. Kompiluji hlavní aplikaci GPhotosSync..." -ForegroundColor Cyan
if (Test-Path $publishAppDir) { Remove-Item $publishAppDir -Recurse -Force }
dotnet publish (Join-Path $root "src\GPhotosSync\GPhotosSync.csproj") -c Release -r win-x64 --self-contained false -o $publishAppDir

Write-Host "2. Vytvářím instalační archiv payload.zip..." -ForegroundColor Cyan
if (Test-Path $payloadZip) { Remove-Item $payloadZip -Force }
Compress-Archive -Path "$publishAppDir\*" -DestinationPath $payloadZip -CompressionLevel Optimal

Write-Host "3. Kompiluji Windows instalátor GPhotosSync_Setup.exe..." -ForegroundColor Cyan
if (Test-Path $installerOutDir) { Remove-Item $installerOutDir -Recurse -Force }
dotnet publish (Join-Path $root "src\GPhotosSync.Installer\GPhotosSync.Installer.csproj") -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $installerOutDir

Copy-Item (Join-Path $installerOutDir "GPhotosSync_Setup.exe") $finalSetupExe -Force
Write-Host "Hotovo! Instalační program vytvořen: $finalSetupExe" -ForegroundColor Green
