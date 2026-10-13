namespace ToutCompteFait.Core.Data;

// Entités de persistance (EF Core). Les noms de tables et de colonnes sont ceux du premier schéma SQLite du logiciel
// (snake_case) : les bases déjà créées s'ouvrent sans conversion. Dates et énumérations sont stockées en texte
// (« 2026-09-01 », « NORMAL »...), identique sur tous les moteurs ; la conversion se fait dans Compta.

public sealed class AccountEntity
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "BANK";
    public int Position { get; set; }
    public bool Active { get; set; } = true;
    /// <summary>Textes qui identifient ce compte dans l'en-tête d'un relevé téléchargé (séparés par « ; »).</summary>
    public string? StatementMatch { get; set; }
    /// <summary>Numéro de compte comptable de ce compte (export FEC) ; vide = numéro automatique (512…/530…).
    /// Donnée de paramétrage, hors périmètre de l'empreinte des exercices : modifiable à tout moment.</summary>
    public string? AccountNumber { get; set; }
}

/// <summary>Numéro de compte comptable d'une catégorie (par genre P/C et nom, tous exercices confondus) pour l'export
/// FEC. Table séparée des catégories (verrouillées avec leur exercice) : le plan de comptes s'enrichit après clôture
/// sans toucher aux exercices validés.</summary>
public sealed class CategoryAccountEntity
{
    public string Kind { get; set; } = "C";
    public string Name { get; set; } = "";
    public string AccountNumber { get; set; } = "";
}

/// <summary>Réglage de l'entreprise (version Autoentreprise) : profil (SIREN, nom, adresse, codes APE), mode de
/// déclaration, taux de cotisations, plafonds. Clé / valeur, hors des tables verrouillées avec les exercices.</summary>
public sealed class BusinessSettingEntity
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>Immobilisation amortie (matériel acheté et étalé sur sa durée d'utilisation). Registre hors des tables
/// verrouillées avec les exercices : les règles de modification protègent les exercices validés.</summary>
public sealed class FixedAssetEntity
{
    public long Id { get; set; }
    public string Label { get; set; } = "";
    public string AcquiredOn { get; set; } = "";
    public long CostCents { get; set; }
    public int Years { get; set; }

    /// <summary>« M » : prorata au mois ; « D » : prorata au jour.</summary>
    public string Prorata { get; set; } = "M";

    /// <summary>« P » : achat porté en charge dans le livre (neutralisé par l'immobilisation) ; « E » : bien déjà existant
    /// repris à sa valeur nette (aucune charge dans le livre).</summary>
    public string Origin { get; set; } = "P";
    public string? CategoryName { get; set; }
    public long? EntryId { get; set; }
    public string? DisposedOn { get; set; }
    public string? Note { get; set; }
    public string CreatedAt { get; set; } = "";
}

/// <summary>Charge classée « investissement » (par nom, tous exercices confondus) : simple repère, hors des tables
/// verrouillées avec les exercices, modifiable après clôture. La charge reste dans le résultat.</summary>
public sealed class CategoryInvestmentEntity
{
    public string Name { get; set; } = "";
}

/// <summary>Type de déclaration URSSAF d'une catégorie de produit (par nom, tous exercices confondus), comme
/// <see cref="CategoryAccountEntity"/> : modifiable après clôture sans toucher aux exercices validés.</summary>
public sealed class CategoryUrssafEntity
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
}

/// <summary>Déclaration de chiffre d'affaires à l'URSSAF, marquée comme faite (trace : date, montant, auteur).</summary>
public sealed class UrssafDeclarationEntity
{
    public string PeriodKey { get; set; } = "";
    public string PeriodStart { get; set; } = "";
    public string PeriodEnd { get; set; } = "";
    public string DeclaredAt { get; set; } = "";
    public string? DeclaredBy { get; set; }
    public long TurnoverCents { get; set; }
    public string? Note { get; set; }
}

