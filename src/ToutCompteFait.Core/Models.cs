namespace ToutCompteFait.Core;

public enum AccountKind { Bank, Savings, Cash, Other }

public enum CategoryKind { Product, Charge }

public enum EntryKind
{
    /// <summary>Recette ou dépense ordinaire sur un compte.</summary>
    Normal,
    /// <summary>Virement entre deux comptes de l'association (aucun effet sur le résultat).</summary>
    Transfer,
    /// <summary>Remboursement à un membre d'un achat fait avec son compte personnel (dépense, sortie).</summary>
    MemberRefund,
    /// <summary>Opération sans mouvement d'argent (refacturation interne, régularisation) : ventile un produit et une
    /// charge de même montant. Aucun compte.</summary>
    NoCash
}

public enum Direction { In, Out }

/// <summary>Régularisation entre exercices (cf. onglets « Report » du classeur Excel).</summary>
public enum AdjustmentType
{
    /// <summary>Payé/encaissé l'exercice précédent, concerne cet exercice : s'ajoute au résultat de la catégorie.</summary>
    FromPrev,
    /// <summary>Payé/encaissé cet exercice, concerne le suivant : se retranche du résultat de la catégorie.</summary>
    ToNext,
    /// <summary>Payé/encaissé cet exercice, concernait un exercice déjà clos : se retranche.</summary>
    ForPast,
    /// <summary>Concerne cet exercice mais sera payé/encaissé le suivant (à payer / à recevoir) : s'ajoute.</summary>
    Due
}

public sealed record Account(long Id, string Name, AccountKind Kind, int Position, bool Active, string? StatementMatch = null,
    string? AccountNumber = null);

public sealed record FiscalYear(long Id, string Label, DateOnly Start, DateOnly End, bool Validated,
    DateTime? ValidatedAt, string? ValidatedBy)
{
    public bool Open => !Validated;
    public bool Contains(DateOnly d) => d >= Start && d <= End;
    public override string ToString() => Label;
}

public sealed record Operation(long Id, long YearId, string Name, int Position);

public sealed record Category(long Id, long YearId, CategoryKind Kind, string Name, int Position, long? OperationId);

public sealed record Entry(long Id, long YearId, DateOnly Date, string Label, bool Pointed, EntryKind Kind,
    long? AccountId, Direction Direction, long? ToAccountId, long AmountCents, string? MemberName, string? Note,
    long? RefundOfId = null, bool FromBank = false, long Seq = 0, long? ReversalOfId = null, bool Cancelled = false)
{
    /// <summary>Écriture d'annulation (contre-passation) d'une autre écriture.</summary>
    public bool IsReversal => ReversalOfId is not null;

    /// <summary>L'écriture n'est plus modifiable : annulée, ou annulation elle-même.</summary>
    public bool Frozen => Cancelled || IsReversal;

    /// <summary>Effet net sur la trésorerie de l'association (0 pour un virement interne).</summary>
    public long NetCash => Kind is EntryKind.Transfer or EntryKind.NoCash ? 0 : (Direction == Direction.In ? AmountCents : -AmountCents);
}

public sealed record Allocation(long CategoryId, long AmountCents);

public sealed record Adjustment(long Id, long YearId, AdjustmentType Type, CategoryKind Kind, long? CategoryId,
    string Label, long AmountCents, string? TargetName, long? SourceId, string? Note)
{
    /// <summary>Signe appliqué au résultat de la catégorie.</summary>
    public int Sign => Type is AdjustmentType.FromPrev or AdjustmentType.Due ? 1 : -1;
}

/// <summary>Une écriture avec sa ventilation, telle que lue en base.</summary>
public sealed record EntryRow(Entry Entry, IReadOnlyList<Allocation> Allocations);

public static class Labels
{
    public static string Of(AdjustmentType t) => t switch
    {
        AdjustmentType.FromPrev => "Réglé l'exercice précédent, concerne cet exercice",
        AdjustmentType.ToNext => "Réglé cet exercice, concerne l'exercice suivant",
        AdjustmentType.ForPast => "Réglé cet exercice, concernait un exercice passé",
        AdjustmentType.Due => "Concerne cet exercice, à régler l'exercice suivant",
        _ => t.ToString()
    };

    public static string Of(AccountKind k) => k switch
    {
        AccountKind.Bank => "Compte bancaire",
        AccountKind.Savings => "Livret / épargne",
        AccountKind.Cash => "Caisse (espèces)",
        _ => "Autre (avances, créances…)"
    };

    public static string Of(EntryKind k) => k switch
    {
        EntryKind.Normal => "Recette / dépense",
        EntryKind.Transfer => "Virement entre comptes",
        EntryKind.MemberRefund => "Remboursement à un membre",
        EntryKind.NoCash => "Sans mouvement d'argent (refacturation interne)",
        _ => k.ToString()
    };
}




