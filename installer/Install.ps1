<#
 Dukkan - per-user installer. No internet, no administrator rights.
 Copies the application to %LocalAppData%\Programs\Dukkan and creates Start-menu and Desktop shortcuts.
 Business data lives separately in %LocalAppData%\Dukkan and is never touched by install or update.
#>
[CmdletBinding()]
param([string]$Target = (Join-Path $env:LOCALAPPDATA 'Programs\Dukkan'))

$ErrorActionPreference = 'Stop'
$Target = [IO.Path]::GetFullPath($Target)
$defaultTarget = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\Dukkan'))
$businessData = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Dukkan'))
if ($Target.TrimEnd('\') -eq [IO.Path]::GetPathRoot($Target).TrimEnd('\') -or
    $Target.TrimEnd('\') -eq [Environment]::GetFolderPath('UserProfile').TrimEnd('\') -or
    $businessData.StartsWith($Target.TrimEnd('\')+'\', [StringComparison]::OrdinalIgnoreCase) -or
    $Target.Equals($businessData, [StringComparison]::OrdinalIgnoreCase) -or
    $Target.StartsWith($businessData+'\', [StringComparison]::OrdinalIgnoreCase) -or
    $Target.StartsWith([IO.Path]::GetFullPath($PSScriptRoot)+'\', [StringComparison]::OrdinalIgnoreCase) -or
    $Target.Equals([IO.Path]::GetFullPath($PSScriptRoot), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Choose a dedicated program folder outside the package and business data.'
}
$source = Join-Path $PSScriptRoot 'app'
if (-not (Test-Path (Join-Path $source 'Dukkan.exe'))) { throw "Application files not found next to the installer ($source)." }

# Verify the package against the shipped checksums before installing (authenticity of the copy on this media).
$manifest = Join-Path $PSScriptRoot 'SHA256SUMS.txt'
if (Test-Path $manifest) {
    foreach ($line in Get-Content $manifest) {
        if ($line -match '^([0-9A-Fa-f]{64})\s+\*?(.+)$') {
            $file = Join-Path $source $Matches[2]
            if (-not (Test-Path $file) -or (Get-FileHash $file -Algorithm SHA256).Hash -ne $Matches[1].ToUpper()) {
                throw "Checksum mismatch for $($Matches[2]). The installer package is damaged; do not install."
            }
        }
    }
    Write-Host 'Package checksums verified.'
}

if (Get-Process -Name 'Dukkan' -ErrorAction SilentlyContinue) {
    throw 'Dukkan is running. Close it (and finish any open sale) before installing or updating.'
}

# Version handling: the new package's version versus what is installed. A newer database layout cannot be opened by an
# older program, so installing an older version over a newer one is refused (the shop's data would become unreadable).
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Dukkan'
$newVersion = [version](Get-Item (Join-Path $source 'Dukkan.exe')).VersionInfo.FileVersion
$oldVersion = $null
$installedExe = Join-Path $Target 'Dukkan.exe'
if (Test-Path -LiteralPath $installedExe) { try { $oldVersion = [version](Get-Item -LiteralPath $installedExe).VersionInfo.FileVersion } catch { } }
# The first public release restarts numbering after private builds using the same schema.
# Permit only these known transitions; all other downgrades remain refused.
$publicVersionReset = $newVersion -eq [version]'1.0.0.0' -and
    $oldVersion -in @([version]'1.4.2.0', [version]'1.4.3.0')
if ($oldVersion -and $newVersion -lt $oldVersion -and -not $publicVersionReset) {
    throw "Dukkan $oldVersion is already installed; this package is the older version $newVersion. Install the newer package instead (older programs cannot open newer data)."
}
if ($oldVersion) { Write-Host "Updating Dukkan $oldVersion -> $newVersion (your data is kept)." } else { Write-Host "Installing Dukkan $newVersion." }

# An earlier build was called Kaboky: remove its program files and shortcuts (its data folder is moved by the new app on first start).
if (Get-Process -Name 'Kaboky' -ErrorAction SilentlyContinue) { throw 'The previous version (Kaboky) is running. Close it before installing.' }
if ($Target.Equals($defaultTarget, [StringComparison]::OrdinalIgnoreCase)) {
    $oldTarget = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\Kaboky'))
    if ($oldTarget -ne [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\Kaboky'))) { throw 'Unexpected legacy program path' }
    if (Test-Path -LiteralPath $oldTarget) { Remove-Item -LiteralPath $oldTarget -Recurse -Force }
    foreach ($dir in @([Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('Desktop'))) {
        Remove-Item -LiteralPath (Join-Path $dir 'Kaboky.lnk') -ErrorAction SilentlyContinue
    }
}

New-Item -ItemType Directory -Force -Path $Target | Out-Null
# Replace program files only; the data folder is separate.
if ((Get-ChildItem -LiteralPath $Target -Force | Measure-Object).Count -gt 0 -and -not (Test-Path -LiteralPath (Join-Path $Target 'Dukkan.exe'))) {
    throw 'The selected folder is not empty and does not contain an existing Dukkan installation.'
}
Get-ChildItem $Target -Force | Remove-Item -Recurse -Force
Copy-Item (Join-Path $source '*') $Target -Recurse -Force
Copy-Item (Join-Path $PSScriptRoot 'Uninstall.ps1') $Target -Force

$exe = Join-Path $Target 'Dukkan.exe'
$shell = New-Object -ComObject WScript.Shell
foreach ($dir in @([Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('Desktop'))) {
    $lnk = $shell.CreateShortcut((Join-Path $dir 'Dukkan.lnk'))
    $lnk.TargetPath = $exe
    $lnk.WorkingDirectory = $Target
    $lnk.IconLocation = "$exe,0"
    $lnk.Save()
}

# Register in Windows "Installed apps" (Settings > Apps) so it can be found, updated and removed like any other program.
New-Item -Path $uninstallKey -Force | Out-Null
$uninstaller = Join-Path $Target 'Uninstall.ps1'
$uninstallCmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$uninstaller`""
$sizeKb = [int]((Get-ChildItem -LiteralPath $Target -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
$props = @{
    DisplayName          = 'Dukkan'
    DisplayVersion       = $newVersion.ToString(3)
    Publisher            = 'Dukkan'
    DisplayIcon          = "$exe,0"
    InstallLocation      = $Target
    InstallDate          = (Get-Date).ToString('yyyyMMdd')
    UninstallString      = $uninstallCmd
    QuietUninstallString = "$uninstallCmd -Quiet"
}
foreach ($k in $props.Keys) { New-ItemProperty -Path $uninstallKey -Name $k -Value $props[$k] -PropertyType String -Force | Out-Null }
New-ItemProperty -Path $uninstallKey -Name 'EstimatedSize' -Value $sizeKb -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name 'NoModify' -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name 'NoRepair' -Value 1 -PropertyType DWord -Force | Out-Null

# "Start with Windows" (set inside the app) points at the program path; keep it valid after an update.
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if ((Get-ItemProperty -Path $runKey -Name 'Dukkan' -ErrorAction SilentlyContinue)) { Set-ItemProperty -Path $runKey -Name 'Dukkan' -Value "`"$exe`"" }

Write-Host "Installed to $Target"
Write-Host "Business data folder (kept on update/uninstall): $(Join-Path $env:LOCALAPPDATA 'Dukkan')"

