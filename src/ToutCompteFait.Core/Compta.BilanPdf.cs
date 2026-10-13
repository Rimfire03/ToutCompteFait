using System.Globalization;
using ToutCompteFait.Core.Export;

namespace ToutCompteFait.Core;

/// <summary>Ligne du détail d'une catégorie : une ventilation du livre, ou un report qui la corrige.</summary>
public sealed record CategoryDetailLine(DateOnly Date, string Label, string Account, string CategoryName, CategoryKind Kind,
    string Origin, long Cents, string Comment, bool IsReport);

public sealed partial class Compta
{
    /// <summary>Lignes qui composent le total d'une ou plusieurs catégories d'un exercice : ventilations du livre puis
    /// reports (effet signé). Leur somme (produits − charges) retrouve le résultat de ces catégories dans le bilan.</summary>
    public List<CategoryDetailLine> CategoryDetail(long yearId, IReadOnlyCollection<long> categoryIds)
    {
        var year = Year(yearId) ?? throw new ComptaException("Exercice introuvable.");
        var cats = Categories(yearId).ToDictionary(k => k.Id);
        var accounts = Accounts(includeInactive: true).ToDictionary(a => a.Id, a => a.Name);
        var ids = categoryIds.ToHashSet();
        var lines = new List<CategoryDetailLine>();

        foreach (var r in Entries(yearId).OrderBy(r => r.Entry.Date).ThenBy(r => r.Entry.Seq))
        {
            var e = r.Entry;
            foreach (var a in r.Allocations.Where(a => ids.Contains(a.CategoryId)))
            {
                if (!cats.TryGetValue(a.CategoryId, out var cat)) continue;
                var account = e.AccountId is { } id && accounts.TryGetValue(id, out var n) ? n : "—";
                var comment = string.Join(" · ", new[] { e.Note?.Trim(), e.MemberName }.Where(s => !string.IsNullOrWhiteSpace(s)));
                lines.Add(new CategoryDetailLine(e.Date, e.Label, account, cat.Name, cat.Kind,
                    e.Kind == EntryKind.MemberRefund ? "Remboursement membre" : "Livre", a.AmountCents, comment, false));
            }
        }
        foreach (var adj in Adjustments(yearId).Where(a => a.CategoryId is { } cid && ids.Contains(cid)))
        {
            if (!cats.TryGetValue(adj.CategoryId!.Value, out var cat)) continue;
            lines.Add(new CategoryDetailLine(year.End, $"Report : {adj.Label}", "—", cat.Name, cat.Kind, "Report",
                adj.Sign * adj.AmountCents, Labels.Of(adj.Type) + (string.IsNullOrWhiteSpace(adj.Note) ? "" : " · " + adj.Note), true));
        }
        return lines;
    }

    /// <summary>Bilan &amp; comptes annuels imprimable (PDF A4 portrait). Édition synthétique : synthèse, compte de
    /// fonctionnement, rentabilité des opérations, situation. Édition détaillée : la même chose, puis chaque catégorie
    /// avec toutes ses lignes de compte. L'état de l'exercice (clôturé ou non) figure sur chaque page ; un exercice non
    /// validé porte le filigrane « BILAN PROVISOIRE ».</summary>
    public byte[] ExportBilanPdf(long yearId, bool detailed, string? organization = null, string? editedBy = null)
    {
        var b = ComputeBilan(yearId);
        var y = b.Year;
        var pdf = new SimplePdf(landscape: false);
        const double left = 40, right = 555, bottom = 56;
        var top = pdf.PageH - 42;
        var pos = 0.0;
        var org = string.IsNullOrWhiteSpace(organization) ? Editions.AppName : organization.Trim();
        var title = detailed ? "Bilan & comptes annuels — édition détaillée" : "Bilan & comptes annuels — édition synthétique";
        var closed = y.Validated;
        string Eur(long cents) => Money.Format(cents) + " €";

        // Identité de l'association / entreprise (fiche récupérée par SIREN ou SIRET), si elle est renseignée.
        var identity = "";
        try
        {
            var prof = GetBusinessProfile();
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(prof.Siret) || !string.IsNullOrWhiteSpace(prof.Siren))
                parts.Add(!string.IsNullOrWhiteSpace(prof.Siret) ? "SIRET " + prof.Siret : "SIREN " + prof.Siren);
            if (!string.IsNullOrWhiteSpace(prof.Rna)) parts.Add("RNA " + prof.Rna);
            if (!string.IsNullOrWhiteSpace(prof.Address))
            {
                var addr = prof.Address!;
                if (!string.IsNullOrWhiteSpace(prof.PostalCode) && !addr.Contains(prof.PostalCode!, StringComparison.Ordinal))
                    addr += $", {prof.PostalCode} {prof.City}";
                parts.Add(addr);
            }
            identity = string.Join(" — ", parts);
        }
        catch (Exception) { /* fiche indisponible : l'en-tête reste sans identité */ }
        var dy = identity.Length > 0 ? 13 : 0;

