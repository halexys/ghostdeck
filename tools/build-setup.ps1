# Builds the one-file installer GhostDeckSetup.exe (TECHNICAL.md §86) on THIS machine.
#
#   pwsh tools/build-setup.ps1 -Version 1.40.0
#
# 1. copies the MSI WMI schema (C:\Windows\SysWOW64\msiapcfg.dll) into the gitignored local/
#    folder, after checking it is signed by Micro-Star - it is MSI's file and never goes into git;
# 2. publishes GhostDeck self-contained (the .NET runtime inside, nothing to install first) as a
#    single compressed file, with the schema embedded when step 1 found it;
# 3. writes out/GhostDeckSetup-<version>.exe. Started under that name it shows the installer;
#    once installed it runs as GhostDeck.exe, the normal app.
#
# The resulting exe carries an MSI-signed component: keep it for your own machines, do not
# publish it as a release.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [switch]$NoSchema   # build without msiapcfg.dll (installer then relies on MSI Center being installed)
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$local = Join-Path $root 'local'
$schema = Join-Path $local 'msiapcfg.dll'

if ($NoSchema) {
    if (Test-Path $schema) { Remove-Item $schema }
    Write-Host 'Sin componente de MSI: el instalador no lo traerá.' -ForegroundColor Yellow
} else {
    $src = Join-Path $env:windir 'SysWOW64\msiapcfg.dll'
    if (-not (Test-Path $src)) { throw "No hay $src en esta PC. Instala MSI Center una vez o compila con -NoSchema." }
    $sig = Get-AuthenticodeSignature $src
    if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'Micro-Star International') {
        throw "$src no tiene una firma válida de Micro-Star ($($sig.Status), $($sig.SignerCertificate.Subject))."
    }
    New-Item -ItemType Directory -Force $local | Out-Null
    Copy-Item $src $schema -Force
    Write-Host "Componente de MSI copiado: $schema (firmado por Micro-Star)" -ForegroundColor Green
}

$pub = Join-Path $root 'out\setup-publish'
dotnet publish (Join-Path $root 'GhostDeck.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -p:Version=$Version -o $pub
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish falló.' }

$out = Join-Path $root "out\GhostDeckSetup-$Version.exe"
Copy-Item (Join-Path $pub 'GhostDeck.exe') $out -Force
Remove-Item $pub -Recurse -Force
$mb = [math]::Round((Get-Item $out).Length / 1MB, 1)
Write-Host "Listo: $out ($mb MB)" -ForegroundColor Green
