# Builds everything that ships into ..\dist\
#   dist\laptop\              ConnectMe.exe            (+ put ffmpeg.exe here)
#   dist\imac-windows\        ConnectMeDisplay.exe     (+ put mpv.exe here), install-windows.ps1
#   dist\imac-linux\          ConnectMeDisplay, install-linux.sh
# The macOS iMac app is built on the iMac itself: mac/ConnectMeDisplay/build.sh
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$dist = Join-Path (Split-Path $PSScriptRoot) 'dist'
$common = @('-c', 'Release', '--self-contained', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=none', '-p:EnableCompressionInSingleFile=true')

dotnet publish ConnectMe.Sender  -r win-x64   @common -o "$dist\laptop"
dotnet publish ConnectMe.Display -r win-x64   @common -o "$dist\imac-windows"
dotnet publish ConnectMe.Display -r linux-x64 @common -o "$dist\imac-linux"

Copy-Item ..\install\imac-windows.ps1 "$dist\imac-windows\install-windows.ps1" -Force
Copy-Item ..\install\linux.sh "$dist\imac-linux\install-linux.sh" -Force

Write-Host "`nDone. Remember to add ffmpeg.exe to dist\laptop and mpv.exe to dist\imac-windows."

