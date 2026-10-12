<#
.SYNOPSIS
    Release complete de Tout Compte Fait : controles, publish, signature, zip, MSI, release GitHub.

.DESCRIPTION
    Version = <Version> de src\ToutCompteFait\ToutCompteFait.csproj (a incrementer et committer AVANT) : le numero est
    COMMUN a toutes les versions (editions) du logiciel, declarees dans editions.json (build = true).
    Etapes : controles (arbre propre, commits pousses, tag libre) -> tests -> pour chaque version : dotnet publish
    (-p:Edition=<id>), signature (tools\Sign-Release.ps1), zip <Exe>-v<version>-win-x64.zip, MSI
    <Exe>-v<version>-win-x64.msi (tools\Build-Installer.ps1) -> notes (fournies, ou generees en local a partir des commits
    depuis le dernier tag) -> gh release create (une seule release, les fichiers de toutes les versions).
    Ordre des fichiers : la version « historique » (legacyLauncher) d'abord ; les anciennes installations (<= 1.2.x), qui
    prennent le premier .zip / .msi de la release, passent ainsi sur la version Asso. Cette version embarque aussi le
    lanceur ToutCompteFait.exe (tools\LegacyLauncher) que ces anciennes installations relancent apres leur mise a jour.
    Chaque application installee lit cette release (Parametres > Mises a jour) et ne prend que le fichier de SA version.

    JAMAIS DE DONNEES : les zip ne contiennent que le programme. Le script refuse de continuer si un dossier publie ou un
    zip contient une base (.db), un dossier Donnees\ ou Sauvegardes\, ou un fichier de reglages.

.EXAMPLE
    .\tools\Release.ps1 -NoPublish                 # tout sauf la publication GitHub (test)
    .\tools\Release.ps1 -NotesFile notes.md
    .\tools\Release.ps1 -Notes "Correctif X"
    .\tools\Release.ps1 -Edition asso -NoPublish   # une seule version