        void Header()
        {
            pdf.SetFill(0.1, 0.15, 0.25);
            pdf.Text(left, top, org, 12, true);
            if (identity.Length > 0)
            {
                pdf.SetFill(0.3, 0.35, 0.42);
                pdf.Text(left, top - 13, SimplePdf.Fit(identity, 8, right - left), 8);
                pdf.SetFill(0.1, 0.15, 0.25);
            }
            pdf.Text(left, top - 18 - dy, title, 10, true);
            pdf.SetFill(0.3, 0.35, 0.42);
            pdf.Text(left, top - 32 - dy, $"Exercice {y.Label} — du {y.Start:dd/MM/yyyy} au {y.End:dd/MM/yyyy}", 9);
            if (closed)
            {
                pdf.SetFill(0.02, 0.37, 0.27);
                pdf.Text(left, top - 46 - dy, $"EXERCICE CLÔTURÉ — validé le {y.ValidatedAt:dd/MM/yyyy}" +
                                         (string.IsNullOrWhiteSpace(y.ValidatedBy) ? "" : $" par {y.ValidatedBy}"), 9, true);
            }
            else
            {
                pdf.SetFill(0.73, 0.11, 0.11);
                pdf.Text(left, top - 46 - dy, "EXERCICE NON CLÔTURÉ — BILAN PROVISOIRE : chiffres susceptibles d'évoluer jusqu'à la validation", 9, true);
            }
            pdf.SetStroke(0.6, 0.65, 0.72);
            pdf.Line(left, top - 54 - dy, right, top - 54 - dy, 0.8);
            pdf.SetFill(0, 0, 0);
            pdf.SetStroke(0, 0, 0);
            pos = top - 72 - dy;
        }

        void Need(double height)
        {
            if (pos - height >= bottom) return;
            pdf.NewPage();
            Header();
        }

        void Section(string text, (double r, double g, double bl)? color = null)
        {
            Need(78);   // titre + en-tête + quelques lignes : jamais de titre orphelin en bas de page
            pos -= 6;
            var c = color ?? (0.1, 0.15, 0.25);
            pdf.SetFill(c.r, c.g, c.bl);
            pdf.Text(left, pos, text, 11, true);
            pdf.SetStroke(c.r, c.g, c.bl);
            pdf.Line(left, pos - 4, right, pos - 4, 1.2);
            pdf.SetFill(0, 0, 0);
            pdf.SetStroke(0, 0, 0);
            pos -= 20;
        }

        // Tableau à nom + 3 colonnes numériques alignées à droite.
        const double c1 = left + 345, c2 = left + 430, c3 = right;
        void Cols(string a, string b1, string b2, string b3, bool header = false, bool total = false)
        {
            Need(16);
            if (header || total)
            {
                if (header) pdf.SetFill(0.93, 0.95, 0.97); else pdf.SetFill(0.96, 0.97, 0.98);
                pdf.FillRect(left, pos - 4, right - left, 15);
                pdf.SetFill(0, 0, 0);
            }
            var bold = header || total;
            pdf.Text(left + 4, pos, SimplePdf.Fit(a, 8.5, c1 - left - 80), 8.5, bold);
            if (b1.Length > 0) pdf.TextRight(c1, pos, b1, 8.5, bold);
            if (b2.Length > 0) pdf.TextRight(c2, pos, b2, 8.5, bold);
            if (b3.Length > 0) pdf.TextRight(c3 - 4, pos, b3, 8.5, bold);
            pdf.SetStroke(0.85, 0.88, 0.92);
            pdf.Line(left, pos - 5, right, pos - 5, 0.4);
            pdf.SetStroke(0, 0, 0);
            pos -= 15;
        }

        string Signed(long cents) => (cents > 0 ? "+" : "") + Money.Format(cents);

        Header();
        var number = 0;
        string N() => (++number) + ". ";

        // ------------------------------------------------------------ synthèse
        Section(N() + "Synthèse de l'exercice");
        Cols("Produits de l'exercice", "", "", Eur(b.ProductsTotal));
        Cols("Charges de l'exercice", "", "", Eur(b.ChargesTotal));
        Cols(b.Result >= 0 ? "Résultat net : excédent" : "Résultat net : déficit", "", "", Eur(b.Result), total: true);
        if (b.EntriesToFill > 0 || b.UnbalancedEntries > 0)
        {
            pdf.SetFill(0.73, 0.11, 0.11);
            pdf.Text(left + 4, pos, $"Attention : {Math.Max(b.EntriesToFill, b.UnbalancedEntries)} écriture(s) du livre sont incomplètes ou non ventilées ; le résultat n'est pas fiable.", 8.5, true);
            pdf.SetFill(0, 0, 0);
            pos -= 15;
        }

