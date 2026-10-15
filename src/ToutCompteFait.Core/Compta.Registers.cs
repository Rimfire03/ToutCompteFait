using System.Text;
using ToutCompteFait.Core.Business;
using ToutCompteFait.Core.Export;

namespace ToutCompteFait.Core;

/// <summary>Registre légal d'une micro-entreprise.</summary>
public enum RegisterKind
{
    /// <summary>Livre des recettes (art. 50-0 CGI, art. L123-28 du Code de commerce) : chronologique, avec origine, montant,
    /// mode de règlement et référence de la pièce justificative.</summary>
    Recipes,
    /// <summary>Registre des achats (obligatoire en cas de vente de marchandises) : chronologique, avec fournisseur, nature,
    /// montant, mode de règlement et référence de la pièce.</summary>
    Purchases
}

public sealed record RegisterLine(long Seq, DateOnly Date, string Label, string Nature, long AmountCents,
    string PaymentMode, string PieceRef, int Attachments);

public sealed record RegisterData(RegisterKind Kind, FiscalYear Year, IReadOnlyList<RegisterLine> Lines)
{
    public long Total => Lines.Sum(l => l.AmountCents);

    public string Title => Kind == RegisterKind.Recipes ? "Livre des recettes" : "Registre des achats";

    /// <summary>Lignes sans mode de règlement ou sans référence de pièce : mentions obligatoires manquantes.</summary>
    public int MissingMentions => Lines.Count(l => l.PaymentMode.Length == 0 || l.PieceRef.Length == 0);

    /// <summary>Totaux par nature (catégorie ou type de déclaration).</summary>
    public IEnumerable<(string Nature, long Total)> ByNature =>
        Lines.GroupBy(l => l.Nature).OrderBy(g => g.Key).Select(g => (g.Key, g.Sum(l => l.AmountCents)));
}

public sealed partial class Compta
{
    /// <summary>Livre des recettes ou registre des achats d'un exercice, dans l'ordre chronologique des opérations (puis
    /// numéro d'ordre). Écritures annulées et contre-passations exclues (effet net nul). Recettes : entrées d'argent
    /// ordinaires, nature = produits ventilés (« À classer » si non ventilée). Achats : sorties d'argent ordinaires,
    /// hors dépenses personnelles de l'exploitant.</summary>
    public RegisterData BuildRegister(long yearId, RegisterKind kind)
    {
        var year = Year(yearId) ?? throw new ComptaException("Exercice introuvable.");
        var cats = Categories(yearId).ToDictionary(c => c.Id);
        var counts = AttachmentCounts(yearId);
        var personal = PersonalCategoryNames();
        var personalProducts = PersonalProductNames();
        var urssaf = Editions.Current.IsBusiness ? CategoryUrssafKinds() : null;
        var lines = new List<RegisterLine>();

        foreach (var r in Entries(yearId).OrderBy(r => r.Entry.Date).ThenBy(r => r.Entry.Seq))
        {
            var e = r.Entry;
            if (e.Kind != EntryKind.Normal) continue;
            string nature;
            long amount;
            if (kind == RegisterKind.Recipes)
            {
                if (e.Direction != Direction.In || e.RefundOfId is not null) continue;
                var products = r.Allocations.Where(a => cats.TryGetValue(a.CategoryId, out var c) && c.Kind == CategoryKind.Product
                                                        && !personalProducts.Contains(c.Name)).ToList();
                if (r.Allocations.Count > 0 && products.Count == 0) continue;   // avoir / remboursement / apport personnel : pas une recette
                amount = products.Count == 0 ? e.AmountCents : products.Sum(a => a.AmountCents);
                nature = products.Count == 0
                    ? "À classer"
                    : string.Join(" + ", products.Select(a => NatureName(cats[a.CategoryId].Name, urssaf)).Distinct());
            }
            else
            {
                if (e.Direction != Direction.Out) continue;
                var charges = r.Allocations.Where(a => cats.TryGetValue(a.CategoryId, out var c) && c.Kind == CategoryKind.Charge).ToList();
                if (charges.Count > 0 && charges.All(a => personal.Contains(cats[a.CategoryId].Name))) continue;
                amount = e.AmountCents;
                nature = charges.Count == 0 ? "À classer" : string.Join(" + ", charges.Select(a => cats[a.CategoryId].Name).Distinct());
            }
            lines.Add(new RegisterLine(e.Seq, e.Date, e.Label, nature, amount, PaymentModes.Label(e.PaymentMode),
                e.PieceRef ?? "", counts.GetValueOrDefault(e.Id)));
        }
        return new RegisterData(kind, year, lines);
    }

    private static string NatureName(string categoryName, IReadOnlyDictionary<string, UrssafKind>? urssaf)
    {
        if (urssaf is null) return categoryName;
        var k = ResolveUrssafKind(urssaf, categoryName);
        return k is null ? categoryName : $"{categoryName} ({k.Value.Short()})";
    }