#>
param(
    [string]$Notes,
    [string]$NotesFile,
    [switch]$NoPublish,
    [ValidateSet("main","dev")][string]$Channel = "main",
    [string]$Repo = "Rimfire03/ToutCompteFait",
    [string[]]$Edition
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $root "src\ToutCompteFait\ToutCompteFait.csproj"
$distDir = Join-Path $root "dist\release"

function Git { $ErrorActionPreference = "Continue"; & git.exe -C $root @args 2>&1 | ForEach-Object { "$_" } }
function Step([string]$m) { Write-Host "[release] $m" }
function DropDir([string]$p) { if (Test-Path $p) { [IO.Directory]::Delete($p, $true) } }

# --- Version
$m = [regex]::Match([IO.File]::ReadAllText($csproj), '<Version>(\d+\.\d+\.\d+)</Version>')
if (-not $m.Success) { throw "Balise <Version> introuvable dans $csproj" }
$version = $m.Groups[1].Value
$dev = ($Channel -eq "dev")
$tag = if ($dev) { "v$version-dev" } else { "v$version" }
Step "version $version"

# --- Versions (editions) a construire : la version historique d'abord (ordre des fichiers de la release)
$all = @((Get-Content (Join-Path $root "editions.json") -Raw -Encoding UTF8 | ConvertFrom-Json).editions | Where-Object { $_.build })
if ($Edition) { $all = @($all | Where-Object { $Edition -contains $_.id }) }
if ($all.Count -eq 0) { throw "Aucune version (edition) a construire." }
$all = @($all | Sort-Object @{ Expression = { if ($_.legacyLauncher) { 0 } else { 1 } } })
Step ("versions : " + (($all | ForEach-Object { $_.id }) -join ", "))

# --- Controles
if (-not $NoPublish) {
    if (@(Git status --porcelain).Count -gt 0) { throw "Arbre de travail non propre : committer d'abord." }
    Git fetch --quiet | Out-Null
    $ahead = [int](Git rev-list --count "@{u}..HEAD")
    if ($ahead -gt 0) { throw "$ahead commit(s) non pousse(s) : pousser d'abord." }
    # "release not found" sort sur stderr : on ne se fie qu'au code de sortie.
    $ErrorActionPreference = "Continue"
    & gh.exe release view $tag --repo $Repo 2>&1 | Out-Null
    $exists = ($LASTEXITCODE -eq 0)
    $ErrorActionPreference = "Stop"
    if ($exists) { throw "La release $tag existe deja sur $Repo : incrementer <Version>." }
}

# --- Tests (le moteur comptable ne doit jamais etre publie casse)
Step "tests"
& dotnet run --project (Join-Path $root "tests\ToutCompteFait.Tests") -c Release --nologo -v q | Select-Object -Last 1 | ForEach-Object { Write-Host "  $_" }
if ($LASTEXITCODE -ne 0) { throw "Les tests ont echoue : release annulee." }

$work = Join-Path $env:TEMP "toutcomptefait-release-$version"
New-Item -ItemType Directory -Force $work | Out-Null
DropDir $distDir
$groups = @()   # fichiers a publier, par version : @(@{ Id; Files = @(zip, msi) })
Add-Type -AssemblyName System.IO.Compression.FileSystem
$forbiddenName = '\.(db|db-journal|db-wal|db-shm|lock|bak|pfx|key)$'

foreach ($ed in $all) {
    $id = $ed.id
    $exeName = "$($ed.exe).exe"
    $publishDir = Join-Path $distDir $id
    Step "[$id] dotnet publish"
    & dotnet publish $csproj -c Release -r win-x64 -p:Edition=$id `
        --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:PublishReadyToRun=false -p:EnableCompressionInSingleFile=true -p:DebugType=None $(if ($dev) { "-p:ReleaseChannel=dev" }) `
        -o $publishDir --nologo -v quiet | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish ($id) a echoue (code $LASTEXITCODE)." }

    # Garde : aucune donnee dans ce qui sera publie
    $forbidden = @(Get-ChildItem $publishDir -Recurse -Force | Where-Object {
        $_.Name -match $forbiddenName -or $_.Name -like '*.settings.json' -or $_.Name -eq 'settings.json' -or
        ($_.PSIsContainer -and $_.Name -in @('Donnees', 'Sauvegardes'))
    })
    if ($forbidden.Count -gt 0) {
        throw ("DONNEES DANS LE DOSSIER PUBLIE, release annulee : " + (($forbidden | ForEach-Object { $_.FullName }) -join ", "))
    }

    # Signature (obligatoire avant le zip)
    Step "[$id] signature"
    $exe = Join-Path $publishDir $exeName
    & (Join-Path $PSScriptRoot "Sign-Release.ps1") -ExePath $exe | Select-Object -Last 3 | ForEach-Object { Write-Host "  $_" }
    if (-not (Get-AuthenticodeSignature $exe).SignerCertificate) { throw "$exeName n'est pas signe : zip annule." }

    # Lanceur de compatibilite ToutCompteFait.exe (anciennes mises a jour automatiques et raccourcis existants)
    $launcher = $null
    if ($ed.legacyLauncher) {
        Step "[$id] lanceur de compatibilite ToutCompteFait.exe"
        $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
        if (-not (Test-Path $csc)) { throw "csc.exe (.NET Framework 4) introuvable : $csc" }
        $launcher = Join-Path $work "ToutCompteFait.exe"
        if (Test-Path $launcher) { [IO.File]::Delete($launcher) }
        & $csc /nologo /target:winexe /optimize+ "/out:$launcher" "/win32icon:$(Join-Path $root 'src\ToutCompteFait\Assets\app.ico')" `
            /r:System.Windows.Forms.dll (Join-Path $PSScriptRoot "LegacyLauncher\ToutCompteFait.cs") | Out-Null
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $launcher)) { throw "Compilation du lanceur de compatibilite echouee." }
        & (Join-Path $PSScriptRoot "Sign-Release.ps1") -ExePath $launcher | Select-Object -Last 1 | ForEach-Object { Write-Host "  $_" }
        if (-not (Get-AuthenticodeSignature $launcher).SignerCertificate) { throw "Le lanceur n'est pas signe." }
        Copy-Item $launcher (Join-Path $publishDir "ToutCompteFait.exe") -Force
    }

    # Zip (l'executable est a la racine : exige par UpdateService)
    $zip = Join-Path $work "$($ed.exe)-$tag-win-x64.zip"
    if (Test-Path $zip) { [IO.File]::Delete($zip) }
    Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zip -CompressionLevel Optimal
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $names = @($archive.Entries | ForEach-Object { $_.FullName })
        if ($names -notcontains $exeName) { throw "$exeName absent de la racine du zip." }
        if ($ed.legacyLauncher -and $names -notcontains "ToutCompteFait.exe") { throw "ToutCompteFait.exe (lanceur) absent de la racine du zip." }
        $bad = @($names | Where-Object { $_ -match $forbiddenName -or $_ -match '^(Donnees|Sauvegardes)[\\/]' -or $_ -like '*settings.json' })
        if ($bad.Count -gt 0) { throw "DONNEES DANS LE ZIP, release annulee : $($bad -join ', ')" }
    } finally { $archive.Dispose() }
    Step ("[$id] zip {0} ({1:N0} Mo, {2} fichier(s), aucune donnee)" -f (Split-Path $zip -Leaf), ((Get-Item $zip).Length / 1MB), $names.Count)

    # Installateur MSI (par machine) : meme exe signe, MSI signe a son tour ; aucune donnee embarquee
    Step "[$id] installateur MSI"
    $msi = Join-Path $work "$($ed.exe)-$tag-win-x64.msi"
    $bi = @{ Version = $version; OutFile = $msi; PublishDir = $publishDir; Edition = $id }
    if ($launcher) { $bi.LauncherPath = $launcher }
    & (Join-Path $PSScriptRoot "Build-Installer.ps1") @bi | Select-Object -Last 1 | ForEach-Object { Write-Host "  $_" }
    if (-not (Test-Path $msi)) { throw "MSI ($id) non genere." }

    $groups += ,@{ Id = $id; Files = @($zip, $msi) }
}

# --- Notes
$notesPath = Join-Path $work "notes.md"
if ($NotesFile) { Copy-Item $NotesFile $notesPath -Force }
elseif ($Notes) { [IO.File]::WriteAllText($notesPath, $Notes, (New-Object Text.UTF8Encoding($false))) }
else {
    $prev = (Git describe --tags --abbrev=0 "HEAD^" 2>$null | Select-Object -First 1)
    if ($LASTEXITCODE -ne 0 -or -not $prev) { $prev = $null }
    $range = if ($prev) { "$prev..HEAD" } else { "HEAD" }
    $log = (Git log $range --no-merges --format="- %s%n%b") -join "`n"
    if ($log.Length -gt 12000) { $log = $log.Substring(0, 12000) }
    $body = $null
    try {
        . "$env:USERPROFILE\.claude\tools\Invoke-LocalLlm.ps1"
        $body = Invoke-LocalLlm @"
Redige en francais les notes de release (markdown) de la version $version de l'application Tout Compte Fait (comptabilite d'association), a partir de ces messages de commit. Regroupe en 2 a 6 puces claires pour l'utilisateur final, sans jargon technique, sans inventer de fonctionnalite, sans titre ni introduction. Reponds uniquement par les puces.

$log
"@
    } catch { Write-Warning "Notes locales indisponibles ($($_.Exception.Message)) : notes minimales." }
    if (-not $body) { $body = $log }
    [IO.File]::WriteAllText($notesPath, $body, (New-Object Text.UTF8Encoding($false)))
}

# --- Version du schema de base (derniere migration de DatabaseMigrations.All) : marqueur lu par l'application
# pour ne proposer un retour Dev -> Main que si la base est compatible.
$migText = [IO.File]::ReadAllText((Join-Path $root "src\ToutCompteFait.Core\Data\Migrations\DatabaseMigrations.cs"))
$allBlock = [regex]::Match($migText, 'All\s*=\s*\r?\n\s*\{(?<b>.*?)\r?\n\s*\};', 'Singleline')
$schema = $null
if ($allBlock.Success) {
    $nums = [regex]::Matches($allBlock.Groups['b'].Value, '(?m)^ {8}\((\d+),') | ForEach-Object { [int]$_.Groups[1].Value }
    if ($nums) { $schema = ($nums | Measure-Object -Maximum).Maximum }
}
if (-not $schema) { throw "Version du schema de base introuvable dans DatabaseMigrations.cs." }
[IO.File]::AppendAllText($notesPath, "`n`n<!-- db-schema: $schema -->`n", (New-Object Text.UTF8Encoding($false)))
Step "schema de base $schema"
if ($NoPublish) {
    Write-Host "[release] -NoPublish : arret avant GitHub. Fichiers : $((($groups | ForEach-Object { $_.Files }) | ForEach-Object { $_ }) -join ' ; ') ; notes : $notesPath"
    return
}

# --- Publication : la release est creee avec les fichiers de la premiere version (la version historique), puis ceux
# des autres versions sont ajoutes UN PAR UN (ordre garanti : les anciennes installations prennent le premier .msi/.zip).
Step "gh release create $tag"
$sha = (Git rev-parse HEAD | Select-Object -First 1).Trim()
& gh.exe release create $tag $groups[0].Files --repo $Repo --target $sha --title "Version $version$(if ($dev) { ' (dev)' })" --notes-file $notesPath $(if ($dev) { "--prerelease" } else { "--latest" })
if ($LASTEXITCODE -ne 0) { throw "gh release create a echoue (code $LASTEXITCODE)." }
foreach ($g in ($groups | Select-Object -Skip 1)) {
    foreach ($f in $g.Files) {
        & gh.exe release upload $tag $f --repo $Repo
        if ($LASTEXITCODE -ne 0) { throw "gh release upload ($($g.Id)) a echoue (code $LASTEXITCODE)." }
    }
}
Write-Host "[release] OK : https://github.com/$Repo/releases/tag/$tag"