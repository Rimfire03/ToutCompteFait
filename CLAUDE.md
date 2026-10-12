# Consignes pour Claude

## Git

- Committer et pousser directement sur la branche `main` (dépôt personnel). Pas de branche de travail ni de pull
  request, sauf demande explicite. Avant de modifier : `git pull --ff-only origin main`.

## Données : jamais dans le dépôt, jamais publiées

- **Ne jamais committer ni publier une base de données** (`*.db`, `Donnees\`, `Sauvegardes\`), un fichier de
  réglages, un relevé bancaire (PDF, OFX, CSV) ou un classeur Excel de l'association. `.gitignore` les exclut ;
  `tools\Release.ps1` refuse de publier si le dossier ou le zip contient des données.
- Les jeux d'essai des tests sont fictifs (aucun vrai numéro de compte, aucun nom de personne).

## Évolution du schéma et exercices clos

- Un exercice validé ne bloque jamais une évolution de la base. Toute modification de structure passe par une
  nouvelle migration à la fin de `DatabaseMigrations.All` (+ équivalent dans `ServerMigrations`), idempotente
  (`ColumnExists`, `TableExists`, `IndexExists`), en SQL portable ; ne jamais modifier une migration publiée.
- Les déclencheurs SQLite de verrouillage sont retirés avant les migrations puis recréés (`SqliteTriggers`) : une
  migration peut donc compléter les exercices validés, mais ne doit **jamais** changer un montant, une date, un libellé
  ni une ventilation (l'empreinte le révélerait).
- Les formules d'empreinte (`Compta.ComputeChecksumV1`, `ComputeChecksumV2`, `AuditChain.ComputeHash`) sont **figées** :
  pour protéger davantage de données, ajouter une version (`IntegritySeal.Current`) et garder les anciennes ; ne jamais
  modifier une formule existante.
- Une écriture ne se supprime jamais (annulation par contre-passation, `Compta.CancelEntry`) ; le journal d'audit n'est
  jamais modifié ni purgé. Toute nouvelle opération qui modifie des écritures doit consigner un message dans le journal.
- Le paramétrage qui doit rester modifiable après clôture (numéros de compte FEC, réglages d'export) va dans des tables
  ou colonnes hors des tables verrouillées et hors de l'empreinte.

## Versions (éditions) du logiciel

- Plusieurs versions cohabitent (une installation par version) : editions.json + src\ToutCompteFait.Core\Editions.cs.\n  Version actuelle = **Asso** (ToutCompteFaitAsso.exe). Le numéro de version est commun ; ne jamais renommer les\n  noms de données de la version historique (ToutCompteFait : réglages, registre, licence, dossier d'installation).\n- Jamais de nom d'exécutable, de nom affiché ou de nom de fichier de release en dur : passer par Editions.Current\n  (code) ou editions.json (scripts). Un UpgradeCode / GUID de dossier n'est jamais partagé entre deux versions.\n- Une licence = une seule version (LicenseOptions.Edition, champ edition du serveur License-server).\n- 	ools\Release.ps1 publie toutes les versions dans **une** release, version historique en premier (les anciennes\n  installations prennent le premier .msi/.zip) ; ne pas changer cet ordre ni retirer ToutCompteFait.exe (lanceur).

## Release

- Incrémenter `<Version>` dans `src\ToutCompteFait\ToutCompteFait.csproj`, committer et pousser, puis lancer
  `.\tools\Release.ps1` (options : `-NotesFile`, `-Notes`, `-NoPublish`, `-Channel dev`). Il vérifie l'arbre, lance
  les tests, publie (mono-fichier win-x64), signe avec le certificat global « TomLine prod&co », zippe
  (`ToutCompteFait-v<version>-win-x64.zip`, exécutable à la racine), construit le MSI signé
  (`tools\Build-Installer.ps1`, `installer\Product.wxs`) et crée la release GitHub avec les deux fichiers.
- L'application installée vérifie la dernière release au démarrage (Paramètres → Mises à jour) et ne remplace que
  les fichiers du programme.
- L'exécutable doit être **signé** avant d'être zippé ; ne jamais committer de `.pfx`, de mot de passe ni de clé.
