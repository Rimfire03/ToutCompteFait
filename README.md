# Tout Compte Fait

Livre de trésorerie portable de l'association : remplace les classeurs Excel « livre ».
Application Windows (WPF, .NET 8), un seul `.exe` autonome ; les données sont dans un fichier SQLite à côté.

## Utilisation rapide

1. Lancer `dist\ToutCompteFait\ToutCompteFait.exe` (la base est dans `Donnees\compta.db`).
2. **Livre** : « Nouvelle écriture » → type (dépense, recette, virement entre comptes, remboursement à un membre,
   sans mouvement d'argent), date, libellé, montant, catégorie. Le montant est repris dans la ventilation ; un bandeau
   vert/rouge indique si tout est ventilé. La rentabilité de l'opération concernée s'affiche pendant la saisie.
3. **Bilan et opérations** : compte de fonctionnement, rentabilité par opération, actif/passif, contrôles, validation.
4. **Reports entre exercices** : paiements/encaissements qui concernent un autre exercice (transmis automatiquement).
5. **Paramètres** : produits et charges, opérations (liens produits ↔ charges), comptes, exercice, import Excel, journal.

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

## Comptes, mots de passe et rôles

Le logiciel s'ouvre sur un écran de connexion. Au tout premier lancement (aucun compte), il propose de créer
l'**administrateur** ; ensuite **seul un administrateur crée les comptes** (Paramètres → Utilisateurs). Un compte créé
reçoit un mot de passe provisoire qu'il doit changer à sa première connexion. Les mots de passe sont hachés
(PBKDF2-SHA256, sel par compte), jamais enregistrés en clair ; 5 échecs bloquent l'identifiant 2 minutes.

| Droit | Administrateur | Trésorier | Lecteur |
|---|---|---|---|
| Consulter livre, bilan, reports, exporter | ✔ | ✔ | ✔ |
| Saisir, modifier, supprimer des écritures et des reports ; importer un relevé bancaire | ✔ | ✔ | — |
| Créer un exercice, gérer produits/charges/opérations/comptes bancaires | ✔ | ✔ | — |
| Valider un exercice | ✔ | ✔ | — |
| Rouvrir un exercice validé | ✔ | — | — |
| Créer et gérer les utilisateurs | ✔ | — | — |
| Base de données (serveur, fichier, sauvegardes, restauration), import des classeurs Excel | ✔ | — | — |

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
* Publier une version : incrémenter `<Version>` dans `src\ComptaAepby\ComptaAepby.csproj`, committer, pousser, puis
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

## Import des classeurs Excel

Paramètres → Application → « Importer des classeurs Excel… » (ou `tools\Publish.ps1 -WithData`).
Reprise fidèle, **sans correction silencieuse** ; les incohérences sont listées dans un rapport et restent visibles
(lignes rouges du Livre). Ce que l'import a trouvé dans les trois classeurs fournis :

| Exercice | Constat dans l'Excel | Traitement |
|---|---|---|
| 2025-2026 | Actif 25 510,27 ≠ passif 25 328,35 (écart 181,92 €) | 4 écritures mal ventilées (lignes 105, 106, 146, 269) restent à corriger ; `+ 2 écritures 161,59 € sans catégorie` (aller-retour) |
| 2025-2026 | dates en 2026 pour des lignes de décembre 2025 (18/12 et 31/12) | signalées « hors exercice », à corriger |
| 2025-2026 | ligne 271 (refacturation sans mouvement d'argent) | importée comme « Sans mouvement d'argent » |
| 2024-2025 | 168,99 € saisis en **texte** (ligne 259), ignorés par les totaux Excel | repris comme montant (charges +168,99) |
| 2024-2025 | écart actif/passif de 854,65 € ; reports sans catégorie | écritures non ventilées visibles, reports à affecter |
| 2026-2027 | vide ; report 254,99 € (« lot loto » payé en 2025-2026) | reporté automatiquement, catégorie à choisir |

## Développement

```
ComptaAepby.sln
src/ComptaAepby.Core   moteur (règles comptables, SQLite, import Excel) — aucune dépendance d'interface
src/ComptaAepby        application WPF
tests/ComptaAepby.Tests   284 vérifications, dont le rapprochement avec les formules des classeurs Excel
tools/Publish.ps1      tests → publication mono-fichier → signature → (option) base initiale
tools/Sign-Release.ps1 signature avec le certificat global TomLine prod&co
```

* Tests : `dotnet run --project tests\ComptaAepby.Tests -c Release`
* Publication : `.\tools\Publish.ps1` (ajouter `-WithData -Workbooks a.xlsx,b.xlsx` pour créer la base)
* Diagnostic : `ToutCompteFait.exe --db <fichier.db> --year <libellé> --shots <dossier>` enregistre une capture de
  chaque écran (`--selftest` saisit en plus deux écritures de test : réservé à une base de démonstration).



