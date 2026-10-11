# Installs HanYeong for the current user (no admin rights needed):
# copies the exe to %LOCALAPPDATA%\Programs\HanYeong, adds a Start menu shortcut,
# registers start-with-Windows and launches it. Run again to update.
#   .\tools\install.ps1                 # uses dist\HanYeong.exe (needs .NET 10 Desktop Runtime)
#   .\tools\install.ps1 -Standalone     # uses dist\HanYeong-standalone.exe
param([switch]$Standalone, [string]$Source)
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
if (-not $Source) { $Source = if ($Standalone) { "$root\dist\HanYeong-standalone.exe" } else { "$root\dist\HanYeong.exe" } }
if (-not (Test-Path $Source)) { throw "$Source not found - run tools\publish.ps1 first." }

$dir = Join-Path $env:LOCALAPPDATA 'Programs\HanYeong'
$exe = Join-Path $dir 'HanYeong.exe'

# Stop a running copy so the file can be replaced.
Get-Process HanYeong -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
Start-Sleep -Milliseconds 300

New-Item -ItemType Directory -Force $dir | Out-Null
Copy-Item $Source $exe -Force

$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) '한영 변환기.lnk'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $dir
$link.Description = '한/영 잘못 친 글자 변환'
$link.Save()

# Same value the settings window toggles.
Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'HanYeongConverter' -Value "`"$exe`""

Start-Process $exe
"Installed: $exe"
"Start menu: $shortcut"
"Starts with Windows: yes (change in the tray menu > 설정)"
