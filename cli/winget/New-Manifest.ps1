<#
.SYNOPSIS
  Writes the winget manifest for one release of Reothor.Elevate.CLI from the zip archives' SHA-256 hashes.

.DESCRIPTION
  Fills the three templates in this directory (version, installer, locale) and writes them to
  manifests\r\Reothor\Elevate.CLI\<version>\, the layout microsoft/winget-pkgs expects. The CLI is
  a portable package: winget unpacks the zip and puts `elevate` on the PATH through its links
  directory. Validate the result with `winget validate manifests\r\Reothor\Elevate.CLI\<version>`.

.PARAMETER Version
  The release version, e.g. 1.3.0.

.PARAMETER X64Sha256
  SHA-256 of elevate-cli-<version>-win-x64.zip (from the .sha256 file next to it).

.PARAMETER Arm64Sha256
  SHA-256 of elevate-cli-<version>-win-arm64.zip.

.PARAMETER Repository
  owner/name of the GitHub repository the release lives in.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Version,
    [Parameter(Mandatory)] [string]$X64Sha256,
    [Parameter(Mandatory)] [string]$Arm64Sha256,
    [string]$Repository = "FrodeHus/elevate",
    [string]$ReleaseDate = (Get-Date -Format "yyyy-MM-dd")
)

$ErrorActionPreference = "Stop"
$target = Join-Path $PSScriptRoot "manifests\r\Reothor\Elevate.CLI\$Version"
New-Item -ItemType Directory -Force -Path $target | Out-Null

$values = @{
    VERSION      = $Version
    RELEASE_DATE = $ReleaseDate
    X64_URL      = "https://github.com/$Repository/releases/download/v$Version/elevate-cli-$Version-win-x64.zip"
    ARM64_URL    = "https://github.com/$Repository/releases/download/v$Version/elevate-cli-$Version-win-arm64.zip"
    X64_SHA256   = $X64Sha256.ToUpperInvariant()
    ARM64_SHA256 = $Arm64Sha256.ToUpperInvariant()
}

foreach ($name in "Reothor.Elevate.CLI.yaml", "Reothor.Elevate.CLI.installer.yaml", "Reothor.Elevate.CLI.locale.en-US.yaml") {
    $text = Get-Content -Raw (Join-Path $PSScriptRoot "templates\$name")
    foreach ($pair in $values.GetEnumerator()) {
        $text = $text.Replace("{{" + $pair.Key + "}}", $pair.Value)
    }
    Set-Content -Path (Join-Path $target $name) -Value $text -Encoding utf8 -NoNewline
}

Write-Host "Manifest written to $target"
