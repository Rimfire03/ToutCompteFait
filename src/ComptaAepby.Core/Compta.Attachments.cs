using System.IO.Compression;
using ComptaAepby.Core.Data;
using ComptaAepby.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace ComptaAepby.Core;

/// <summary>Justificatif joint à une écriture (facture, ticket, reçu…). Le contenu est stocké dans la base, donc
/// partagé entre les postes et inclus dans les sauvegardes ; <see cref="Compta.AttachmentData"/> le lit à la demande.</summary>
public sealed record Attachment(long Id, long EntryId, string FileName, string ContentType, long SizeBytes,
    string AddedAt, string? AddedBy);

public sealed partial class Compta
{
    /// <summary>Taille maximale d'un justificatif (le contenu est dans la base : on évite de la gonfler).</summary>
    public const long MaxAttachmentBytes = 10L * 1024 * 1024;

    /// <summary>Extensions refusées : fichiers exécutables ou scripts (un justificatif est un document).</summary>
    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".msi", ".scr", ".pif", ".lnk", ".js", ".jse", ".vbs", ".vbe", ".wsf", ".wsh",
        ".ps1", ".psm1", ".reg", ".dll", ".jar", ".hta", ".cpl", ".msc", ".sh", ".appx", ".msix", ".app", ".gadget"
    };

    private static string ContentTypeOf(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".tif" or ".tiff" => "image/tiff",
        ".webp" => "image/webp",
        ".txt" => "text/plain",
        ".csv" => "text/csv",
        ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xls" => "application/vnd.ms-excel",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".odt" => "application/vnd.oasis.opendocument.text",
        ".ods" => "application/vnd.oasis.opendocument.spreadsheet",
        _ => "application/octet-stream"
    };

    /// <summary>Vérifie un fichier avant de le joindre (type, taille) et renvoie son nom sans chemin ;
    /// <see cref="ComptaException"/> sinon. Utilisable par l'interface avant l'enregistrement de l'écriture.</summary>
    public static string CheckAttachmentFile(string? fileName, long length)
    {
        var name = Path.GetFileName((fileName ?? "").Trim());
        if (name.Length == 0) throw new ComptaException("Nom de fichier invalide.");
        if (name.Length > 300) name = name[..300];
        if (BlockedExtensions.Contains(Path.GetExtension(name)))
            throw new ComptaException($"Ce type de fichier ({Path.GetExtension(name)}) n'est pas accepté comme justificatif.");
        if (length == 0) throw new ComptaException("Le fichier est vide.");
        if (length > MaxAttachmentBytes)
            throw new ComptaException($"Le fichier est trop gros ({length / 1024.0 / 1024.0:0.#} Mo) : " +
                                      $"{MaxAttachmentBytes / 1024 / 1024} Mo maximum par justificatif.");
        return name;
    }

    /// <summary>Justificatifs d'une écriture (sans leur contenu).</summary>
    public List<Attachment> Attachments(long entryId)
    {
        using var l = Open();
        return l.Ctx.Attachments.AsNoTracking().Where(a => a.EntryId == entryId).OrderBy(a => a.Id)
            .Select(a => new { a.Id, a.EntryId, a.FileName, a.ContentType, a.SizeBytes, a.AddedAt, a.AddedBy })
            .AsEnumerable()
            .Select(a => new Attachment(a.Id, a.EntryId, a.FileName, a.ContentType, a.SizeBytes, a.AddedAt, a.AddedBy))
            .ToList();
    }

    /// <summary>Nombre de justificatifs par écriture pour un exercice (pour l'affichage du livre).</summary>
    public Dictionary<long, int> AttachmentCounts(long yearId)
    {
        using var l = Open();
        var ctx = l.Ctx;
        return (from a in ctx.Attachments.AsNoTracking()
                join e in ctx.Entries.AsNoTracking() on a.EntryId equals e.Id
                where e.YearId == yearId
                group a by a.EntryId into g
                select new { EntryId = g.Key, Count = g.Count() })
            .ToDictionary(x => x.EntryId, x => x.Count);
    }

    /// <summary>Contenu d'un justificatif.</summary>
    public byte[] AttachmentData(long attachmentId)
    {
        using var l = Open();
        var row = l.Ctx.Attachments.AsNoTracking().Where(a => a.Id == attachmentId)
                      .Select(a => new { a.Data, a.Compression }).FirstOrDefault()
                  ?? throw new ComptaException("Justificatif introuvable.");
        if (row.Compression != "BROTLI") return row.Data;
        using var input = new MemoryStream(row.Data);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        brotli.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>Compresse sans perte (Brotli) ; renvoie le contenu d'origine si le gain est inférieur à 5 %
    /// (PDF, JPEG… déjà compressés).</summary>
    internal static (byte[] Stored, string Compression) Pack(byte[] data)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
            brotli.Write(data, 0, data.Length);
        return output.Length < data.Length * 0.95 ? (output.ToArray(), "BROTLI") : (data, "NONE");
    }

    /// <summary>Joint un fichier à une écriture (exercice ouvert, droit de saisie).</summary>
    public long AddAttachment(long entryId, string fileName, byte[] data)
    {
        var row = EntryById(entryId) ?? throw new ComptaException("Écriture introuvable.");
        RequireOpen(row.Entry.YearId);
        var name = CheckAttachmentFile(fileName, data.Length);

        using var l = Open();
        var (stored, compression) = Pack(data);
        var e = new AttachmentEntity
        {
            EntryId = entryId, FileName = name, ContentType = ContentTypeOf(name), SizeBytes = data.Length,
            Data = stored, Compression = compression, AddedAt = Now(), AddedBy = UserName
        };
        l.Ctx.Attachments.Add(e);
        Log(l.Ctx, "Justificatif ajouté", $"{row.Entry.Date:dd/MM/yyyy} « {row.Entry.Label} » : {name} ({data.Length / 1024} Ko" +
            (compression == "BROTLI" ? $", compressé en {stored.Length / 1024} Ko" : "") + ")");
        Save(l.Ctx);
        return e.Id;
    }

    public void RemoveAttachment(long attachmentId)
    {
        using var l = Open();
        var ctx = l.Ctx;
        var a = ctx.Attachments.FirstOrDefault(x => x.Id == attachmentId) ?? throw new ComptaException("Justificatif introuvable.");
        var entry = EntryById(a.EntryId) ?? throw new ComptaException("Écriture introuvable.");
        RequireOpen(entry.Entry.YearId);
        ctx.Attachments.Remove(a);
        Log(ctx, "Justificatif retiré", $"{entry.Entry.Date:dd/MM/yyyy} « {entry.Entry.Label} » : {a.FileName}");
        Save(ctx);
    }
}
