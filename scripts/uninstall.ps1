<#
.SYNOPSIS
  Removes a per-user papertrail install made by install.ps1.
  Keeps %APPDATA%\Papertrail (approved websites) unless -RemoveSettings is given.
#>
param([switch]$RemoveSettings)

$ErrorActionPreference = 'Stop'

$target = Join-Path $env:LOCALAPPDATA 'Programs\Papertrail'
$exe = Join-Path $target 'Papertrail.exe'

if (Test-Path $exe) {
    Start-Process -FilePath $exe -ArgumentList '--unregister', '--quiet' -Wait
}

Get-Process -Name Papertrail -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $exe } |
    ForEach-Object { $_.Kill(); $_.WaitForExit(5000) | Out-Null }

Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path ([Environment]::GetFolderPath('Programs')) 'Papertrail.lnk')
Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $target
Remove-Item -Recurse -Force -ErrorAction SilentlyContinue (Join-Path $env:LOCALAPPDATA 'Papertrail')
if ($RemoveSettings) {
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue (Join-Path $env:APPDATA 'Papertrail')
}

Write-Host 'papertrail removed.'
