# Plan d'action — retours du bêta-testeur

Source : « Rapport_retour_Tout_Compte_Fait_restructure.docx » (8 rubriques, 24 remarques).
Effort : **S** (≤ ½ jour), **M** (1 à 3 jours), **L** (plus d'une semaine). Statut au 18/10/2026, version 1.12.9.

## Synthèse

- **11 remarques sont des corrections rapides** (libellés, retours visuels, états vides, couleurs) : lot 1.
- **8 sont de vraies fonctionnalités** (centre de notifications, calendrier URSSAF, création contextuelle, navigation
  personnalisable…) : lot 2.
- **4 relèvent de la stratégie produit** (parcours d'accueil complet, connexion bancaire, plan comptable par classes,
  mode démo) : lot 3, à arbitrer avant de lancer.
- **Exclu du périmètre (décision du 18/10/2026)** : tout ce qui touche à la facturation et aux devis, dont les
  « tarifs des produits » (§4.3 du rapport). Le logiciel reste un outil de trésorerie.
- **1 point à reproduire** avant tout développement : ventilation des recettes (§3.3).
- Plusieurs remarques sont **déjà partiellement traitées** par les versions 1.12.x (voir colonne « Déjà fait »).

## Avancement du lot 1 (branche `beta`, 18/10/2026) — lot terminé

| # | Statut | Détail |
|---|---|---|
| 4.1 | Fait | « Gestion de l'entreprise » (Autoentreprise) partout : menu, titre, aide, messages. L'Asso garde « Paramétrage du dossier » |
| 4.2 | Fait | Message « ✔ Enregistré » (2 s, sans prendre le focus) après chaque modification réussie |
| 3.2 | Fait | « … » retirés des boutons de la barre du Livre et des immobilisations |
| 6.1 | Fait | Graphiques « fantômes » (grille, colonnes pâles, anneau, message) quand il n'y a pas de donnée |
| 7.2 | Fait | Charges en ambre (Bilan, immobilisations, variations) ; statistiques en palettes par sens (produits verts, charges ambre, opérations bleus) ; le rouge reste réservé aux alertes, erreurs et résultats négatifs |
| 5a | Fait | « J-N » ou « en retard de N j » dans l'alerte, bouton « Ouvrir le site de l'URSSAF » (alerte et Reporting) |
| 1.1 | Fait | Écran d'accueil réécrit en langage simple, premier choix recommandé et message de bienvenue intégré |
| 3.4a | Reporté (décision du 18/10/2026) ; acquis conservés :| Champ « Rechercher une catégorie » dans la saisie d'écriture (listes de plus de 12 lignes) ; dans la liste d'affectation rapide du Livre et dans la saisie, taper les premières lettres du nom place la sélection. Listes de comptes non modifiées (peu de lignes) |
| 3.3 | Reporté (décision du 18/10/2026) ; acquis conservés :| Non reproduit : en mode Recette, « Ajouter une ligne de ventilation » fonctionne (3 lignes ajoutées en capture). Message d'aide réécrit (retour à la ligne, indique d'abord le montant puis le bouton). À confirmer avec le testeur |
| 3.1a | Reporté (décision du 18/10/2026) ; acquis conservé : version générique ;| Page d'aide « Récupérer le fichier de votre banque » ; pas de pas-à-pas par banque (menus non vérifiables) |

> **Points 3.1a, 3.3 et 3.4a : mis de côté pour le moment** (décision du 18/10/2026). Aucun travail supplémentaire n'est prévu dessus avant nouvel ordre.

## Lot 1 — Corrections rapides (version 1.13)

