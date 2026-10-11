<#
.SYNOPSIS
    Construit le paquet MSI de Tout Compte Fait (WiX 4, par machine) a partir du dossier publie, puis le signe.

.DESCRIPTION
    A lancer apres `dotnet publish` et signature de l'exe (Release.ps1 enchaine tout).
    Prerequis : outil `wix` 4.0.x (`dotnet tool install --global wix --version 4.0.6`) et son extension Util
    (`wix extension add -g WixToolset.Util.wixext/4.0.6`). Le MSI embarque l'exe deja signe ; le MSI lui-meme
    est signe avec le certificat global (Sign-Release.ps1 -ExePath).

    JAMAIS DE DONNEES : le MSI ne contient que l'executable (les dossiers Donnees et Sauvegardes sont crees vides).
    Le script refuse de continuer si le dossier publie contient une base ou des reglages.

.EXAMPLE
    .\tools\Build-Installer.ps1 -Version 1.0.0 -OutFile C:\temp\ToutCompteFait-v1.0.0-win-x64.msi -PublishDir dist\release
#>
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$OutFile,
    [Parameter(Mandatory)][string]$PublishDir
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$wixExe = Join-Path $env:USERPROFILE ".dotnet\tools\wix.exe"
if (-not (Test-Path $wixExe)) { $wixExe = (Get-Command wix -ErrorAction Stop).Source }
$ico = Join-Path $root "src\ComptaAepby\Assets\app.ico"
if (-not (Test-Path (Join-Path $PublishDir "ToutCompteFait.exe"))) { throw "ToutCompteFait.exe introuvable dans $PublishDir." }

$data = @(Get-ChildItem $PublishDir -Recurse -Force | Where-Object {
    $_.Name -match '\.(db|db-journal|db-wal|db-shm|lock|bak|pfx|key)$' -or $_.Name -like '*settings.json' -or
    ($_.PSIsContainer -and $_.Name -in @('Donnees', 'Sauvegardes')) })
if ($data.Count -gt 0) { throw "DONNEES DANS LE DOSSIER PUBLIE, MSI annule : $(($data | ForEach-Object { $_.FullName }) -join ', ')" }

if (Test-Path $OutFile) { Remove-Item $OutFile -Force }
& $wixExe build (Join-Path $root "installer\Product.wxs") -arch x64 -ext WixToolset.Util.wixext `
    -d "Version=$Version" -d "PublishDir=$PublishDir" -d "IconPath=$ico" -o $OutFile
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $OutFile)) { throw "wix build a echoue (code $LASTEXITCODE)." }

& (Join-Path $PSScriptRoot "Sign-Release.ps1") -ExePath $OutFile | Select-Object -Last 3 | ForEach-Object { Write-Host "  $_" }
if (-not (Get-AuthenticodeSignature $OutFile).SignerCertificate) { throw "Le MSI n'est pas signe." }
Write-Host ("[installer] {0} ({1:N1} Mo)" -f (Split-Path $OutFile -Leaf), ((Get-Item $OutFile).Length / 1MB))
