$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$installerDir = Join-Path $root "installer"

Write-Host "Publishing self-contained win-x64 build..."
dotnet publish (Join-Path $root "src\SkyFireLauncher\SkyFireLauncher.csproj") `
    -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false `
    -o (Join-Path $installerDir "publish\win-x64")
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

Write-Host "Building installer..."
dotnet build (Join-Path $installerDir "SkyFireLauncherInstaller.wixproj") -c Release
if ($LASTEXITCODE -ne 0) { throw "Installer build failed" }

Write-Host "Done: $installerDir\bin\x64\Release\SkyFireLauncherSetup.msi"