| # | Remarque | Action | Effort | Déjà fait / notes |
|---|---|---|---|---|
| 4.1 | Renommer « Paramétrage du dossier » | Autoentreprise : « Gestion de l'entreprise » ; Asso : « Paramétrage de l'association » (libellé par version, via `Wording`) | S | — |
| 4.2 | Confirmation d'enregistrement | Message discret « ✔ Enregistré » (bandeau 2 s) après chaque ajout / modification de paramétrage ; garder les boutons « Enregistrer » existants | S | Les boutons existent déjà (Entreprise, exercice, immobilisations) ; le reste est silencieux |
| 3.2 | Boutons / onglets « … » à la place du libellé | Vérifier à plusieurs largeurs de fenêtre (captures). Les « … » vus sont probablement des libellés volontaires (« Immobiliser… », « Importer un FEC… ») : les retirer de la barre du Livre pour lever l'ambiguïté | S | Troncatures réelles corrigées en 1.12.4 (bilan, import, immobilisations, listes) |
| 6.1 | Statistiques : écran blanc | Afficher les graphiques « fantômes » (axes, grille, camembert grisé) avec un message « Aucune donnée pour l'instant » au lieu d'un écran vide | S | Aujourd'hui : message seul (1.12.4) |
| 7.2 | Cohérence des couleurs, moins de rouge | Définir des jetons sémantiques (produit = vert, charge = ambre, alerte = rouge, information = bleu) et les appliquer partout ; le rouge n'est gardé que pour les actions destructrices et les erreurs | M | Audit des écrans à faire d'abord (rouge des charges dans Bilan, tuiles, graphiques) |
| 5a | URSSAF : jours restants + lien | Ajouter « J-15 » à côté de « À déclarer avant le… », un bouton « Ouvrir autoentrepreneur.urssaf.fr » dans le Reporting et la fenêtre d'alerte | S | Échéances et alerte à l'ouverture déjà en place (1.12.x) |
| 3.4a | Recherche dans les listes déroulantes | Composant commun « liste filtrable » (saisie = filtre) pour catégories, comptes, produits | M | À appliquer à la saisie d'écriture, à l'import, aux paramétrages |
| 3.3 | Pas de ligne de ventilation en recette | **À reproduire d'abord** (le bouton « Ajouter une ligne » existe en mode Recette). Hypothèses : un seul produit disponible depuis le filtrage par sens (1.12.0), ou bouton inactif tant que le montant est vide. Demander version et édition au testeur | S/M | Voir « Questions » |
| 1.1 | 4 choix de base de données incompréhensibles | Réécrire les libellés en langage simple (« Sur cet ordinateur (recommandé) », « Sur un serveur partagé »…), pré-sélectionner le choix local, replier les autres sous « Options avancées » | S | Texte actuel dans `FirstRunWindow` |
| 3.1a | Tutoriels d'export CSV par banque | Page d'aide statique : étapes illustrées pour les principales banques (Crédit Agricole, BNP, Société Générale, LCL, Caisse d'Épargne, Banque Populaire, CIC, La Banque Postale, Qonto, Shine…) + lien depuis la fenêtre d'import | S/M | Contenu à rédiger ; à vérifier banque par banque |
| 8a | Note humoristique | À ignorer | — | — |

## Lot 2 — Fonctionnalités (versions 1.14 à 1.15)

| # | Remarque | Proposition | Effort |
|---|---|---|---|
| 2 | Centre de notifications | Icône cloche dans la barre latérale avec compteur ; liste des alertes en cours (URSSAF à déclarer, ventilations à faire, exercice à clôturer, sauvegarde ancienne). Source unique réutilisée par la fenêtre d'alerte à l'ouverture | M |
| 5b | Calendrier des échéances | Vue calendrier mensuelle / annuelle dans le Reporting URSSAF avec les échéances et l'état (à faire, faite, en retard) | M |
| 5c | Conserver les déclarations de chiffre d'affaires | Aujourd'hui : simple marque « déclarée ». Ajouter : montant déclaré, date, référence / note, historique consultable, écart avec le calculé. Nouvelle table (migration) | M |
| 7.3 | Création contextuelle (compte…) | Bouton « + Nouveau compte » dans la saisie d'écriture, l'import et la liste de filtres ; même principe pour catégories et opérations. Réutilise les fenêtres existantes | M |
| 7.1 | Barre latérale personnalisable | Réordonner par glisser-déposer ou ▲▼, choisir la page d'accueil ; réglages mémorisés par utilisateur | M |
| 6.2 | Graphiques dans Bilan | Résultat produits / charges (barres), évolution de la trésorerie, camembert des charges ; réutilise `BarChart`, `PieChart`, `LineChart` | M |
| 3.4b | Catégories adaptées (matières premières…) | Modèles de catégories par activité (vente de marchandises, prestation, artisanat…) proposés à la création de l'entreprise, avec libellés clairs et numéro de compte FEC pré-rempli | M |
| 1.3a | Profil utilisateur (version courte) | À la première ouverture de l'Autoentreprise : 3 questions (type d'activité, mode de déclaration, achats de matières) qui règlent URSSAF, catégories et fonctions visibles. Voir lot 3 pour la version complète | M |

## Lot 3 — À arbitrer (stratégie produit)

| # | Remarque | Enjeu | Recommandation |
|---|---|---|---|
| 1.2 / 1.3 / 1.4 | Accueil : message de bienvenue intégré, profil, visite guidée, checklist | Refonte du parcours de premier lancement (fenêtres actuelles : base, licence, administrateur, premier exercice) | Faire un **assistant en étapes unique** (base → compte → exercice → profil → comptes bancaires) avec checklist persistante dans l'accueil ; visite guidée en dernier |
| 3.1b | Connexion directe aux banques | Agrégateur d'accès aux comptes (DSP2) : contrat, coût par utilisateur, sécurité, RGPD | Reporter ; commencer par les tutoriels CSV (lot 1) et mesurer la demande |
| 3.4c | Classes du plan comptable | Arborescence par classes (1 à 8) et codes compréhensibles | Proposer une **vue** par classe en lecture (compte FEC) sans changer le modèle ; ne pas imposer le plan comptable à l'Autoentreprise |
| 8 | Mode démonstration | Données fictives pour présenter le logiciel | Créer un « dossier de démonstration » dans un fichier séparé (utilitaire de test existant), bandeau « DÉMO » permanent, jamais mélangé aux vraies données |

## Ordre proposé

1. **1.13 (semaine 1)** : renommage, confirmations d'enregistrement, textes de démarrage, états vides, jours restants + lien URSSAF, retrait des « … », reproduction du point 3.3.
2. **1.13 (semaine 2)** : jetons de couleur et audit, listes filtrables.
3. **1.14** : centre de notifications, calendrier URSSAF, historique des déclarations, création contextuelle de compte.
4. **1.15** : navigation personnalisable, graphiques du bilan, modèles de catégories, profil court.
5. **Ensuite** : assistant d'accueil complet, mode démo ; arbitrages sur la banque et les tarifs.

## Règles à respecter pour toute la suite

- Schéma : uniquement par migrations idempotentes (une pour les déclarations conservées) ; exercices validés intacts.
- Aucun texte tronqué : vérifier par captures d'écran (mode `--shots`) dans les deux versions.
- Une version = un numéro commun ; question du devlog à chaque release.
- **Travail uniquement sur la branche `beta`** (décision du 18/10/2026) : pas de commit sur `main` ni `dev` pour ces
  chantiers ; la fusion vers `main` et la release se font sur demande explicite.

## Questions pour le testeur

1. Version utilisée (numéro, Asso ou Autoentreprise) et capture de l'écran où la ligne de ventilation manque (§3.3).
2. Banque(s) utilisée(s) pour prioriser les tutoriels CSV.
3. Les « … » signalés sont-ils ceux de « Immobiliser… » ou « Importer un FEC… » ?