    public string ExportRegisterCsv(long yearId, RegisterKind kind)
    {
        var data = BuildRegister(yearId, kind);
        var supplier = kind == RegisterKind.Recipes ? "Origine (client)" : "Fournisseur";
        var sb = new StringBuilder();
        sb.Append($"N°;Date;{supplier};Nature;Montant;Mode de règlement;Référence de la pièce;Justificatifs joints\r\n");
        foreach (var l in data.Lines)
            sb.Append(l.Seq).Append(';').Append(l.Date.ToString("dd/MM/yyyy")).Append(';').Append(CsvField(l.Label)).Append(';')
              .Append(CsvField(l.Nature)).Append(';').Append(Plain(l.AmountCents)).Append(';').Append(CsvField(l.PaymentMode)).Append(';')
              .Append(CsvField(l.PieceRef)).Append(';').Append(l.Attachments == 0 ? "" : l.Attachments.ToString()).Append("\r\n");
        sb.Append(";;;Total;").Append(Plain(data.Total)).Append(";;;\r\n");
        return sb.ToString();
    }

    public byte[] ExportRegisterPdf(long yearId, RegisterKind kind, string? organization = null)
    {
        var data = BuildRegister(yearId, kind);
        var y = data.Year;
        var profile = GetBusinessProfile();
        var who = !string.IsNullOrWhiteSpace(profile.Name) ? profile.Name! : organization;
        var pdf = new SimplePdf();
        const double left = 30, right = SimplePdf.PageWidth - 30, top = SimplePdf.PageHeight - 40, rowH = 11.5, size = 7.5;
        const double cSeq = left, cDate = left + 26, cLabel = left + 74, cNature = left + 300, cMode = left + 430, cRef = left + 510;
        var supplier = kind == RegisterKind.Recipes ? "Origine (client)" : "Fournisseur";

        double yPos = 0;
        void Header()
        {
            pdf.Text(left, top, data.Title + (string.IsNullOrWhiteSpace(who) ? "" : " - " + who.Trim()), 13, true);
            pdf.Text(left, top - 15, $"Période du {y.Start:dd/MM/yyyy} au {y.End:dd/MM/yyyy}" +
                (string.IsNullOrWhiteSpace(profile.Siren) ? "" : $" - SIREN {profile.Siren}"), 9);
            pdf.Text(left, top - 27, kind == RegisterKind.Recipes
                ? "Recettes encaissées, dans l'ordre chronologique (art. 50-0 CGI, art. L123-28 du Code de commerce)."
                : "Achats et dépenses, dans l'ordre chronologique. Les pièces justificatives sont à conserver 10 ans.", 7);
            yPos = top - 46;
            pdf.Text(cSeq, yPos, "N°", size, true);
            pdf.Text(cDate, yPos, "Date", size, true);
            pdf.Text(cLabel, yPos, supplier, size, true);
            pdf.Text(cNature, yPos, "Nature", size, true);
            pdf.Text(cMode, yPos, "Règlement", size, true);
            pdf.Text(cRef, yPos, "Référence pièce", size, true);
            pdf.TextRight(right, yPos, "Montant", size, true);
            pdf.Line(left, yPos - 3, right, yPos - 3);
            yPos -= rowH + 2;
        }
        Header();
        foreach (var l in data.Lines)
        {
            if (yPos < 50) { pdf.NewPage(); Header(); }
            pdf.TextRight(cSeq + 20, yPos, l.Seq.ToString(), size);
            pdf.Text(cDate, yPos, l.Date.ToString("dd/MM/yyyy"), size);
            pdf.Text(cLabel, yPos, SimplePdf.Fit(l.Label, size, cNature - cLabel - 6), size);
            pdf.Text(cNature, yPos, SimplePdf.Fit(l.Nature, size, cMode - cNature - 6), size);
            pdf.Text(cMode, yPos, SimplePdf.Fit(l.PaymentMode, size, cRef - cMode - 6), size);
            pdf.Text(cRef, yPos, SimplePdf.Fit(l.PieceRef, size, right - 80 - cRef), size);
            pdf.TextRight(right, yPos, Money.Format(l.AmountCents), size);
            yPos -= rowH;
        }
        if (yPos < 90) { pdf.NewPage(); Header(); }
        pdf.Line(left, yPos + 7, right, yPos + 7);
        yPos -= 4;
        foreach (var (nature, total) in data.ByNature)
        {
            pdf.Text(cLabel, yPos, "Total " + SimplePdf.Fit(nature, size, 300), size);
            pdf.TextRight(right, yPos, Money.Format(total), size);
            yPos -= rowH;
            if (yPos < 40) { pdf.NewPage(); Header(); }
        }
        pdf.Text(cLabel, yPos, $"TOTAL - {data.Lines.Count} ligne(s)", size, true);
        pdf.TextRight(right, yPos, Money.Format(data.Total), size, true);
        if (data.MissingMentions > 0)
            pdf.Text(cLabel, yPos - rowH - 2, $"{data.MissingMentions} ligne(s) sans mode de règlement ou sans référence de pièce : à compléter dans le livre.", 7);
        return pdf.Save($"Édité le {DateTime.Now:dd/MM/yyyy HH:mm} - {Editions.AppName}");
    }
}
