<#
.SYNOPSIS
  Builds the papertrail MSI (win-x86), optionally signing Papertrail.exe and the MSI.

.DESCRIPTION
  1. dotnet publish src/Papertrail.App (framework-dependent, single file, win-x86)
  2. signtool sign Papertrail.exe        (when a certificate is given)
  3. wix build installer/Papertrail.wxs  (WiX 5.0.2 local tool, see .config/dotnet-tools.json)
  4. wix msi validate                    (ICE checks)
  5. signtool sign the MSI               (when a certificate is given)

  Output: artifacts/msi/papertrail-<version>-x86.msi

  Signing uses one of:
    -CertThumbprint (or PAPERTRAIL_SIGN_CERT_THUMBPRINT)   certificate in the current user's or machine's store
    -PfxPath (or PAPERTRAIL_SIGN_PFX) with -PfxPassword (or PAPERTRAIL_SIGN_PFX_PASSWORD)
  The timestamp server comes from -TimestampUrl (or PAPERTRAIL_SIGN_TIMESTAMP_URL); the default is
  DigiCert's. signtool is found in the Windows 10/11 SDK, or set SIGNTOOL to its full path.
  Without a certificate the build is unsigned, unless -RequireSigning is given (use that in release CI).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts/build-msi.ps1

.EXAMPLE
  $env:PAPERTRAIL_SIGN_PFX = 'C:\secure\pursuant.pfx'; $env:PAPERTRAIL_SIGN_PFX_PASSWORD = '...'
  powershell -ExecutionPolicy Bypass -File scripts/build-msi.ps1 -RequireSigning
#>
param(
    [string]$Version,
    [string]$Configuration = 'Release',
    [string]$CertThumbprint = $env:PAPERTRAIL_SIGN_CERT_THUMBPRINT,
    [string]$PfxPath = $env:PAPERTRAIL_SIGN_PFX,
    [string]$PfxPassword = $env:PAPERTRAIL_SIGN_PFX_PASSWORD,
    [string]$TimestampUrl = $(if ($env:PAPERTRAIL_SIGN_TIMESTAMP_URL) { $env:PAPERTRAIL_SIGN_TIMESTAMP_URL } else { 'http://timestamp.digicert.com' }),
    [switch]$RequireSigning,
    [switch]$SkipValidation
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$publishDir = Join-Path $root 'artifacts\publish\win-x86'
$msiDir = Join-Path $root 'artifacts\msi'

function Invoke-Checked([string]$File, [string[]]$Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File $($Arguments -join ' ') failed with exit code $LASTEXITCODE." }
}

function Find-SignTool {
    if ($env:SIGNTOOL) { return $env:SIGNTOOL }
    $onPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $found = Get-ChildItem -Path $kits -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -eq 'x64' } |
        Sort-Object { $_.Directory.Parent.Name } -Descending |
        Select-Object -First 1
    if (-not $found) { throw 'signtool.exe not found. Install the Windows SDK signing tools or set SIGNTOOL.' }
    return $found.FullName
}

$signing = [bool]($CertThumbprint -or $PfxPath)
if ($RequireSigning -and -not $signing) {
    throw 'Signing is required but no certificate was given (set PAPERTRAIL_SIGN_CERT_THUMBPRINT or PAPERTRAIL_SIGN_PFX).'
}

function Invoke-Sign([string]$Path, [string]$Description) {
    if (-not $signing) {
        Write-Warning "Not signing $Path (no certificate given)."
        return
    }
    $signArgs = @('sign', '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256', '/d', $Description)
    if ($CertThumbprint) {
        $signArgs += @('/sha1', $CertThumbprint)
    } else {
        $signArgs += @('/f', $PfxPath)
        if ($PfxPassword) { $signArgs += @('/p', $PfxPassword) }
    }
    Invoke-Checked (Find-SignTool) ($signArgs + $Path)
    Invoke-Checked (Find-SignTool) @('verify', '/pa', $Path)
}

Push-Location $root
try {
    if (-not $Version) {
        $Version = (Invoke-Checked dotnet @('msbuild', 'src/Papertrail.App/Papertrail.App.csproj', '-getProperty:Version') | Select-Object -Last 1).Trim()
    }

    Remove-Item -Recurse -Force $publishDir -ErrorAction SilentlyContinue
    Invoke-Checked dotnet @('publish', 'src/Papertrail.App', '-c', $Configuration, '-o', $publishDir, "-p:Version=$Version")
    $exe = Join-Path $publishDir 'Papertrail.exe'
    Invoke-Sign $exe 'papertrail'

    Invoke-Checked dotnet @('tool', 'restore')
    New-Item -ItemType Directory -Force -Path $msiDir | Out-Null
    $msi = Join-Path $msiDir "papertrail-$Version-x86.msi"
    Invoke-Checked dotnet @('wix', 'build', 'installer/Papertrail.wxs', '-arch', 'x86',
        '-d', "ProductVersion=$Version", '-d', "PublishDir=$publishDir", '-o', $msi)
    if (-not $SkipValidation) {
        Invoke-Checked dotnet @('wix', 'msi', 'validate', $msi)
    }
    Invoke-Sign $msi 'papertrail installer'

    Write-Host "Built $msi ($([math]::Round((Get-Item $msi).Length / 1KB)) KB)"
} finally {
    Pop-Location
}