public sealed class YearEntity
{
    public long Id { get; set; }
    public string Label { get; set; } = "";
    public string StartDate { get; set; } = "";
    public string EndDate { get; set; } = "";
    public string Status { get; set; } = "OPEN";
    public string? ValidatedAt { get; set; }
    public string? ValidatedBy { get; set; }
    public string? Checksum { get; set; }
    public string? ClosingJson { get; set; }
}

public sealed class YearOpeningEntity
{
    public long YearId { get; set; }
    public long AccountId { get; set; }
    public long Cents { get; set; }
}

public sealed class OperationEntity
{
    public long Id { get; set; }
    public long YearId { get; set; }
    public string Name { get; set; } = "";
    public int Position { get; set; }
}

public sealed class CategoryEntity
{
    public long Id { get; set; }
    public long YearId { get; set; }
    public string Kind { get; set; } = "C";
    public string Name { get; set; } = "";
    public int Position { get; set; }
    public long? OperationId { get; set; }
}

public sealed class EntryEntity
{
    public long Id { get; set; }
    public long YearId { get; set; }
    public string EntryDate { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Pointed { get; set; }
    public string Kind { get; set; } = "NORMAL";
    public long? AccountId { get; set; }
    public string Direction { get; set; } = "OUT";
    public long? ToAccountId { get; set; }
    public long AmountCents { get; set; }
    public string? MemberName { get; set; }
    public string? Note { get; set; }
    public string? ImportRef { get; set; }
    /// <summary>Remboursement reçu : écriture de dépense d'origine (remboursement partiel ou total).</summary>
    public long? RefundOfId { get; set; }
    /// <summary>Numéro d'ordre de l'écriture dans son exercice : séquentiel, sans trou, attribué à la création
    /// (<see cref="AppDbContext"/>) et jamais réutilisé ; une écriture n'est jamais supprimée, elle est annulée.</summary>
    public long Seq { get; set; }
    /// <summary>Contre-passation : écriture annulée par celle-ci (opposée de l'original, effet net nul).
    /// Une écriture de ce type n'est plus jamais modifiable.</summary>
    public long? ReversalOfId { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

public sealed class AttachmentEntity
{
    public long Id { get; set; }
    public long EntryId { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
    /// <summary>« NONE » (contenu tel quel) ou « BROTLI » (contenu compressé, SizeBytes = taille d'origine).</summary>
    public string Compression { get; set; } = "NONE";
    public string AddedAt { get; set; } = "";
    public string? AddedBy { get; set; }
}

public sealed class AllocationEntity
{
    public long Id { get; set; }
    public long EntryId { get; set; }
    public long CategoryId { get; set; }
    public long AmountCents { get; set; }
}

public sealed class AdjustmentEntity
{
    public long Id { get; set; }
    public long YearId { get; set; }
    public string Type { get; set; } = "TO_NEXT";
    public string Kind { get; set; } = "C";
    public long? CategoryId { get; set; }
    public string Label { get; set; } = "";
    public long AmountCents { get; set; }
    public string? TargetName { get; set; }
    public long? SourceId { get; set; }
    public string? Note { get; set; }
}

public sealed class AuditEntity
{
    public long Id { get; set; }
    public string At { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Action { get; set; } = "";
    public string? Detail { get; set; }
    /// <summary>Chaînage : empreinte de la ligne précédente (« » pour la première) et empreinte de celle-ci
    /// (<see cref="AuditChain"/>). Une ligne chaînée ne peut plus être modifiée ni supprimée.</summary>
    public string? PrevHash { get; set; }
    public string? Hash { get; set; }
}

/// <summary>Compte d'accès au logiciel (mot de passe haché, jamais en clair).</summary>
public sealed class UserEntity
{
    public long Id { get; set; }
    public string UserName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "READER";
    /// <summary>Statut Développeur, en plus du rôle : configurations multiples de l'application.</summary>
    public bool IsDeveloper { get; set; }
    public bool Active { get; set; } = true;
    public bool MustChangePassword { get; set; }
    public string CreatedAt { get; set; } = "";
    public string? CreatedBy { get; set; }
    public string? LastLoginAt { get; set; }
}
