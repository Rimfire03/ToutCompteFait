<#
.SYNOPSIS
    Publie Tout Compte Fait en un seul executable portable (win-x64, autonome) dans dist\ToutCompteFait, puis le signe.

.DESCRIPTION
    Aucune donnee n'est dans l'executable : la base SQLite vit a cote, dans Donnees\compta.db (ou ailleurs, voir
    Parametres > Application). Option -WithData : cree Donnees\compta.db a partir de classeurs Excel (-Workbooks).

.EXAMPLE
    .\tools\Publish.ps1
    .\tools\Publish.ps1 -WithData -Workbooks "D:\2024-2025\livre 2024-2025.xlsx","D:\2025-2026\livre 2025-2026.xlsx"
#>
param(
    [switch]$WithData,
    [string[]]$Workbooks = @(),
    [switch]$NoSign
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "dist\ToutCompteFait"

Write-Host "== Tests du moteur"
& dotnet run --project (Join-Path $root "tests\ComptaAepby.Tests") -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Les tests ont echoue : publication annulee." }

Write-Host "== dotnet publish"
# On garde le dossier Donnees (la base) s'il existe deja : seuls les fichiers du programme sont remplaces.
if (Test-Path $out) {
    Get-ChildItem $out -File | ForEach-Object { $_.Delete() }
}
& dotnet publish (Join-Path $root "src\ComptaAepby\ComptaAepby.csproj") -c Release -r win-x64 `
    --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -o $out --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish a echoue (code $LASTEXITCODE)." }
Get-ChildItem $out -Filter *.pdb | ForEach-Object { $_.Delete() }

if (-not $NoSign) {
    Write-Host "== Signature"
    & (Join-Path $PSScriptRoot "Sign-Release.ps1") -ExePath (Join-Path $out "ToutCompteFait.exe")
}

if ($WithData) {
    if ($Workbooks.Count -eq 0) { throw "-WithData demande au moins un classeur (-Workbooks)." }
    $db = Join-Path $out "Donnees\compta.db"
    if (Test-Path $db) { throw "$db existe deja : rien n'est ecrase." }
    Write-Host "== Base initiale depuis Excel"
    $t = Join-Path $root "tests\ComptaAepby.Tests\bin\Release\net8.0\ComptaAepby.Tests.exe"
    & $t makedb $db @Workbooks
}
Write-Host "Termine : $out"
