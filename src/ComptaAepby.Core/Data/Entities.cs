namespace ComptaAepby.Core.Data;

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
}

/// <summary>Compte d'accès au logiciel (mot de passe haché, jamais en clair).</summary>
public sealed class UserEntity
{
    public long Id { get; set; }
    public string UserName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "READER";
    public bool Active { get; set; } = true;
    public bool MustChangePassword { get; set; }
    public string CreatedAt { get; set; } = "";
    public string? CreatedBy { get; set; }
    public string? LastLoginAt { get; set; }
}
