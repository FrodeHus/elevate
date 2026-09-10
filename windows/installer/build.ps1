<#
.SYNOPSIS
  Publishes Elevate and the CLI for Windows and builds the per-user MSI for each architecture.

.DESCRIPTION
  For every architecture: publishes the app and the CLI, then `wix build` of Elevate.wxs into
  out\Elevate-<version>-<arch>.msi with a .sha256 next to it; the MSI installs both and puts the
  folder on the user's PATH. Signs the MSI and elevate.exe with signtool through Azure Trusted
  Signing when the signing variables are set (see -Sign). Needs the .NET 10 SDK and WiX v5
  (`dotnet tool install --global wix --version 5.0.2`).

.PARAMETER Version
  The product version, e.g. 1.0.0. Also stamped into the assembly.

.PARAMETER Architectures
  x64, arm64, or both (the default).

.PARAMETER Sign
  Sign the MSIs with Azure Trusted Signing. Requires the environment variables
  AZURE_TRUSTED_SIGNING_ENDPOINT, AZURE_TRUSTED_SIGNING_ACCOUNT, AZURE_TRUSTED_SIGNING_PROFILE and an
  Azure identity (AZURE_CLIENT_ID / AZURE_TENANT_ID / AZURE_CLIENT_SECRET or a logged-in az CLI),
  plus the Microsoft.Trusted.Signing.Client dlib on the path given by AZURE_TRUSTED_SIGNING_DLIB.
#>
[CmdletBinding()]
param(
    [string]$Version = "1.0.0",
    [ValidateSet("x64", "arm64")]
    [string[]]$Architectures = @("x64", "arm64"),
    [string]$Configuration = "Release",
    [switch]$Sign
)

$ErrorActionPreference = "Stop"
# WiX v5: v6 and later require accepting the Open Source Maintenance Fee EULA, a decision for the maintainers.
$WixVersion = "5.0.2"
$root = Split-Path -Parent $PSScriptRoot
$installer = $PSScriptRoot
$project = Join-Path $root "src\Elevate.App\Elevate.App.csproj"
$cliProject = Join-Path $root "..\cli\src\Elevate.Cli\Elevate.Cli.csproj"
$out = Join-Path $installer "out"
New-Item -ItemType Directory -Force -Path $out | Out-Null

if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    throw "The WiX toolset is not installed: run `dotnet tool install --global wix --version $WixVersion`."
}
# The UI extension provides the exit dialog with the launch checkbox; its major version must match the tool's.
wix extension add --global "WixToolset.UI.wixext/$WixVersion" 2>&1 | Out-Null

function Sign-File([string]$Path, [string]$Arch) {
    Write-Host "== Signing $Path"
    $dlib = $env:AZURE_TRUSTED_SIGNING_DLIB
    $metadata = Join-Path $out "signing-$Arch.json"
    @{
        Endpoint = $env:AZURE_TRUSTED_SIGNING_ENDPOINT
        CodeSigningAccountName = $env:AZURE_TRUSTED_SIGNING_ACCOUNT
        CertificateProfileName = $env:AZURE_TRUSTED_SIGNING_PROFILE
    } | ConvertTo-Json | Set-Content -Path $metadata -Encoding ascii
    signtool sign /v /fd SHA256 /tr http://timestamp.acs.microsoft.com /td SHA256 /dlib $dlib /dmdf $metadata $Path
    if ($LASTEXITCODE -ne 0) { throw "signtool failed for $Path" }
}

foreach ($arch in $Architectures) {
    $publishDir = Join-Path $installer "publish\$arch"
    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }

    Write-Host "== Publishing $arch"
    dotnet publish $project -c $Configuration -r "win-$arch" -p:Platform=$arch --self-contained false `
        -p:Version=$Version -p:WindowsAppSDKSelfContained=true -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $arch" }

    # The CLI: self-contained single file (the csproj's publish settings), signed before it goes
    # into the package so the MSI carries a signed elevate.exe.
    $cliDir = Join-Path $installer "publish\cli-$arch"
    if (Test-Path $cliDir) { Remove-Item -Recurse -Force $cliDir }
    Write-Host "== Publishing the CLI for $arch"
    dotnet publish $cliProject -c $Configuration -r "win-$arch" -p:Version=$Version -o $cliDir --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for the CLI ($arch)" }
    Remove-Item -Force (Join-Path $cliDir "*.pdb") -ErrorAction SilentlyContinue
    if (-not (Test-Path (Join-Path $cliDir "elevate.exe"))) { throw "elevate.exe missing after publish ($arch)" }
    if ($arch -eq "x64" -or $env:PROCESSOR_ARCHITECTURE -eq "ARM64") {
        # Runs natively on an x64 host (x64) or an Arm host (x64 under emulation, arm64 native).
        $reported = (& (Join-Path $cliDir "elevate.exe") --version).Trim()
        if ($reported -ne $Version) { throw "elevate --version printed '$reported', expected '$Version'" }
    }
    if ($Sign) { Sign-File (Join-Path $cliDir "elevate.exe") $arch }

    $msi = Join-Path $out "Elevate-$Version-$arch.msi"
    Write-Host "== Building $msi"
    wix build (Join-Path $installer "Elevate.wxs") -arch $arch -d "Version=$Version" -d "PublishDir=$publishDir" `
        -d "CliDir=$cliDir" -ext WixToolset.UI.wixext -b $installer -o $msi
    if ($LASTEXITCODE -ne 0) { throw "wix build failed for $arch" }

    if ($Sign) { Sign-File $msi $arch }

    $hash = (Get-FileHash -Algorithm SHA256 $msi).Hash.ToLowerInvariant()
    "$hash *$(Split-Path -Leaf $msi)" | Set-Content -Path "$msi.sha256" -Encoding ascii
    Write-Host "== $msi sha256 $hash"
}
