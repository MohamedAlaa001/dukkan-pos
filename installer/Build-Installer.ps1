<#
 Builds the distribution into dist\Dukkan-Setup: a self-contained win-x64 publish (the .NET runtime is
 bundled, so the shop PC needs nothing else), checksums, install/uninstall scripts and a Setup.cmd launcher.
 Build machine needs internet for NuGet restore once; the resulting folder installs without internet.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root 'dist\Dukkan-Setup'
$dist = [IO.Path]::GetFullPath($dist)
if ($dist -ne [IO.Path]::GetFullPath((Join-Path $root 'dist\Dukkan-Setup'))) { throw 'Unexpected distribution path' }
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $dist 'app') | Out-Null

& dotnet publish (Join-Path $root 'src\Dukkan.UI\Dukkan.UI.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:DebugType=none -o (Join-Path $dist 'app')
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Get-ChildItem (Join-Path $dist 'app') -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring((Join-Path $dist 'app').Length + 1)
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash, $rel
} | Set-Content (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii

Copy-Item (Join-Path $PSScriptRoot 'Install.ps1'), (Join-Path $PSScriptRoot 'Uninstall.ps1') $dist
Copy-Item (Join-Path $root 'docs\USER_GUIDE.md') $dist
Set-Content (Join-Path $dist 'Setup.cmd') '@powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1"
@pause' -Encoding ascii
Write-Host "Built $dist"

