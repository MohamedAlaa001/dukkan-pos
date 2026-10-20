<#
 Builds a single Dukkan-Setup.exe (a self-extracting installer using Windows' built-in IExpress) from dist\Dukkan-Setup.
 Run Build-Installer.ps1 first. Output: dist\Dukkan-Setup.exe. Double-clicking it extracts to a temp folder and runs Install.ps1.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$pkg = Join-Path $root 'dist\Dukkan-Setup'
if (-not (Test-Path (Join-Path $pkg 'Install.ps1'))) { throw 'Run installer\Build-Installer.ps1 first.' }

$work = [IO.Path]::GetFullPath((Join-Path $env:TEMP ('dukkan-sfx-' + [guid]::NewGuid().ToString('N'))))
$workPrefix = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\dukkan-sfx-'
if (-not $work.StartsWith($workPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected setup build directory' }
New-Item -ItemType Directory -Path $work | Out-Null
try {
    Compress-Archive -Path (Join-Path $pkg '*') -DestinationPath (Join-Path $work 'payload.zip') -CompressionLevel Optimal

    @'
@echo off
setlocal
set "T=%TEMP%\DukkanSetup-%RANDOM%-%RANDOM%"
if exist "%T%" ( echo Temporary folder collision. Please run setup again. & pause & exit /b 1 )
powershell -NoProfile -ExecutionPolicy Bypass -Command "Expand-Archive -LiteralPath '%~dp0payload.zip' -DestinationPath '%T%' -Force"
if errorlevel 1 ( echo Could not unpack the installer. & pause & exit /b 1 )
powershell -NoProfile -ExecutionPolicy Bypass -File "%T%\Install.ps1"
echo.
if errorlevel 1 ( echo Installation failed. ) else ( echo Dukkan is installed. It is listed in Settings, Apps, Installed apps. & start "" "%LOCALAPPDATA%\Programs\Dukkan\Dukkan.exe" )
rd /s /q "%T%" 2>nul
pause
'@ | Set-Content (Join-Path $work 'run.cmd') -Encoding ascii

    $out = Join-Path $root 'dist\Dukkan-Setup.exe'
    if (Test-Path $out) { Remove-Item $out -Force }
    $sed = @"
[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=0
HideExtractAnimation=0
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=
DisplayLicense=
FinishMessage=
TargetName=$out
FriendlyName=Dukkan Setup
AppLaunched=cmd /c ".\run.cmd"
PostInstallCmd=<None>
AdminQuietInstCmd=
UserQuietInstCmd=
SourceFiles=SourceFiles
[SourceFiles]
SourceFiles0=$work\
[SourceFiles0]
%FILE0%=
%FILE1%=
[Strings]
FILE0="run.cmd"
FILE1="payload.zip"
"@
    $sedPath = Join-Path $work 'dukkan.sed'
    Set-Content $sedPath $sed -Encoding ascii
    $p = Start-Process iexpress.exe -ArgumentList '/N','/Q',$sedPath -Wait -PassThru -WindowStyle Hidden
    if (-not (Test-Path $out)) { throw "IExpress did not produce $out (exit $($p.ExitCode))." }
    # IExpress gives the installer a generic icon: stamp the Dukkan icon on it.
    # The file can stay locked for a moment after IExpress exits (scanners, indexer): retry a few times.
    for ($try = 1; $try -le 8; $try++) {
        try { & (Join-Path $PSScriptRoot 'Set-ExeIcon.ps1') -Exe $out -Icon (Join-Path $root 'src\Dukkan.UI\Assets\dukkan.ico'); break }
        catch { if ($try -eq 8) { throw }; Start-Sleep -Seconds 3 }
    }
    Write-Host ("Built {0} ({1:N0} MB)" -f $out, ((Get-Item $out).Length / 1MB))
}
finally {
    if (-not ([IO.Path]::GetFullPath($work)).StartsWith($workPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe setup cleanup path' }
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
