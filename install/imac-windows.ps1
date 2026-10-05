# ConnectMe Display: install or update on a Windows (Boot Camp) iMac.
# In PowerShell *as Administrator*:
#
#   & ([scriptblock]::Create((irm https://raw.githubusercontent.com/Kaissabbegh/ConnectMe/main/install/imac-windows.ps1))) -Name "Desk 12 iMac"
#
# Re-run to update. Downloads ConnectMeDisplay from the latest release and mpv from its official Windows builds.
param([string]$Name = $env:COMPUTERNAME)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$repo = 'Kaissabbegh/ConnectMe'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw 'Please run PowerShell as Administrator (right-click > Run as administrator).' }

$dest = Join-Path $env:ProgramFiles 'ConnectMe Display'
New-Item -ItemType Directory -Force $dest | Out-Null
Get-Process ConnectMeDisplay, mpv -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep 1

Write-Host 'Downloading the latest ConnectMe Display...'
Invoke-WebRequest "https://github.com/$repo/releases/latest/download/ConnectMeDisplay-win-x64.exe" -OutFile (Join-Path $dest 'ConnectMeDisplay.exe')

if (-not (Test-Path (Join-Path $dest 'mpv.exe'))) {
    Write-Host 'Downloading mpv (video player)...'
    $rel = Invoke-RestMethod 'https://api.github.com/repos/shinchiro/mpv-winbuild-cmake/releases/latest'
    $asset = $rel.assets | Where-Object { $_.name -match '^mpv-x86_64-\d{8}-git-[0-9a-f]+\.7z$' } | Select-Object -First 1
    if (-not $asset) { throw 'Could not find the mpv download. Get mpv.exe from https://mpv.io/installation/ and put it in ' + $dest }
    $tmp = Join-Path $env:TEMP 'connectme-mpv'
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory $tmp | Out-Null
    $archive = Join-Path $tmp 'mpv.7z'
    Invoke-WebRequest $asset.browser_download_url -OutFile $archive
    & "$env:SystemRoot\System32\tar.exe" -xf $archive -C $tmp
    if (-not (Test-Path (Join-Path $tmp 'mpv.exe'))) {
        throw "Couldn't unpack mpv (older Windows can't open .7z). Unpack $archive with 7-Zip and copy mpv.exe to $dest, then re-run."
    }
    Copy-Item (Join-Path $tmp 'mpv.exe') $dest -Force
    Get-ChildItem $tmp -Filter *.dll | Copy-Item -Destination $dest -Force
}

$exe = Join-Path $dest 'ConnectMeDisplay.exe'

# Firewall: laptops reach this iMac on TCP 47800 (video) and UDP 5353 (discovery); private/domain networks only.
foreach ($rule in 'ConnectMe Display (TCP)', 'ConnectMe Display (mDNS)') {
    Get-NetFirewallRule -DisplayName $rule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
}
New-NetFirewallRule -DisplayName 'ConnectMe Display (TCP)' -Direction Inbound -Program $exe -Protocol TCP -LocalPort 47800 -Action Allow -Profile Domain,Private | Out-Null
New-NetFirewallRule -DisplayName 'ConnectMe Display (mDNS)' -Direction Inbound -Program $exe -Protocol UDP -LocalPort 5353 -Action Allow -Profile Domain,Private | Out-Null

# Start at login for every user, plus a Start menu entry.
$shell = New-Object -ComObject WScript.Shell
foreach ($folder in @([Environment]::GetFolderPath('CommonStartup'), [Environment]::GetFolderPath('CommonPrograms'))) {
    $lnk = $shell.CreateShortcut((Join-Path $folder 'ConnectMe Display.lnk'))
    $lnk.TargetPath = $exe
    $lnk.Arguments = "--name `"$Name`""
    $lnk.WorkingDirectory = $dest
    $lnk.Save()
}

Write-Host ""
Write-Host "Installed to $dest as `"$Name`". It starts at login; starting it now."
Write-Host "If Windows lists the office network as 'Public', switch it to 'Private' or laptops can't connect."
Start-Process $exe -ArgumentList "--name `"$Name`"" -WorkingDirectory $dest
