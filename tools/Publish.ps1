<#
.SYNOPSIS
    Publie une version (edition) de Tout Compte Fait en un seul executable portable (win-x64, autonome) dans
    dist\ToutCompteFait<Edition>, puis le signe. Versions : editions.json (defaut : asso -> ToutCompteFaitAsso.exe).

.DESCRIPTION
    Aucune donnee n'est dans l'executable : la base SQLite vit a cote, dans Donnees\compta.db (ou ailleurs, voir
    Parametres > Application).

.EXAMPLE
    .\tools\Publish.ps1
    .\tools\Publish.ps1 -Edition asso
#>
param(
    [switch]$NoSign,
    [string]$Edition = "asso"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$ed = (Get-Content (Join-Path $root "editions.json") -Raw -Encoding UTF8 | ConvertFrom-Json).editions | Where-Object { $_.id -eq $Edition }
if (-not $ed) { throw "Version (edition) '$Edition' absente de editions.json." }
$out = Join-Path $root "dist\$($ed.exe)"

Write-Host "== Tests du moteur"
& dotnet run --project (Join-Path $root "tests\ToutCompteFait.Tests") -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Les tests ont echoue : publication annulee." }

Write-Host "== dotnet publish"
# On garde le dossier Donnees (la base) s'il existe deja : seuls les fichiers du programme sont remplaces.
if (Test-Path $out) {
    Get-ChildItem $out -File | ForEach-Object { $_.Delete() }
}
& dotnet publish (Join-Path $root "src\ToutCompteFait\ToutCompteFait.csproj") -c Release -r win-x64 `
    -p:Edition=$Edition --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -o $out --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish a echoue (code $LASTEXITCODE)." }
Get-ChildItem $out -Filter *.pdb | ForEach-Object { $_.Delete() }

if (-not $NoSign) {
    Write-Host "== Signature"
    & (Join-Path $PSScriptRoot "Sign-Release.ps1") -ExePath (Join-Path $out "$($ed.exe).exe")
}

Write-Host "Termine : $out"