        // Résultat négatif : part d'investissement (achats d'immobilisations portés à l'actif, hors résultat).
        if (b.InvestmentNote() is { Length: > 0 } investmentNote)
        {
            // Texte réparti sur des lignes de la largeur de la page.
            var lines = new List<string>();
            var current = "";
            foreach (var word in investmentNote.Split(' '))
            {
                var test = current.Length == 0 ? word : current + " " + word;
                if (SimplePdf.Width(test, 8.5) > right - left - 12 && current.Length > 0) { lines.Add(current); current = word; }
                else current = test;
            }
            if (current.Length > 0) lines.Add(current);
            Need(lines.Count * 12 + 10);
            pdf.SetFill(0.28, 0.33, 0.41);
            foreach (var line in lines) { pdf.Text(left + 4, pos, line, 8.5, false); pos -= 12; }
            pdf.SetFill(0, 0, 0);
            pos -= 3;
        }

        // ------------------------------------------------------------ compte de fonctionnement
        void Table(string name, IReadOnlyList<CategoryResult> list, long total, (double r, double g, double bl) color, bool charges)
        {
            Section(name, color);
            Cols("Catégorie", "Livre", "Reports", "Total", header: true);
            // Les catégories sans aucun mouvement ne sont pas imprimées (le total reste exact).
            foreach (var c in list.Where(x => x.Cash != 0 || x.Adjustments != 0))
                Cols(c.Name, Money.FormatOrBlank(c.Cash), c.Adjustments == 0 ? "" : Signed(c.Adjustments), Money.Format(c.Total));
            if (charges)
            {
                if (b.AssetsReclass != 0) Cols("Achats immobilisés (portés à l'actif)", "", Signed(-b.AssetsReclass), Money.Format(-b.AssetsReclass));
                if (b.AssetsDepreciation != 0) Cols("Dotations aux amortissements", "", Signed(b.AssetsDepreciation), Money.Format(b.AssetsDepreciation));
                if (b.AssetsDisposals != 0) Cols("Valeur nette des immobilisations sorties", "", Signed(b.AssetsDisposals), Money.Format(b.AssetsDisposals));
            }
            Cols("TOTAL " + (charges ? "CHARGES" : "PRODUITS"), Money.Format(list.Sum(x => x.Cash)),
                Money.Format(total - list.Sum(x => x.Cash)), Money.Format(total), total: true);
        }
        pos -= 4;
        Table(N() + "Compte de fonctionnement — Charges", b.OperatingCharges.ToList(), b.ChargesTotal, (0.73, 0.11, 0.11), true);
        pos -= 6;
        Table(number + " (suite). Compte de fonctionnement — Produits", b.Products, b.ProductsTotal, (0.08, 0.5, 0.24), false);
        pdf.SetFill(0.3, 0.35, 0.42);
        Need(14);
        pdf.Text(left + 4, pos, "« Reports » : régularisations entre exercices (payé un exercice pour un autre). Montants en euros.", 7.5);
        pdf.SetFill(0, 0, 0);
        pos -= 12;

        // ------------------------------------------------------------ opérations
        if (b.Operations.Count > 0)
        {
            Section(N() + "Rentabilité des opérations");
            Cols("Opération", "Produits", "Charges", "Rentabilité", header: true);
            foreach (var o in b.Operations)
                Cols(o.Operation.Name, Money.Format(o.ProductsTotal), Money.Format(o.ChargesTotal), Signed(o.Result));
        }

