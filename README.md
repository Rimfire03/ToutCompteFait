# Tout Compte Fait

Livre de trésorerie portable de l'association : remplace les classeurs Excel « livre ».
Application Windows (WPF, .NET 8), un seul `.exe` autonome ; les données sont dans un fichier SQLite à côté.

## Utilisation rapide

1. Lancer `dist\ToutCompteFaitAsso\ToutCompteFaitAsso.exe` (la base est dans `Donnees\compta.db`).
2. **Livre** : « Nouvelle écriture » → type (dépense, recette, virement entre comptes, remboursement à un membre,
   sans mouvement d'argent), date, libellé, montant, catégorie. Le montant est repris dans la ventilation ; un bandeau
   vert/rouge indique si tout est ventilé. La rentabilité de l'opération concernée s'affiche pendant la saisie.
3. **Bilan et opérations** : compte de fonctionnement, rentabilité par opération, actif/passif, contrôles, validation.
4. **Reports entre exercices** : paiements/encaissements qui concernent un autre exercice (transmis automatiquement).
5. **Paramètres** : produits et charges, opérations (liens produits ↔ charges), comptes, exercice, journal.

## Règles comptables (ce que le logiciel garantit)

* Montants en **centimes entiers** (aucun arrondi flottant).
* Une écriture est **valide** seulement si `produits ventilés − charges ventilées = mouvement de trésorerie`
  (avoir : une entrée d'argent sur une charge compte en négatif ; remboursement d'une recette : l'inverse).
* Un virement entre comptes n'a aucun effet sur le résultat ; il déplace le solde d'un compte à l'autre.
* Résultat d'une catégorie = livre + régularisations :
  `+ payé l'exercice précédent pour cet exercice`, `+ à régler l'exercice suivant`,
  `− payé cet exercice pour l'exercice suivant`, `− payé cet exercice pour un exercice passé`.
* Passif = solde d'ouverture + report (−régularisations nettes) + résultat + écart non ventilé ; **Actif = Passif** est
  contrôlé (l'écart non ventilé rend visible toute écriture à corriger au lieu de la masquer).
* Soldes d'ouverture et reports (`pour l'exercice suivant` → `réglé l'exercice précédent`) sont repris **automatiquement** de
  l'exercice précédent.
* **Exercice validé** : plus aucune écriture, catégorie, opération ou report ne peut être ajouté, modifié ou retiré —
  vérifié par le logiciel **et** par des déclencheurs SQLite (même en SQL direct). Une empreinte SHA-256 enregistrée à
  la validation détecte toute altération faite hors du logiciel. Les exercices se valident dans l'ordre.
  Une réouverture exceptionnelle est possible (motif obligatoire, tracée dans le journal, refusée si l'exercice
  suivant est validé).

## Conformité comptable et fiscale (piste d'audit)

* **Numérotation continue** : chaque écriture reçoit un numéro d'ordre dans son exercice (1, 2, 3…), sans trou ni
  réutilisation. À la clôture, les écritures déplacées vers l'exercice suivant sont renumérotées et l'exercice clôturé
  est renuméroté sans trou (tracé au journal).
* **Aucune suppression** : une écriture s'**annule** par une contre-passation (écriture inverse, même date, même
  montant) ; l'original reste dans le livre (case « Annulations »). Effet net nul sur les comptes. Les justificatifs
  restent attachés.
* **Journal d'audit chaîné** : chaque ligne porte le SHA-256 de son contenu et de la ligne précédente ; modification,
  insertion ou retrait de lignes sont détectés (Paramètres, Bilan). Les lignes ne se modifient ni ne se suppriment
  (garde du logiciel + déclencheurs SQLite). Chaque création, modification (avant / après), annulation, pointage,
  affectation, import et régularisation y est consigné avec le numéro de l'écriture.
* **Empreinte des exercices validés** (v2) : couvre en plus la note, la référence d'import, la date de saisie, les
  annulations, les régularisations et le **contenu de chaque justificatif**. Les empreintes d'ancienne version restent
  vérifiables ; elles sont mises à niveau automatiquement (après vérification, tracé) à l'ouverture suivante.
* **Exports** (bouton « Exporter » du Livre) : livre-journal PDF ou CSV, et **FEC** (Fichier des Écritures Comptables,
  18 colonnes, débit = crédit contrôlé) pour un contrôle fiscal ou un expert-comptable. Les numéros de compte des
  produits, charges et comptes bancaires se saisissent dans « Numéros de compte comptable » (à défaut : numéros
  automatiques de classe 6, 7 et 5) ; ils restent modifiables même sur un exercice validé.
* **Vidage de la base** : refusé tant qu'un exercice validé existe, sauf archive exportée (livre CSV, PDF et FEC) et
  phrase de confirmation spécifique, puis consigné au journal.
* Hors périmètre : comptabilité d'engagement complète (plan comptable associatif ANC 2018-06), reçus fiscaux de dons.
  Le FEC est généré à partir d'une comptabilité de trésorerie : à faire valider par l'expert-comptable de l'association.

## Évolution du logiciel et de la base après clôture

Un exercice validé n'empêche pas la base d'évoluer :

* Les **migrations** (`DatabaseMigrations`) lèvent les verrous SQLite le temps de leur exécution (transaction + sauvegarde
  préalable), puis les recréent ; elles peuvent donc ajouter des colonnes, des tables, des index et compléter des données
  de **tous** les exercices, y compris validés. Une migration ne doit jamais changer un montant, une date, un libellé ni
  une ventilation d'un exercice validé (l'empreinte le signalerait).
* La **formule d'empreinte est versionnée** (`IntegritySeal`) : chaque exercice est vérifié avec la formule inscrite
  dans son empreinte, les anciennes formules sont figées et conservées. Protéger de nouvelles données = nouvelle formule,
  jamais modification d'une ancienne.
* Les données de **paramétrage** (numéros de compte, réglages d'export) vivent hors des tables verrouillées et de
  l'empreinte : elles s'enrichissent à tout moment.

## Comptes, mots de passe et rôles

Le logiciel s'ouvre sur un écran de connexion. Au tout premier lancement (aucun compte), il propose de créer
l'**administrateur** ; ensuite **seul un administrateur crée les comptes** (Paramètres → Utilisateurs). Un compte créé
reçoit un mot de passe provisoire qu'il doit changer à sa première connexion. Les mots de passe sont hachés
(PBKDF2-SHA256, sel par compte), jamais enregistrés en clair ; 5 échecs bloquent l'identifiant 2 minutes.

| Droit | Administrateur | Trésorier | Lecteur |
|---|---|---|---|
| Consulter livre, bilan, reports, exporter | ✔ | ✔ | ✔ |
| Saisir, modifier, annuler des écritures et des reports ; importer un relevé bancaire | ✔ | ✔ | — |
| Créer un exercice, gérer produits/charges/opérations/comptes bancaires | ✔ | ✔ | — |
| Valider un exercice | ✔ | ✔ | — |
| Rouvrir un exercice validé | ✔ | — | — |
| Créer et gérer les utilisateurs | ✔ | — | — |
| Base de données (serveur, fichier, sauvegardes, restauration) | ✔ | — | — |

Les droits sont vérifiés par le moteur (`Compta`), pas seulement masqués dans l'interface (`RoleInfo.Has`). Le dernier
administrateur actif ne peut être ni rétrogradé ni désactivé. Un lecteur n'occupe jamais le verrou d'écriture.

> Limite à connaître : l'authentification protège l'**application**. Avec un fichier SQLite posé sur un partage, toute
> personne qui a accès au fichier peut l'ouvrir avec un autre outil ; pour une vraie séparation des accès, utilisez un
> **serveur de base de données** (PostgreSQL conseillé) dont les identifiants ne sont connus que de l'administrateur.

## Base de données (même système que FiltresApp)

* Quatre moteurs : **SQLite** (fichier local ou réseau), **PostgreSQL**, **MariaDB / MySQL**, **SQL Server**
  (Paramètres → Base de données → « Serveur de base de données… » : test de connexion, copie des données vers le
  serveur, redémarrage). Mot de passe du serveur chiffré par DPAPI (poste Windows).
* Schéma versionné (migrations), sauvegarde automatique du fichier avant toute mise à jour, refus d'ouvrir une base
  plus récente que le logiciel.
* Un seul rédacteur : fichier `.lock` (SQLite) ou ligne de verrou avec battement de cœur (serveurs) ; autres postes en
  lecture seule, qui se mettent à jour tout seuls.
* Sauvegardes manuelles ou planifiées (quotidienne, hebdomadaire, mensuelle), restauration, journal des opérations.
* Limite : les exercices validés sont verrouillés par déclencheurs SQLite **et** par le logiciel (tous moteurs). Sur
  PostgreSQL / MariaDB / SQL Server le verrou est celui du logiciel (le schéma est vérifié par les tests, mais aucun
  serveur n'était disponible pour les essayer en conditions réelles).

## Import d'un relevé bancaire

Livre → « Importer un relevé bancaire… » (CSV ou OFX téléchargé depuis la banque ; colonnes détectées, modifiables).

* Une ligne du relevé est rapprochée d'une écriture du même compte, de **même date et même montant** : son
  **intitulé est remplacé** par celui de la banque (la ventilation n'est jamais touchée).
* Sinon une écriture est **créée sans produit ni charge** : elle apparaît en **jaune** dans le Livre.
* Dans le Livre, la colonne « Ventilation » d'une ligne jaune propose directement la catégorie (« Affecter à… ») ;
  pour répartir sur plusieurs catégories, « Modifier ».
* **Date légèrement différente** (jusqu'à 7 jours, même compte et même montant) : le rapprochement n'est jamais
  automatique. La ligne apparaît en **orange** avec les écritures candidates ; vous choisissez « Rapprocher de… »,
  « Créer une nouvelle écriture » ou « Ignorer cette ligne ». Tant qu'une ligne orange n'est pas tranchée, l'import
  est impossible. Option : prendre la date de la banque pour les rapprochements validés (sinon la date saisie est
  conservée). Une correspondance à la date exacte reste prioritaire.
* Ré-importer le même relevé ne crée aucun doublon. Les lignes d'un exercice validé ou hors de tout exercice sont
  ignorées. Une écriture à ventiler empêche la validation de l'exercice.

## Importer un relevé imprimé (PDF ou image, OCR)

Livre → « Importer un relevé bancaire… » → choisir **un ou plusieurs PDF** (par exemple les 12 relevés mensuels de
l'année) ou une **image** (PNG, JPEG, TIFF, BMP).

* **PDF numérique** (texte sélectionnable, téléchargé de l'espace client) : le texte et sa position sont lus
  directement, **sans OCR**, donc sans risque de confusion de chiffres.
* **PDF scanné ou photo** : pages rendues puis reconnues par l'**OCR intégré à Windows** (hors ligne, rien n'est envoyé
  sur Internet ; le pack de langue français de Windows doit être présent : Paramètres → Heure et langue → Langue →
  Français → Reconnaissance optique de caractères).
* **Plusieurs comptes dans un même PDF** (compte courant + Livret A) : le logiciel les sépare par le numéro de compte
  imprimé ; vous choisissez le compte à importer dans la liste « Compte ».
* La mise en page est déduite des en-têtes de colonnes (Date, Date de valeur, Libellé / Détail des opérations, Débit,
  Crédit ou Montant, Solde), y compris sur plusieurs lignes ; les signes « + » / « - » imprimés à part, les milliers
  coupés et les libellés sur plusieurs lignes sont gérés. Des **profils** (mots-clés) sont modifiables : un fichier
  `.json` par profil dans le dossier `profils-releves` à côté de l'exécutable.
* **Le relevé doit « tomber juste » avant import** : ancien solde + opérations = nouveau solde, totaux des débits / crédits
  du résumé retrouvés, solde courant cohérent ligne à ligne, et — pour plusieurs mois — le solde de fin d'un relevé est
  le solde de début du suivant. Au moindre écart l'import est **bloqué** et la ligne ou la jonction fautive est
  désignée ; l'image de la page s'affiche à côté avec la ligne encadrée pour la corriger.
* Rien n'est deviné : année absente → date laissée vide ; chiffre douteux (lettre à la place d'un chiffre) → corrigé
  mais signalé ; montant sans décimales → ligne incomplète. Si aucun solde n'est lisible, vous pouvez les saisir ; à
  défaut l'import exige que vous certifiiez avoir comparé chaque ligne avec le relevé.
* Les lignes validées suivent ensuite le circuit habituel (rapprochement, lignes jaunes, validation des dates douteuses).
* Validé sur 12 relevés mensuels réels de la Caisse d'Épargne (octobre 2025 → septembre 2026) : 19 tableaux, tous
  « justes » (soldes et totaux), enchaînement des mois cohérent ; rapprochement avec l'export OFX de la banque : 298
  opérations sur 299 identiques à la date et au centime près (la 299e a un jour d'écart, cas géré par la validation des
  dates douteuses).
* Diagnostics : `ToutCompteFait.exe --ocrtest <dossier>` (relevé factice PDF + image, lecture complète) ;
  `--readstatement <fichier> <sortie.txt>` (texte positionné + lecture d'un vrai relevé) ;
  `--ocrui <dossier de PDF> <capture.png>` (écran de vérification sur tous les PDF d'un dossier).
## Versions (éditions) du logiciel

Le logiciel existe en plusieurs **versions** qui s'installent **côte à côte** sur un même poste (une installation par
version). Le numéro de version est **commun** à toutes ; chaque version y ajoute sa mention (« 1.3.0 · version Asso »).

| | Version Asso (historique) |
|---|---|
| Nom affiché | Tout Compte Fait Asso |
| Exécutable | `ToutCompteFaitAsso.exe` |
| Installateur / archive | `ToutCompteFaitAsso-v<version>-win-x64.msi` / `.zip` |
| Dossier d'installation | `C:\Program Files\ToutCompteFait` (inchangé : les données `Donnees\` restent en place) |
| Réglages, clé de registre, licence | `ToutCompteFait` (inchangés : les installations et licences existantes continuent) |

* **Une seule base de code.** La version est choisie à la compilation (`-p:Edition=asso`, métadonnée d'assembly lue par
  `Editions.Current`). Les versions sont déclarées dans `editions.json` (nom, exécutable, `UpgradeCode`, GUID des dossiers).
  Ajouter une version = ajouter une entrée (nouveaux GUID) puis `Release.ps1` la construit et la publie.
* **Mises à jour.** Une release GitHub contient les fichiers de toutes les versions ; chaque installation ne prend que celui
  de sa version et ne se met jamais à jour vers une autre. Les anciennes installations (≤ 1.2.x, avant les versions) passent
  automatiquement sur la version Asso : leur mise à jour prend le premier `.msi`/`.zip` de la release (Asso, publié en premier)
  et le lanceur `ToutCompteFait.exe` livré avec Asso (`tools\LegacyLauncher`) les relance après la mise à jour.
* **Licences.** Une licence ne donne droit qu'à **une** version (champ « version » côté serveur de licences, `asso` par
  défaut pour toutes les licences déjà émises). Le produit et le nom de la licence ne changent pas.
## Mises à jour automatiques

Même système que FiltresApp : le logiciel interroge la dernière **release GitHub** du dépôt
[Rimfire03/ToutCompteFait](https://github.com/Rimfire03/ToutCompteFait/releases) au démarrage (désactivable) et propose
à un administrateur de l'installer. Paramètres → **Mises à jour** : version, vérification automatique, canal (Main =
stable, Dev = préversions), « Vérifier maintenant », « Télécharger et installer ».

* **Deux formes de livraison**, publiées ensemble à chaque release : le `.zip` portable (exécutable à lancer depuis
  n'importe quel dossier) et l'**installateur `.msi`** (par poste : `C:\Program Files\ToutCompteFait`, raccourci du menu
  Démarrer, dossiers `Donnees\` et `Sauvegardes\` inscriptibles par tous les utilisateurs du poste). Le logiciel détecte
  son mode et télécharge le bon fichier : un poste installé se met à jour par le `.msi`, un poste portable par le `.zip`.
* L'installation sauvegarde la base juste avant, télécharge le fichier signé, ferme le logiciel, remplace **uniquement
  les fichiers du programme** et relance. La base, les réglages et les sauvegardes ne sont jamais touchés ; le `.msi` ne
  contient aucune base et crée les dossiers de données vides.
* Si la base a été mise à jour par une version plus récente que le logiciel, la mise à jour est proposée avant le refus
  d'ouvrir (rien n'est modifié).
* Publier une version : incrémenter `<Version>` dans `src\ToutCompteFait\ToutCompteFait.csproj`, committer, pousser, puis
  `.\tools\Release.ps1`. Le script lance les tests, publie, signe, zippe, construit et signe le `.msi`
  (WiX 4 : `dotnet tool install --global wix --version 4.0.6` puis `wix extension add -g WixToolset.Util.wixext/4.0.6`),
  **refuse de continuer si le dossier, le zip ou le MSI contient une base de données**, et crée la release avec les
  deux fichiers.
* **Aucune donnée n'est jamais publiée** : `Donnees\`, `*.db`, sauvegardes, réglages, relevés (PDF/OFX/CSV) et classeurs
  Excel sont exclus du dépôt (`.gitignore`) et des releases.

## Plusieurs postes

Un seul poste écrit à la fois : le premier qui ouvre la base garde un verrou (`compta.db.lock`, même principe que
FiltresApp) ; les autres sont en **lecture seule** et voient les modifications apparaître automatiquement. Le pied de
page (identique à FiltresApp) indique l'état : puce verte (accessible), orange (lecture seule), rouge (connexion perdue).

> Le verrou repose sur les verrous de fichiers Windows : il fonctionne sur un **dossier partagé réseau (SMB/NAS)**,
> pas à travers la synchronisation Google Drive / OneDrive, où chaque poste a sa propre copie du fichier.

## Développement

```
ToutCompteFait.sln
src/ToutCompteFait.Core   moteur (règles comptables, bases SQLite / serveur, import des relevés) — aucune dépendance d'interface
src/ToutCompteFait        application WPF
tests/ToutCompteFait.Tests   vérifications du moteur (clôture, ventilation, import, sauvegardes…)
tools/Publish.ps1      tests → publication mono-fichier → signature
tools/Sign-Release.ps1 signature avec le certificat global TomLine prod&co
```

* Tests : `dotnet run --project tests\ToutCompteFait.Tests -c Release`
* Publication : `.\tools\Publish.ps1`
* Diagnostic : `ToutCompteFait.exe --db <fichier.db> --year <libellé> --shots <dossier>` enregistre une capture de
  chaque écran (`--selftest` saisit en plus deux écritures de test : réservé à une base de démonstration).



