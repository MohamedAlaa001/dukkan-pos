<#
 Removes the program files, shortcuts, the "start with Windows" entry and the Installed-apps entry.
 Business data is NEVER deleted by this script unless you run it from a console with -DeleteBusinessData
 and type the confirmation phrase - a separate, deliberate destructive action.
 Windows (Settings > Apps) runs this script hidden: it asks for confirmation in a message box and keeps the data.
#>
[CmdletBinding()]
param([switch]$DeleteBusinessData, [switch]$Quiet)

$ErrorActionPreference = 'Stop'
$target = $PSScriptRoot
$dataFolder = Join-Path $env:LOCALAPPDATA 'Dukkan'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Dukkan'

function Say([string]$text, [string]$icon = 'Information') {
    if ($Quiet) { Write-Host $text; return }
    Add-Type -AssemblyName System.Windows.Forms
    [void][System.Windows.Forms.MessageBox]::Show($text, 'Dukkan', 'OK', $icon)
}

if (Get-Process -Name 'Dukkan' -ErrorAction SilentlyContinue) {
    Say 'Dukkan is running. Close it (and finish any open sale), then uninstall again.' 'Warning'
    throw 'Close Dukkan first.'
}

if (-not $Quiet -and -not $DeleteBusinessData) {
    Add-Type -AssemblyName System.Windows.Forms
    $ask = [System.Windows.Forms.MessageBox]::Show("Remove Dukkan from this PC?`n`nYour sales, stock, customers and backups are kept in:`n$dataFolder`nand are available again if you reinstall.", 'Uninstall Dukkan', 'YesNo', 'Question', 'Button2')
    if ($ask -ne 'Yes') { return }
}

foreach ($dir in @([Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('Desktop'))) {
    Remove-Item (Join-Path $dir 'Dukkan.lnk') -ErrorAction SilentlyContinue
}
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Dukkan' -ErrorAction SilentlyContinue
Remove-Item -Path $uninstallKey -Recurse -Force -ErrorAction SilentlyContinue

if ($DeleteBusinessData) {
    $answer = Read-Host "This permanently deletes ALL sales, stock, customer and backup data in $dataFolder. Type DELETE MY DATA to continue"
    if ($answer -ceq 'DELETE MY DATA') { Remove-Item $dataFolder -Recurse -Force; Write-Host 'Business data deleted.' }
    else { Write-Host 'Confirmation did not match. Business data kept.' }
} else {
    Write-Host 'Business data kept.'
}

# Remove program files after this script exits (it lives inside the folder it removes).
Start-Process powershell -WindowStyle Hidden -ArgumentList '-NoProfile','-Command',"Start-Sleep 2; Remove-Item -LiteralPath '$target' -Recurse -Force"
if (-not $Quiet) { Say "Dukkan was removed.`nYour business data is still in $dataFolder." }
Write-Host 'Dukkan removed.'
