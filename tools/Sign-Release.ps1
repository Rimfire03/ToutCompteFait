<#
.SYNOPSIS
    Signe (Authenticode + horodatage) l'executable publie, avant de creer le zip de release.

.DESCRIPTION
    A lancer apres `dotnet publish` et AVANT Compress-Archive : l'exe signe est celui qui part dans le zip
    (le zip n'est pas signe lui-meme, la signature est dans l'exe).

    Le certificat est cherche dans le magasin utilisateur (Cert:\CurrentUser\My) : par empreinte
    (-Thumbprint ou variable d'environnement COMPTA_SIGN_THUMBPRINT), sinon le certificat de signature
    de code le plus recent dont le sujet contient "TomLine prod&co". Aucun mot de passe ni fichier .pfx
    dans le depot : la cle privee reste dans le magasin de certificats du poste de publication (sauvegarde
    .pfx a part, hors depot - voir README).

.EXAMPLE
    .\tools\Sign-Release.ps1
    .\tools\Sign-Release.ps1 -ExePath "C:\chemin\vers\ToutCompteFait.exe"
#>
param(
    [string]$ExePath,
    [string]$Thumbprint = $env:COMPTA_SIGN_THUMBPRINT,
    [string[]]$TimestampServers = @("http://timestamp.digicert.com", "http://timestamp.sectigo.com")
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if (-not $ExePath) {
    $publishDir = Join-Path $root "dist\ToutCompteFait"
    $exes = @(Get-ChildItem -Path $publishDir -Filter *.exe -File -ErrorAction SilentlyContinue)
    if ($exes.Count -ne 1) {
        throw "Attendu exactement un .exe dans $publishDir (trouve : $($exes.Count)). Precisez -ExePath."
    }
    $ExePath = $exes[0].FullName
}
if (-not (Test-Path $ExePath)) { throw "Executable introuvable : $ExePath" }

$candidates = Get-ChildItem Cert:\CurrentUser\My | Where-Object {
    $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) -and
    # OID "Code Signing" : le nom affiche depend de la langue de Windows ("Signature du code" en francais).
    ($_.EnhancedKeyUsageList.ObjectId -contains "1.3.6.1.5.5.7.3.3")
}
if ($Thumbprint) {
    $cert = $candidates | Where-Object { $_.Thumbprint -eq $Thumbprint } | Select-Object -First 1
} else {
    $cert = $candidates | Where-Object { $_.Subject -like "*TomLine prod&co*" } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
}
if (-not $cert) {
    throw "Aucun certificat de signature de code valide trouve dans Cert:\CurrentUser\My. Importez la sauvegarde .pfx (Import-PfxCertificate) puis relancez."
}

Write-Host "Signature de $ExePath"
Write-Host "Certificat : $($cert.Subject) (empreinte $($cert.Thumbprint), expire le $($cert.NotAfter.ToString('dd/MM/yyyy')))"

$signed = $false
foreach ($server in $TimestampServers) {
    try {
        $r = Set-AuthenticodeSignature -FilePath $ExePath -Certificate $cert -HashAlgorithm SHA256 -TimestampServer $server
        if ($r.SignerCertificate) { $signed = $true; Write-Host "Horodatage : $server"; break }
    } catch {
        Write-Warning "Horodatage via $server impossible : $($_.Exception.Message)"
    }
}
if (-not $signed) {
    Write-Warning "Aucun serveur d'horodatage joignable : signature SANS horodatage (valide seulement jusqu'a l'expiration du certificat)."
    $r = Set-AuthenticodeSignature -FilePath $ExePath -Certificate $cert -HashAlgorithm SHA256
}

$check = Get-AuthenticodeSignature -FilePath $ExePath
if (-not $check.SignerCertificate) { throw "Echec : l'executable n'est pas signe ($($check.StatusMessage))." }
Write-Host "Signature ecrite. Statut : $($check.Status) - $($check.StatusMessage)"
if ($check.TimeStamperCertificate) { Write-Host "Horodatage present (TimeStamper : $($check.TimeStamperCertificate.Subject))" }
Write-Host "Un statut 'UnknownError' / 'NotTrusted' est normal avec un certificat auto-signe tant que TomLine-signature.cer (certificat global TomLine prod&co) n'est pas installe comme approuve sur le poste."

