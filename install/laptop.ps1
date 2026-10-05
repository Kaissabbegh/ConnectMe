# ConnectMe: install or update on a Windows laptop (no admin needed). In PowerShell:
#
#   irm https://raw.githubusercontent.com/Kaissabbegh/ConnectMe/main/install/laptop.ps1 | iex
#
# Re-run to update. Downloads ConnectMe from the latest release and FFmpeg from gyan.dev (first time only, ~110 MB).
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$repo = 'Kaissabbegh/ConnectMe'
$dest = Join-Path $env:LOCALAPPDATA 'ConnectMe'
New-Item -ItemType Directory -Force $dest | Out-Null
Get-Process ConnectMe -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dest*" } | Stop-Process -Force
Get-Process ffmpeg -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dest*" } | Stop-Process -Force
Start-Sleep 1

Write-Host 'Downloading the latest ConnectMe...'
Invoke-WebRequest "https://github.com/$repo/releases/latest/download/ConnectMe.exe" -OutFile (Join-Path $dest 'ConnectMe.exe')

if (-not (Test-Path (Join-Path $dest 'ffmpeg.exe'))) {
    Write-Host 'Downloading FFmpeg (~110 MB, first time only)...'
    $tmp = Join-Path $env:TEMP 'connectme-ffmpeg'
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory $tmp | Out-Null
    $zip = Join-Path $tmp 'ffmpeg.zip'
    Invoke-WebRequest 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip' -OutFile $zip
    Expand-Archive $zip -DestinationPath $tmp -Force
    $ff = Get-ChildItem $tmp -Recurse -Filter ffmpeg.exe | Select-Object -First 1
    Copy-Item $ff.FullName $dest -Force
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

$exe = Join-Path $dest 'ConnectMe.exe'
$shell = New-Object -ComObject WScript.Shell
foreach ($folder in @([Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('Desktop'))) {
    $lnk = $shell.CreateShortcut((Join-Path $folder 'ConnectMe.lnk'))
    $lnk.TargetPath = $exe
    $lnk.WorkingDirectory = $dest
    $lnk.Save()
}

Write-Host ""
Write-Host "Installed ConnectMe to $dest (Desktop and Start menu shortcuts added). Starting it now."
Write-Host "For an extended second screen, also install the Virtual Display Driver:"
Write-Host "  https://github.com/VirtualDrivers/Virtual-Display-Driver/releases  (set the new screen to 2560x1440)"
Start-Process $exe -WorkingDirectory $dest
