# Builds the two release files into dist\:
#   HanYeong.exe             small, needs the .NET 10 Desktop Runtime installed
#   HanYeong-standalone.exe  larger, runs on any 64-bit Windows 10/11 without installing anything
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = "$root\src\HanYeong"
$dist = "$root\dist"
$common = @('-c', 'Release', '-r', 'win-x64', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=none')

Remove-Item $dist -Recurse -Force -ErrorAction SilentlyContinue

dotnet publish $project @common --self-contained false -o "$dist\fd"
if ($LASTEXITCODE -ne 0) { throw 'publish (framework-dependent) failed' }
dotnet publish $project @common --self-contained true -p:EnableCompressionInSingleFile=true -o "$dist\sc"
if ($LASTEXITCODE -ne 0) { throw 'publish (standalone) failed' }

Move-Item "$dist\fd\HanYeong.exe" "$dist\HanYeong.exe"
Move-Item "$dist\sc\HanYeong.exe" "$dist\HanYeong-standalone.exe"
Remove-Item "$dist\fd", "$dist\sc" -Recurse -Force
Get-ChildItem $dist | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
