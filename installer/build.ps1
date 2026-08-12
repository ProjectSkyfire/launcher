$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$installerDir = Join-Path $root "installer"

# Every build must carry a strictly higher version than the last, or MSI treats
# a reinstall as a no-op (skips both the file copy and the major-upgrade path).
$versionFile = Join-Path $installerDir "version.txt"
$buildNumber = ([int](Get-Content $versionFile -Raw)) + 1
Set-Content -Path $versionFile -Value $buildNumber -NoNewline
$version = "1.0.$buildNumber"
Write-Host "Building version $version..."

Write-Host "Publishing self-contained win-x64 build..."
dotnet publish (Join-Path $root "src\SkyFireLauncher\SkyFireLauncher.csproj") `
    -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false `
    -p:Version=$version `
    -o (Join-Path $installerDir "publish\win-x64")
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

Write-Host "Building installer..."
dotnet build (Join-Path $installerDir "SkyFireLauncherInstaller.wixproj") -c Release -p:ProductVersion=$version
if ($LASTEXITCODE -ne 0) { throw "Installer build failed" }

Write-Host "Done: $installerDir\bin\x64\Release\SkyFireLauncherSetup.msi (version $version)"
