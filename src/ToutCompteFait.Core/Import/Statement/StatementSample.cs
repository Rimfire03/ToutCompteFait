using System.Globalization;

namespace ToutCompteFait.Core.Import;

public sealed record SampleOp(DateOnly Date, string Label, long Cents, string? Continuation = null);

public enum SampleAmountLayout { DebitCredit, SignedAmount }

public sealed class SampleOptions
{
    public SampleAmountLayout Layout { get; set; } = SampleAmountLayout.DebitCredit;
    public bool ShortDates { get; set; }
    public bool ValueDate { get; set; } = true;
    public bool RunningBalance { get; set; }
    public bool BalanceLines { get; set; } = true;
    public bool Totals { get; set; } = true;
    public bool Noise { get; set; } = true;
    public int RowsPerPage { get; set; } = 14;
    public double PageWidth { get; set; } = 1000;
}

/// <summary>
/// Fabrique des pages « comme un OCR les rendrait » (mots + rectangles) à partir d'opérations connues. Sert aux tests du
/// moteur de lecture et au test de bout en bout (dessin de l'image → OCR Windows → lecture) : on connaît ainsi la bonne
/// réponse sans avoir besoin d'un vrai relevé.
/// </summary>
public static class StatementSample
{
    private const double CharW = 8.0;
    private const double WordH = 16.0;

    public static string FormatAmount(long cents, bool signed = false)
    {
        var abs = Math.Abs(cents);
        var euros = abs / 100;
        var s = euros.ToString("N0", CultureInfo.GetCultureInfo("fr-FR")).Replace(' ', ' ').Replace(' ', ' ');
        s += "," + (abs % 100).ToString("00", CultureInfo.InvariantCulture);
        return signed && cents < 0 ? "-" + s : s;
    }

    public static List<StatementPage> Build(IReadOnlyList<SampleOp> ops, long openingCents, SampleOptions? options = null)
    {
        var o = options ?? new SampleOptions();
        var pages = new List<StatementPage>();
        var dateX = 40.0;
        var valueX = 130.0;
        var labelX = o.ValueDate ? 230.0 : 140.0;
        var debitR = 700.0;
        var creditR = 800.0;
        var amountR = 760.0;
        var balanceR = 940.0;

        var first = ops.Count > 0 ? ops.Min(x => x.Date) : new DateOnly(2026, 9, 1);
        var last = ops.Count > 0 ? ops.Max(x => x.Date) : new DateOnly(2026, 9, 30);
        var closing = openingCents + ops.Sum(x => x.Cents);
        var totalPages = Math.Max(1, (int)Math.Ceiling(ops.Count / (double)o.RowsPerPage));

        var running = openingCents;
        for (var p = 0; p < totalPages; p++)
        {
            var words = new List<StatementWord>();
            var y = 40.0;

            void Text(string text, double x, double yy)
            {
                foreach (var w in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    words.Add(new StatementWord(w, x, yy, w.Length * CharW, WordH));
                    x += w.Length * CharW + CharW * 0.6;
                }
            }
            void RightText(string text, double right, double yy)
            {
                var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var x = right;
                for (var i = parts.Length - 1; i >= 0; i--)
                {
                    x -= parts[i].Length * CharW;
                    words.Add(new StatementWord(parts[i], x, yy, parts[i].Length * CharW, WordH));
                    x -= CharW * 0.6;
                }
            }

            if (p == 0)
            {
                Text("CAISSE D'EPARGNE ASSOCIATION EXEMPLE", dateX, y);
                y += 28;
                Text("RELEVE DE COMPTE", dateX, y);
                y += 22;
                Text($"du {first:dd/MM/yyyy} au {last:dd/MM/yyyy}", dateX, y);
                y += 22;
                if (o.Noise) { Text("IBAN FR76 1234 5678 9012 3456 7890 123 BIC CEPAFRPP", dateX, y); y += 30; }
                else y += 8;
            }
            else y += 30;

            // en-tête de colonnes (répété sur chaque page)
            var headerY = y;
            Text("Date", dateX, y);
            if (o.ValueDate) Text("Date valeur", valueX, y);
            Text("Libellé", labelX, y);
            if (o.Layout == SampleAmountLayout.DebitCredit)
            {
                RightText("Débit", debitR, y);
                RightText("Crédit", creditR, y);
            }
            else RightText("Montant", amountR, y);
            if (o.RunningBalance) RightText("Solde", balanceR, y);
            y += 30;

            if (p == 0 && o.BalanceLines)
            {
                Text($"ANCIEN SOLDE AU {first.AddDays(-1):dd/MM/yyyy}", labelX, y);
                if (o.Layout == SampleAmountLayout.DebitCredit)
                    RightText(FormatAmount(openingCents), openingCents < 0 ? debitR : creditR, y);
                else RightText(FormatAmount(openingCents, true), amountR, y);
                y += 26;
            }

            foreach (var op in ops.Skip(p * o.RowsPerPage).Take(o.RowsPerPage))
            {
                var dt = o.ShortDates ? op.Date.ToString("dd/MM", CultureInfo.InvariantCulture) : op.Date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
                Text(dt, dateX, y);
                if (o.ValueDate) Text(dt, valueX, y);
                Text(op.Label, labelX, y);
                if (o.Layout == SampleAmountLayout.DebitCredit)
                    RightText(FormatAmount(op.Cents), op.Cents < 0 ? debitR : creditR, y);
                else RightText(FormatAmount(op.Cents, true), amountR, y);
                running += op.Cents;
                if (o.RunningBalance) RightText(FormatAmount(running, true), balanceR, y);
                y += 20;
                if (op.Continuation is not null)
                {
                    Text(op.Continuation, labelX, y);
                    y += 20;
                }
                y += 6;
            }

            if (p == totalPages - 1)
            {
                y += 10;
                if (o.Totals && o.Layout == SampleAmountLayout.DebitCredit)
                {
                    Text("TOTAL DES MOUVEMENTS", labelX, y);
                    RightText(FormatAmount(-ops.Where(x => x.Cents < 0).Sum(x => x.Cents)), debitR, y);
                    RightText(FormatAmount(ops.Where(x => x.Cents > 0).Sum(x => x.Cents)), creditR, y);
                    y += 26;
                }
                if (o.BalanceLines)
                {
                    Text($"NOUVEAU SOLDE AU {last:dd/MM/yyyy}", labelX, y);
                    if (o.Layout == SampleAmountLayout.DebitCredit)
                        RightText(FormatAmount(closing), closing < 0 ? debitR : creditR, y);
                    else RightText(FormatAmount(closing, true), amountR, y);
                    y += 26;
                }
            }
            else
            {
                Text("Suite page suivante", labelX, y + 10);
            }

            if (o.Noise)
            {
                Text($"Page {p + 1}/{totalPages}", dateX, 1300);
                Text("Caisse d'Epargne et de Prévoyance - Siège social - RCS - ORIAS", dateX, 1330);
            }
            pages.Add(new StatementPage(p + 1, o.PageWidth, 1400, words));
        }
        return pages;
    }
}
