<#
.SYNOPSIS
    Publie Tout Compte Fait en un seul executable portable (win-x64, autonome) dans dist\ToutCompteFait, puis le signe.

.DESCRIPTION
    Aucune donnee n'est dans l'executable : la base SQLite vit a cote, dans Donnees\compta.db (ou ailleurs, voir
    Parametres > Application).

.EXAMPLE
    .\tools\Publish.ps1
#>
param(
    [switch]$NoSign
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "dist\ToutCompteFait"

Write-Host "== Tests du moteur"
& dotnet run --project (Join-Path $root "tests\ToutCompteFait.Tests") -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Les tests ont echoue : publication annulee." }

Write-Host "== dotnet publish"
# On garde le dossier Donnees (la base) s'il existe deja : seuls les fichiers du programme sont remplaces.
if (Test-Path $out) {
    Get-ChildItem $out -File | ForEach-Object { $_.Delete() }
}
& dotnet publish (Join-Path $root "src\ToutCompteFait\ToutCompteFait.csproj") -c Release -r win-x64 `
    --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -o $out --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish a echoue (code $LASTEXITCODE)." }
Get-ChildItem $out -Filter *.pdb | ForEach-Object { $_.Delete() }

if (-not $NoSign) {
    Write-Host "== Signature"
    & (Join-Path $PSScriptRoot "Sign-Release.ps1") -ExePath (Join-Path $out "ToutCompteFait.exe")
}

Write-Host "Termine : $out"
