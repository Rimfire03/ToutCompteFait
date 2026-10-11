# Consignes pour Claude

## Git

- Committer et pousser directement sur la branche `main` (dépôt personnel). Pas de branche de travail ni de pull
  request, sauf demande explicite. Avant de modifier : `git pull --ff-only origin main`.

## Données : jamais dans le dépôt, jamais publiées

- **Ne jamais committer ni publier une base de données** (`*.db`, `Donnees\`, `Sauvegardes\`), un fichier de
  réglages, un relevé bancaire (PDF, OFX, CSV) ou un classeur Excel de l'association. `.gitignore` les exclut ;
  `tools\Release.ps1` refuse de publier si le dossier ou le zip contient des données.
- Les jeux d'essai des tests sont fictifs (aucun vrai numéro de compte, aucun nom de personne).

## Release

- Incrémenter `<Version>` dans `src\ToutCompteFait\ToutCompteFait.csproj`, committer et pousser, puis lancer
  `.\tools\Release.ps1` (options : `-NotesFile`, `-Notes`, `-NoPublish`, `-Channel dev`). Il vérifie l'arbre, lance
  les tests, publie (mono-fichier win-x64), signe avec le certificat global « TomLine prod&co », zippe
  (`ToutCompteFait-v<version>-win-x64.zip`, exécutable à la racine), construit le MSI signé
  (`tools\Build-Installer.ps1`, `installer\Product.wxs`) et crée la release GitHub avec les deux fichiers.
- L'application installée vérifie la dernière release au démarrage (Paramètres → Mises à jour) et ne remplace que
  les fichiers du programme.
- L'exécutable doit être **signé** avant d'être zippé ; ne jamais committer de `.pfx`, de mot de passe ni de clé.