        // ------------------------------------------------------------ situation
        Section(N() + "Situation au dernier jour de l'exercice");
        Cols("ACTIF", "", "", "Montant", header: true);
        foreach (var a in b.Accounts) Cols(a.Account.Name, "", "", Money.Format(a.Closing));
        foreach (var a in b.Assets.Where(a => a.Closing != 0))
            Cols($"Immobilisation : {a.Asset.Label} (valeur nette)", "", "", Money.Format(a.Closing));
        Cols("TOTAL ACTIF", "", "", Eur(b.AssetsTotal), total: true);
        pos -= 6;
        Cols("PASSIF", "", "", "Montant", header: true);
        Cols("Solde initial (ouverture des comptes)", "", "", Money.Format(b.OpeningTotal));
        if (b.AssetsNetOpening != 0) Cols("Immobilisations à l'ouverture (valeur nette reportée)", "", "", Money.Format(b.AssetsNetOpening));
        Cols("Report (régularisations entre exercices)", "", "", Money.Format(b.Report));
        Cols("Résultat de l'exercice", "", "", Money.Format(b.Result));
        if (b.PersonalTotal != 0) Cols("Prélèvements de l'exploitant (dépenses personnelles)", "", "", Money.Format(-b.PersonalTotal));
        if (b.UnallocatedGap != 0) Cols($"Écritures non ventilées ({b.UnbalancedEntries})", "", "", Money.Format(b.UnallocatedGap));
        Cols("TOTAL PASSIF", "", "", Eur(b.LiabilitiesTotal), total: true);
        Need(18);
        if (b.BalanceDifference == 0)
        {
            pdf.SetFill(0.02, 0.37, 0.27);
            pdf.Text(left + 4, pos, "Contrôle : actif = passif, le bilan est équilibré.", 8.5, true);
        }
        else
        {
            pdf.SetFill(0.73, 0.11, 0.11);
            pdf.Text(left + 4, pos, $"Contrôle : écart actif / passif de {Eur(b.BalanceDifference)}.", 8.5, true);
        }
        pdf.SetFill(0, 0, 0);
        pos -= 18;

        // ------------------------------------------------------------ détail des catégories (édition détaillée)
        if (detailed)
        {
            pdf.NewPage();
            Header();
            Section(N() + "Détail des catégories — lignes du livre et reports");
            void Detail(IReadOnlyList<CategoryResult> list, bool charges)
            {
                var color = charges ? (0.73, 0.11, 0.11) : (0.08, 0.5, 0.24);
                foreach (var c in list)
                {
                    if (c.CategoryId is not { } id) continue;
                    var lines = CategoryDetail(yearId, new[] { id });
                    if (lines.Count == 0) continue;
                    Need(64);
                    pos -= 4;
                    pdf.SetFill(color.Item1, color.Item2, color.Item3);
                    pdf.Text(left, pos, (charges ? "CHARGE — " : "PRODUIT — ") + c.Name, 9.5, true);
                    pdf.TextRight(right - 4, pos, Eur(c.Total), 9.5, true);
                    pdf.SetStroke(color.Item1, color.Item2, color.Item3);
                    pdf.Line(left, pos - 4, right, pos - 4, 0.8);
                    pdf.SetFill(0, 0, 0);
                    pdf.SetStroke(0, 0, 0);
                    pos -= 18;
                    foreach (var l in lines)
                    {
                        Need(14);
                        pdf.Text(left + 4, pos, l.IsReport ? "Report" : l.Date.ToString("dd/MM/yyyy"), 8);
                        pdf.Text(left + 62, pos, SimplePdf.Fit(l.Label, 8, 255), 8);
                        pdf.Text(left + 328, pos, SimplePdf.Fit(l.Account, 8, 110), 8);
                        pdf.TextRight(right - 4, pos, Money.Format(l.Cents), 8);
                        pos -= 12;
                    }
                    var sum = lines.Sum(l => l.Cents);
                    Need(16);
                    pdf.SetFill(0.96, 0.97, 0.98);
                    pdf.FillRect(left, pos - 4, right - left, 14);
                    pdf.SetFill(0, 0, 0);
                    pdf.Text(left + 4, pos, $"Total {c.Name} ({lines.Count} ligne(s))", 8.5, true);
                    pdf.TextRight(right - 4, pos, Eur(sum), 8.5, true);
                    pos -= 22;
                }
            }
            Detail(b.Charges, true);
            Detail(b.Products, false);
        }

        // ------------------------------------------------------------ signature
        Need(80);
        pos -= 10;
        if (closed)
        {
            pdf.Text(left, pos, $"Document édité le {DateTime.Now:dd/MM/yyyy} — exercice clôturé le {y.ValidatedAt:dd/MM/yyyy}.", 8.5);
            pos -= 24;
            pdf.Text(left, pos, "Fait à ______________________, le ____ / ____ / ________", 9);
            pdf.Text(left + 330, pos, "Signature :", 9);
            pdf.StrokeRect(left + 330, pos - 52, 185, 46);
        }
        else
        {
            pdf.SetFill(0.73, 0.11, 0.11);
            pdf.Text(left, pos, "Document provisoire, non signé : l'exercice n'est pas clôturé. Il ne tient pas lieu de comptes annuels.", 8.5, true);
            pdf.SetFill(0, 0, 0);
        }

        var foot = $"{org} - Bilan & comptes annuels {y.Label} - édité le {DateTime.Now:dd/MM/yyyy HH:mm}" +
                   (string.IsNullOrWhiteSpace(editedBy) ? "" : $" par {editedBy}") + (closed ? "" : " - PROVISOIRE");
        return pdf.Save(foot, closed ? null : "BILAN PROVISOIRE");
    }
}
