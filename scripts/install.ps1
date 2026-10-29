<#
.SYNOPSIS
  Per-user install of papertrail (no administrator rights needed).

.DESCRIPTION
  Copies a published build to %LOCALAPPDATA%\Programs\Papertrail, registers the papertrail://
  URL handler and sign-in auto-start under HKCU, adds a Start menu shortcut and starts the app.
  Interim installer until the Velopack-based one lands.

.EXAMPLE
  dotnet publish src/Papertrail.App -c Release -r win-x86 --self-contained -o artifacts/publish/win-x86
  powershell -ExecutionPolicy Bypass -File scripts/install.ps1
#>
param(
    [string]$Source = (Join-Path $PSScriptRoot '..\artifacts\publish\win-x86'),
    [switch]$NoAutoStart
)

$ErrorActionPreference = 'Stop'

$sourceExe = Join-Path $Source 'Papertrail.exe'
if (-not (Test-Path $sourceExe)) {
    throw "No build found at $Source. Run: dotnet publish src/Papertrail.App -c Release -r win-x86 --self-contained -o artifacts/publish/win-x86"
}

$target = Join-Path $env:LOCALAPPDATA 'Programs\Papertrail'
$exe = Join-Path $target 'Papertrail.exe'

Get-Process -Name Papertrail -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $exe } |
    ForEach-Object { $_.CloseMainWindow() | Out-Null; Start-Sleep -Milliseconds 500; if (-not $_.HasExited) { $_.Kill() } }

New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item -Path (Join-Path $Source '*') -Destination $target -Recurse -Force

$registerArgs = @('--register', '--quiet')
if (-not $NoAutoStart) { $registerArgs += '--autostart' }
$p = Start-Process -FilePath $exe -ArgumentList $registerArgs -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "Registration failed with exit code $($p.ExitCode)." }

$shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'Papertrail.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $target
$shortcut.Description = 'papertrail - scan from web pages'
$shortcut.Save()

Start-Process -FilePath $exe
Write-Host "papertrail installed to $target and running (look for the tray icon)."
