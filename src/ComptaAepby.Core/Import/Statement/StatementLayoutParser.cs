using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ComptaAepby.Core.Import;

/// <summary>
/// Transforme les mots (avec position) d'un relevé bancaire — texte d'un PDF numérique ou résultat d'OCR — en
/// opérations datées et chiffrées. Aucune dépendance à Windows : le même code lit les deux sources et se teste
/// sans OCR. Rien n'est « deviné en silence » : toute correction ou ambiguïté est signalée sur la ligne concernée,
/// et <see cref="StatementChecker"/> vérifie l'arithmétique du relevé (soldes, totaux, solde courant).
/// </summary>
public static class StatementLayoutParser
{
    // ------------------------------------------------------------------ structures internes

    private sealed class Line
    {
        public List<StatementWord> Words = new();
        public double Top, Bottom, CenterY;
        public double Height => Bottom - Top;
    }

    private sealed class PageData
    {
        public StatementPage Page = null!;
        public List<Line> Lines = new();
        public double MedianHeight = 10;
    }

    private sealed class Layout
    {
        public List<StatementColumn> Columns = new();
        public double PageWidth;
        public StatementColumn? Label => Columns.FirstOrDefault(c => c.Kind == StatementColumnKind.Label);
        public StatementColumn? FirstNumeric => Columns.Where(c => c.IsNumeric).OrderBy(c => c.Left).FirstOrDefault();
        public Layout Scaled(double width)
        {
            if (PageWidth <= 0 || Math.Abs(width - PageWidth) < 1) return this;
            var k = width / PageWidth;
            return new Layout
            {
                PageWidth = width,
                Columns = Columns.Select(c => c with { Left = c.Left * k, Right = c.Right * k }).ToList()
            };
        }
    }

    private sealed class AmountTok
    {
        public long Cents;               // valeur absolue
        public bool Negative;            // signe explicite lu (« - », parenthèses)
        public double Left, Right;
        public bool Repaired;
        public string Text = "";
        public StatementColumn? Col;
    }

    private sealed class LineInfo
    {
        public string Norm = "";
        public bool HasDate;
        public int? Day, Month, Year;
        public string DateText = "";
        public bool DateRepaired;
        public List<AmountTok> Amounts = new();
        public string Label = "";
        public bool AnyRepaired;
    }

    // ------------------------------------------------------------------ point d'entrée

    /// <summary>Lit un relevé et rend la première section qui contient des opérations (voir <see cref="ParseSections"/>
    /// pour un PDF qui regroupe plusieurs comptes).</summary>
    public static StatementReadResult Parse(IReadOnlyList<StatementPage> pages, StatementProfile? profile = null, int? defaultYear = null)
    {
        var sections = ParseSections(pages, profile, defaultYear);
        return sections.FirstOrDefault(s => s.Rows.Count > 0) ?? sections[0];
    }

    /// <summary>Un PDF peut regrouper plusieurs comptes (compte courant, livret…) : une section par compte, repérée par le
    /// numéro de compte imprimé au-dessus de chaque tableau.</summary>
    public static List<StatementReadResult> ParseSections(IReadOnlyList<StatementPage> pages, StatementProfile? profile = null, int? defaultYear = null)
    {
        profile ??= StatementProfiles.Generic();
        var data = pages.Select(Prepare).ToList();
        if (data.All(d => d.Lines.Count == 0))
        {
            var empty = new StatementReadResult { PageCount = pages.Count };
            empty.Messages.Add("Aucun texte n'a été lu sur ce document.");
            return new List<StatementReadResult> { empty };
        }

        var docMessages = new List<string>();
        var (periodStart, periodEnd) = DetectPeriod(data);

        // Passe 1 : blocs d'en-tête de colonnes (éventuellement sur plusieurs lignes) ; sinon déduction par les montants.
        var headers = new Dictionary<(int page, int line), Layout>();
        var consumed = new HashSet<(int page, int line)>();
        for (var p = 0; p < data.Count; p++)
        {
            var l = 0;
            while (l < data[p].Lines.Count)
            {
                var block = DetectHeaderBlock(data[p], l, profile);
                if (block is null) { l++; continue; }
                headers[(p, block.Value.Last)] = new Layout { Columns = block.Value.Columns, PageWidth = data[p].Page.Width };
                foreach (var i in block.Value.Lines) consumed.Add((p, i));
                l = block.Value.Last + 1;
            }
        }
        Layout? inferred = null;
        if (headers.Count == 0)
        {
            inferred = InferLayout(data, docMessages);
            if (inferred is null)
            {
                var none = new StatementReadResult { PageCount = pages.Count, PeriodStart = periodStart, PeriodEnd = periodEnd };
                none.Messages.Add("Les en-têtes de colonnes (Date, Libellé, Débit, Crédit…) n'ont pas été reconnus et la position " +
                                  "des montants ne permet pas de les déduire : choisissez un autre profil ou saisissez les opérations à la main.");
                return new List<StatementReadResult> { none };
            }
        }

        // Passe 2 : lecture des lignes.
        var st = new State { Profile = profile, DefaultYear = defaultYear, PeriodStart = periodStart, PeriodEnd = periodEnd, PageCount = pages.Count };
        var layout = inferred;
        for (var p = 0; p < data.Count; p++)
        {
            var pd = data[p];
            if (layout is not null && layout.PageWidth > 0) layout = layout.Scaled(pd.Page.Width);
            Line? prev = null;
            for (var l = 0; l < pd.Lines.Count; l++)
            {
                var line = pd.Lines[l];
                DetectAccount(st, line);
                if (consumed.Contains((p, l)))
                {
                    if (headers.TryGetValue((p, l), out var h))
                    {
                        st.Flush();
                        layout = h;
                        st.TableEnded = false;
                    }
                    prev = null;
                    continue;
                }
                ProcessLine(st, pd, line, prev, layout, p + 1);
                prev = line;
            }
            st.Flush();
        }
        st.Flush();

        var sections = st.Sections.Where(s => s.Rows.Count > 0 || s.OpeningCents is not null || s.ClosingCents is not null).ToList();
        if (sections.Count == 0) sections.Add(st.Sections.FirstOrDefault() ?? new StatementReadResult());

        // Totaux du résumé imprimé avant les tableaux : retenus seulement s'ils correspondent au compte (sinon ils
        // concernent un autre compte du même relevé et ne servent pas au contrôle).
        if (st.PendingDebits is not null || st.PendingCredits is not null)
        {
            var first = sections.FirstOrDefault(s => s.Rows.Count > 0);
            if (first is not null)
            {
                var debits = -first.Rows.Where(r => r.AmountCents < 0).Sum(r => r.AmountCents ?? 0);
                var credits = first.Rows.Where(r => r.AmountCents > 0).Sum(r => r.AmountCents ?? 0);
                if (st.PendingDebits is not null && st.PendingDebits == debits) first.TotalDebitsCents = debits;
                if (st.PendingCredits is not null && st.PendingCredits == credits) first.TotalCreditsCents = credits;
            }
        }

        foreach (var s in sections)
        {
            s.PageCount = pages.Count;
            s.PeriodStart = periodStart;
            s.PeriodEnd = periodEnd;
            s.Messages.AddRange(docMessages);
            if (st.YearMissing && s.Rows.Any(r => r.Date is null))
                s.Messages.Add("L'année des dates n'apparaît pas sur le relevé : indiquez-la pour compléter les dates.");
            if (st.AmountColRows >= 2 && st.AmountColNegative == 0)
                s.Messages.Add("Une colonne « Montant » sans signe a été trouvée : le sens (débit ou crédit) n'est pas lisible. " +
                               "Le contrôle des soldes dira si les montants sont dans le bon sens.");
            if (s.Rows.Count == 0 && sections.Count == 1)
                s.Messages.Add("Aucune opération reconnue : vérifiez le profil choisi ou la qualité du document.");
        }
        return sections;
    }

    private sealed class State
    {
        public StatementProfile Profile = null!;
        public int? DefaultYear;
        public DateOnly? PeriodStart, PeriodEnd;
        public int PageCount;
        public List<StatementReadResult> Sections = new();
        public StatementReadResult? Cur;
        public string? Account, AccountLabel;
        public StatementRow? Current;
        public bool TableEnded;
        public bool YearMissing;
        public int AmountColRows, AmountColNegative;
        public long? PendingDebits, PendingCredits;

        public StatementReadResult Section()
        {
            if (Cur is null)
            {
                Cur = new StatementReadResult { AccountNumber = Account, AccountLabel = AccountLabel };
                Sections.Add(Cur);
            }
            return Cur;
        }

        public int RowsInSection => (Cur?.Rows.Count ?? 0) + (Current is null ? 0 : 1);

        public void Flush()
        {
            if (Current is null) return;
            Section().Rows.Add(Current);
            Current = null;
        }

        public void SetAccount(string number, string label)
        {
            if (number == Account) return;
            Flush();
            Account = number;
            AccountLabel = label;
            TableEnded = false;
            if (Cur is not null)
            {
                if (Cur.Rows.Count > 0 || Cur.OpeningCents is not null || Cur.ClosingCents is not null) Cur = null;
                else
                {
                    Cur.AccountNumber = number;
                    Cur.AccountLabel = label;
                }
            }
        }
    }

    private static readonly Regex AccountRx = new(
        @"(?<label>(?:compte|livret)[a-z0-9 '\-]*?)\s*n°\s*(?:\d{5}\s+\d{5}\s+)?(?<num>\d{11})\s+\d{2}\b", RegexOptions.Compiled);

    private static void DetectAccount(State st, Line line)
    {
        var norm = Norm(string.Join(' ', line.Words.Select(w => w.Text)));
        if (norm.Contains("solde", StringComparison.Ordinal) || norm.Contains("iban", StringComparison.Ordinal)) return;
        var m = AccountRx.Match(norm);
        if (!m.Success) return;
        var label = Regex.Replace(m.Groups["label"].Value, @"\s+", " ").Trim().ToUpperInvariant();
        st.SetAccount(m.Groups["num"].Value, label);
    }
    // ------------------------------------------------------------------ lignes

    private static readonly Regex SignTarget = new(@"^(\d{1,3}|\d{1,3}(?:[ .']\d{3})+[,.]\d{2}|\d+[,.]\d{2})$", RegexOptions.Compiled);

    private static PageData Prepare(StatementPage page)
    {
        var pd = new PageData { Page = page };
        // Les caractères isolés dans la marge gauche (codes de traitement imprimés à la verticale) ne sont pas du contenu.
        var minRight = page.Width * 0.035;
        var words = page.Words.Where(w => !string.IsNullOrWhiteSpace(w.Text) && w.Height > 0 && w.Right > minRight).ToList();
        if (words.Count == 0) return pd;
        var heights = words.Select(w => w.Height).OrderBy(h => h).ToList();
        pd.MedianHeight = Math.Max(heights[heights.Count / 2], 1);
        words = MergeSigns(words, pd.MedianHeight).OrderBy(w => w.CenterY).ToList();
        var tol = pd.MedianHeight * 0.55;
        Line? cur = null;
        foreach (var w in words)
        {
            if (cur is not null && Math.Abs(w.CenterY - cur.CenterY) <= tol)
            {
                cur.Words.Add(w);
                cur.CenterY = cur.Words.Average(x => x.CenterY);
            }
            else
            {
                cur = new Line { CenterY = w.CenterY };
                cur.Words.Add(w);
                pd.Lines.Add(cur);
            }
        }
        foreach (var l in pd.Lines)
        {
            l.Words = l.Words.OrderBy(w => w.X).ToList();
            l.Top = l.Words.Min(w => w.Y);
            l.Bottom = l.Words.Max(w => w.Bottom);
        }
        return pd;
    }

    /// <summary>Un « - » ou « + » est souvent imprimé comme un mot à part, légèrement décalé verticalement : il est
    /// rattaché au montant situé juste à sa droite.</summary>
    private static List<StatementWord> MergeSigns(List<StatementWord> words, double medH)
    {
        var result = new List<StatementWord>(words);
        var drop = new HashSet<int>();
        var replaced = new Dictionary<int, StatementWord>();
        for (var si = 0; si < words.Count; si++)
        {
            var s = words[si];
            if (s.Text is not ("-" or "+" or "–" or "−" or "—")) continue;
            var bestIdx = -1;
            var bestDx = double.MaxValue;
            for (var ti = 0; ti < words.Count; ti++)
            {
                if (ti == si || replaced.ContainsKey(ti) || !SignTarget.IsMatch(words[ti].Text)) continue;
                var dx = words[ti].X - s.Right;
                var dy = Math.Abs(words[ti].CenterY - s.CenterY);
                if (dx >= -1.5 && dx <= medH * 1.5 && dy <= medH * 1.1 && dx < bestDx)
                {
                    bestDx = dx;
                    bestIdx = ti;
                }
            }
            if (bestIdx < 0) continue;
            var t = words[bestIdx];
            replaced[bestIdx] = new StatementWord(s.Text + t.Text, s.X, t.Y, t.Right - s.X, t.Height);
            drop.Add(si);
        }
        result.Clear();
        for (var i = 0; i < words.Count; i++)
        {
            if (drop.Contains(i)) continue;
            result.Add(replaced.TryGetValue(i, out var r) ? r : words[i]);
        }
        return result;
    }
    // ------------------------------------------------------------------ normalisation

    internal static string Norm(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(ch switch { '’' or '‘' or '`' => '\'', ' ' or ' ' => ' ', _ => char.ToLowerInvariant(ch) });
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private static string Token(string s) => Norm(s).Trim(':', ';', '.', '(', ')', '*');

    // ------------------------------------------------------------------ en-têtes

    private static readonly HashSet<string> CurrencyWords = new() { "eur", "euro", "euros", "€", "(eur)", "(euros)", "en" };

    private static readonly HashSet<string> HeaderVocab = new()
    {
        "date", "jour", "valeur", "operation", "operations", "d'operation", "detail", "details", "libelle", "libelles", "nature",
        "montant", "montants", "debit", "debits", "credit", "credits", "solde", "eur", "euros", "en", "de", "des", "designation",
        "description", "intitule", "mouvement", "mouvements", "reference"
    };

    private static string HeaderToken(string s) =>
        Regex.Replace(Norm(s), @"\(.*?\)", "").Trim('(', ')', ':', ';', '*', '.');

    private readonly record struct HeaderBlock(List<int> Lines, int Last, List<StatementColumn> Columns);

    /// <summary>
    /// En-tête de colonnes : une ligne contenant « Montant » (ou « Débit » / « Crédit »), complétée des lignes voisines
    /// qui ne contiennent que du vocabulaire d'en-tête (les relevés bancaires empilent souvent « DATE / D'OPERATION »
    /// sur deux ou trois lignes). Les mots sont regroupés par colonne d'après leur position horizontale.
    /// </summary>
    private static HeaderBlock? DetectHeaderBlock(PageData pd, int idx, StatementProfile p)
    {
        var anchor = pd.Lines[idx];
        bool IsAmountHeaderWord(string t) => p.DebitHeaders.Contains(t) || p.CreditHeaders.Contains(t) || p.AmountHeaders.Contains(t);
        bool NoData(Line l) => !l.Words.Any(w => TryAmount(w.Text, out _, out _, out _) || LooksLikeDateWord(w.Text));
        if (!anchor.Words.Any(w => IsAmountHeaderWord(HeaderToken(w.Text))) || !NoData(anchor)) return null;

        var h = pd.MedianHeight;
        bool Adjacent(Line a, Line b) => b.Top - a.Bottom <= h * 2.5 && a.Top - b.Bottom <= h * 2.5;
        bool Neighbour(Line l) => NoData(l) && l.Words.Any(w => HeaderVocab.Contains(HeaderToken(w.Text)) ||
                                                              p.DateHeaders.Contains(HeaderToken(w.Text)) ||
                                                              p.LabelHeaders.Contains(HeaderToken(w.Text)));
        var lines = new List<int> { idx };
        var top = anchor;
        for (var i = idx - 1; i >= 0 && idx - i <= 2; i--)
        {
            if (!Adjacent(pd.Lines[i], top) || !Neighbour(pd.Lines[i])) break;
            lines.Insert(0, i);
            top = pd.Lines[i];
        }
        var bottom = anchor;
        for (var i = idx + 1; i < pd.Lines.Count && i - idx <= 2; i++)
        {
            if (!Adjacent(bottom, pd.Lines[i]) || !Neighbour(pd.Lines[i])) break;
            lines.Add(i);
            bottom = pd.Lines[i];
        }

        // Regroupement des mots par colonne (recouvrement horizontal ou écart inférieur à un mot).
        var words = lines.SelectMany(i => pd.Lines[i].Words).OrderBy(w => w.X).ToList();
        var clusters = new List<List<StatementWord>>();
        foreach (var w in words)
        {
            var c = clusters.FirstOrDefault(cl => w.X <= cl.Max(x => x.Right) + h * 1.0 && w.Right >= cl.Min(x => x.X) - h * 1.0);
            if (c is null) clusters.Add(new List<StatementWord> { w });
            else c.Add(w);
        }

        var cols = new List<StatementColumn>();
        foreach (var cl in clusters)
        {
            var toks = cl.Select(w => HeaderToken(w.Text)).Where(t => t.Length > 0).ToHashSet();
            StatementColumnKind? kind = null;
            if (toks.Overlaps(p.ValueHeaders)) kind = StatementColumnKind.ValueDate;
            else if (toks.Overlaps(p.DebitHeaders)) kind = StatementColumnKind.Debit;
            else if (toks.Overlaps(p.CreditHeaders)) kind = StatementColumnKind.Credit;
            else if (toks.Overlaps(p.AmountHeaders)) kind = StatementColumnKind.Amount;
            else if (toks.Overlaps(p.BalanceHeaders)) kind = StatementColumnKind.Balance;
            else if (toks.Overlaps(p.DateHeaders)) kind = StatementColumnKind.Date;
            else if (toks.Overlaps(p.LabelHeaders)) kind = StatementColumnKind.Label;
            if (kind is null) continue;
            cols.Add(new StatementColumn(kind.Value, cl.Min(w => w.X), cl.Max(w => w.Right),
                string.Join(' ', cl.OrderBy(w => w.CenterY).ThenBy(w => w.X).Select(w => w.Text))));
        }

        var hasAmount = cols.Any(c => c.Kind is StatementColumnKind.Debit or StatementColumnKind.Credit or StatementColumnKind.Amount);
        var hasText = cols.Any(c => c.Kind is StatementColumnKind.Date or StatementColumnKind.Label);
        if (!hasAmount || !hasText) return null;
        return new HeaderBlock(lines, lines[^1], cols.OrderBy(c => c.Left).ToList());
    }
    // ------------------------------------------------------------------ déduction sans en-tête

    private static Layout? InferLayout(List<PageData> data, List<string> messages)
    {
        foreach (var pd in data)
        {
            var rights = new List<(double L, double R)>();
            var lefts = new List<double>();
            var labelLefts = new List<double>();
            foreach (var line in pd.Lines)
            {
                var info = Analyze(line, null, pd.MedianHeight);
                if (!info.HasDate || info.Amounts.Count == 0) continue;
                rights.AddRange(info.Amounts.Select(a => (a.Left, a.Right)));
                lefts.Add(line.Words[0].X);
                var lw = line.Words.FirstOrDefault(w => !LooksLikeDateWord(w.Text) && !TryAmount(w.Text, out _, out _, out _));
                if (lw is not null) labelLefts.Add(lw.X);
            }
            if (lefts.Count < 3) continue;

            var tol = pd.MedianHeight * 2.5;
            var clusters = new List<List<(double L, double R)>>();
            foreach (var r in rights.OrderBy(x => x.R))
            {
                if (clusters.Count > 0 && r.R - clusters[^1].Average(c => c.R) <= tol) clusters[^1].Add(r);
                else clusters.Add(new List<(double, double)> { r });
            }
            clusters = clusters.Where(c => c.Count >= Math.Max(2, lefts.Count / 5)).ToList();
            if (clusters.Count is 0 or > 3) continue;

            var kinds = clusters.Count switch
            {
                1 => new[] { StatementColumnKind.Amount },
                2 => new[] { StatementColumnKind.Debit, StatementColumnKind.Credit },
                _ => new[] { StatementColumnKind.Debit, StatementColumnKind.Credit, StatementColumnKind.Balance }
            };
            var layout = new Layout { PageWidth = pd.Page.Width };
            var dateLeft = lefts.Min();
            layout.Columns.Add(new StatementColumn(StatementColumnKind.Date, dateLeft, dateLeft + pd.MedianHeight * 4, "(déduit)"));
            var labelLeft = labelLefts.Count > 0 ? labelLefts.Min() : dateLeft + pd.MedianHeight * 5;
            layout.Columns.Add(new StatementColumn(StatementColumnKind.Label, labelLeft, clusters[0].Min(c => c.L), "(déduit)"));
            for (var i = 0; i < clusters.Count; i++)
                layout.Columns.Add(new StatementColumn(kinds[i], clusters[i].Min(c => c.L), clusters[i].Max(c => c.R), "(déduit)"));
            messages.Add("En-têtes de colonnes introuvables : colonnes déduites de la position des montants" +
                                (clusters.Count >= 2 ? " (débit à gauche, crédit à droite)" : "") +
                                ". Le contrôle des soldes confirmera ou non cette lecture.");
            return layout;
        }
        return null;
    }

    // ------------------------------------------------------------------ période

    private static readonly Regex FullDate = new(@"(?<!\d)(\d{2})[/.](\d{2})[/.](\d{4})(?!\d)", RegexOptions.Compiled);

    private static (DateOnly? Start, DateOnly? End) DetectPeriod(List<PageData> data)
    {
        var dates = new List<DateOnly>();
        foreach (var pd in data)
            foreach (var line in pd.Lines)
            {
                // Les dates des opérations commencent la ligne ; celles de l'en-tête (« du … au … », « solde au … ») non.
                var text = string.Join(' ', line.Words.Select(w => w.Text));
                foreach (Match m in FullDate.Matches(text))
                {
                    var prefix = text[..m.Index].Trim();
                    if (Regex.IsMatch(prefix, @"^(\d{1,2}[/.\-]\d{1,2}([/.\-]\d{2,4})?\s*)*$")) continue;
                    if (DateOnly.TryParseExact(m.Value.Replace('.', '/'), "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                        && d.Year is >= 2000 and <= 2100)
                        dates.Add(d);
                }
            }
        return dates.Count == 0 ? (null, null) : (dates.Min(), dates.Max());
    }
    // ------------------------------------------------------------------ analyse d'une ligne

    private static readonly string[] Months =
        { "janv", "fevr", "mars", "avr", "mai", "juin", "juil", "aout", "sept", "oct", "nov", "dec" };

    private static readonly Regex DateRx = new(@"^(\d{1,2})[/.\-](\d{1,2})(?:[/.\-](\d{4}|\d{2}))?$", RegexOptions.Compiled);

    private static string Lookalike(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(c switch { 'O' or 'o' or 'Q' => '0', 'l' or 'I' or '|' or '!' => '1', 'S' => '5', 'B' => '8', _ => c });
        return sb.ToString();
    }

    private static bool LooksLikeDateWord(string s) => TryDate(s, out _, out _, out _, out _);

    private static bool TryDate(string raw, out int day, out int month, out int? year, out bool repaired)
    {
        day = month = 0;
        year = null;
        repaired = false;
        var s = raw.Trim().TrimEnd(':', ';');
        var m = DateRx.Match(s);
        if (!m.Success && s.Any(char.IsDigit) && s.Any(c => c is '/' or '.' or '-'))
        {
            var fixedText = Lookalike(s);
            m = DateRx.Match(fixedText);
            repaired = m.Success;
        }
        if (!m.Success) return false;
        day = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        month = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        if (day is < 1 or > 31 || month is < 1 or > 12) return false;
        if (m.Groups[3].Success)
        {
            var y = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            year = y < 100 ? 2000 + y : y;
        }
        return true;
    }

    private static int MonthFromName(string w)
    {
        var t = Token(w);
        for (var i = 0; i < Months.Length; i++)
            if (t.Length >= 3 && t.StartsWith(Months[i][..Math.Min(3, Months[i].Length)], StringComparison.Ordinal) &&
                (Months[i].StartsWith(t, StringComparison.Ordinal) || t.StartsWith(Months[i], StringComparison.Ordinal) ||
                 FullMonth(i).StartsWith(t, StringComparison.Ordinal)))
                return i + 1;
        return 0;
    }

    private static string FullMonth(int i) => new[]
        { "janvier", "fevrier", "mars", "avril", "mai", "juin", "juillet", "aout", "septembre", "octobre", "novembre", "decembre" }[i];

    private static readonly Regex AmtRx = new(
        @"^(?<pre>[+\-(]*)(?<int>\d{1,3}(?:[ .'  ]\d{3})+|\d+)(?<sep>[,.])(?<dec>\d{2})(?<post>[)\-+]*)$", RegexOptions.Compiled);

    private static readonly Regex Frag = new(@"^[+\-(]?\d{1,3}$", RegexOptions.Compiled);

    /// <summary>Montant strict : deux décimales obligatoires (un entier isolé n'est jamais un montant).</summary>
    private static bool TryAmount(string raw, out long cents, out bool negative, out bool repaired)
    {
        cents = 0;
        negative = false;
        repaired = false;
        var s = raw.Trim().Replace("€", "").Replace("EUR", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (s.Length == 0) return false;
        var m = AmtRx.Match(s);
        if (!m.Success)
        {
            if (!s.Any(char.IsDigit)) return false;
            m = AmtRx.Match(Lookalike(s));
            if (!m.Success) return false;
            repaired = true;
        }
        var digits = new string(m.Groups["int"].Value.Where(char.IsDigit).ToArray());
        if (digits.Length > 12) return false;
        cents = long.Parse(digits, CultureInfo.InvariantCulture) * 100 + int.Parse(m.Groups["dec"].Value, CultureInfo.InvariantCulture);
        var pre = m.Groups["pre"].Value;
        var post = m.Groups["post"].Value;
        negative = pre.Contains('-') || post.Contains('-') || (pre.Contains('(') && post.Contains(')'));
        return true;
    }

    private static LineInfo Analyze(Line line, Layout? layout, double medianH)
    {
        var info = new LineInfo
        {
            Norm = Norm(string.Join(' ', line.Words.Select(w => w.Text)))
        };
        var words = line.Words;
        var h = Math.Max(medianH, 1);

        // 1. dates contiguës au début de la ligne (opération, éventuellement valeur)
        var i = 0;
        var dates = 0;
        while (i < words.Count && dates < 2)
        {
            var w = words[i];
            if (TryDate(w.Text, out var d, out var mo, out var y, out var rep))
            {
                if (dates == 0) { info.HasDate = true; info.Day = d; info.Month = mo; info.Year = y; info.DateText = w.Text; info.DateRepaired = rep; }
                dates++;
                i++;
                continue;
            }
            if (i + 1 < words.Count && Regex.IsMatch(w.Text, @"^\d{1,2}$") && MonthFromName(words[i + 1].Text) is var mm and > 0)
            {
                var consumed = 2;
                int? yy = null;
                if (i + 2 < words.Count && Regex.IsMatch(words[i + 2].Text, @"^(\d{4}|\d{2})$"))
                {
                    var yv = int.Parse(words[i + 2].Text, CultureInfo.InvariantCulture);
                    yy = yv < 100 ? 2000 + yv : yv;
                    consumed = 3;
                }
                if (dates == 0)
                {
                    info.HasDate = true;
                    info.Day = int.Parse(w.Text, CultureInfo.InvariantCulture);
                    info.Month = mm;
                    info.Year = yy;
                    info.DateText = string.Join(' ', words.Skip(i).Take(consumed).Select(x => x.Text));
                }
                dates++;
                i += consumed;
                continue;
            }
            break;
        }
        if (info.HasDate && (info.Day is < 1 or > 31)) info.HasDate = false;

        // 2. montants (avec fusion « 1 » + « 234,56 ») et mots du libellé
        var rest = words.Skip(i).ToList();
        var labelWords = new List<StatementWord>();
        var amounts = new List<AmountTok>();
        var signs = new List<(double L, double R)>();
        var j = 0;
        while (j < rest.Count)
        {
            var w = rest[j];
            var t = w.Text.Trim();
            if (CurrencyWords.Contains(Token(t))) { j++; continue; }
            if (t is "-" or "–" or "—" or "−") { signs.Add((w.X, w.Right)); j++; continue; }
            if (t is "+") { j++; continue; }

            // Fusion de groupes de milliers : « 1 » « 234,56 » ; « 12 » « 345 » « 678,90 ».
            var k = j;
            var text = t;
            var right = w.Right;
            if (Frag.IsMatch(Lookalike(t)) && Regex.IsMatch(t, @"\d"))
            {
                var tryText = t;
                var m = j + 1;
                while (m < rest.Count && rest[m].X - rest[m - 1].Right < h * 1.2)
                {
                    var nt = rest[m].Text.Trim();
                    if (Regex.IsMatch(nt, @"^\d{3}$")) { tryText += " " + nt; m++; continue; }
                    if (Regex.IsMatch(nt, @"^\d{3}[,.]\d{2}[)\-]?$")) { tryText += " " + nt; m++; k = m - 1; text = tryText; right = rest[k].Right; break; }
                    break;
                }
            }
            if (k > j)
            {
                if (TryAmount(text, out var cents, out var neg, out var rep))
                {
                    amounts.Add(new AmountTok { Cents = cents, Negative = neg, Left = w.X, Right = right, Repaired = rep, Text = text });
                    j = k + 1;
                    continue;
                }
            }
            if (TryAmount(t, out var c1, out var n1, out var r1))
            {
                amounts.Add(new AmountTok { Cents = c1, Negative = n1, Left = w.X, Right = w.Right, Repaired = r1, Text = t });
                j++;
                continue;
            }
            labelWords.Add(w);
            j++;
        }

        // signe isolé collé à un montant (« 12,50 - » ou « - 12,50 »)
        foreach (var s in signs)
        {
            var a = amounts.FirstOrDefault(x => (s.L - x.Right >= -h * 0.3 && s.L - x.Right <= h * 1.5) ||
                                                (x.Left - s.R >= -h * 0.3 && x.Left - s.R <= h * 1.5));
            if (a is not null) a.Negative = true;
        }

        // 3. montants : ceux qui sont dans la zone numérique, rattachés à leur colonne ; les autres sont du libellé
        if (layout is not null)
        {
            var first = layout.FirstNumeric;
            var kept = new List<AmountTok>();
            foreach (var a in amounts)
            {
                if (first is not null && a.Right < first.Left)
                {
                    // « COTISATION 45,00 » : un nombre dans le libellé reste du libellé.
                    labelWords.Add(new StatementWord(a.Text, a.Left, line.Top, a.Right - a.Left, line.Height));
                    continue;
                }
                a.Col = Nearest(layout, a, h);
                kept.Add(a);
            }
            amounts = kept;
        }
        info.Amounts = amounts.OrderBy(a => a.Left).ToList();
        info.AnyRepaired = amounts.Any(a => a.Repaired) || info.DateRepaired;
        info.Label = BankStatementParser.Clean(string.Join(' ', labelWords.OrderBy(w => w.X).Select(w => w.Text)));
        return info;
    }

    private static StatementColumn? Nearest(Layout layout, AmountTok a, double h)
    {
        StatementColumn? best = null;
        var bestScore = double.MaxValue;
        var center = (a.Left + a.Right) / 2;
        foreach (var c in layout.Columns.Where(c => c.IsNumeric))
        {
            var score = Math.Min(Math.Abs(a.Right - c.Right), Math.Min(Math.Abs(a.Left - c.Left), Math.Abs(center - c.Center)));
            if (score < bestScore) { bestScore = score; best = c; }
        }
        return best;
    }

    // ------------------------------------------------------------------ traitement d'une ligne

    private static bool ContainsAny(string norm, IEnumerable<string> phrases) =>
        phrases.Any(p => norm.Contains(p, StringComparison.Ordinal));

    private static void ProcessLine(State st, PageData pd, Line line, Line? prev, Layout? layout, int pageNumber)
    {
        var info = Analyze(line, layout, pd.MedianHeight);
        var norm = info.Norm;
        var p = st.Profile;

        // Soldes de début / de fin. Les expressions ambiguës (« solde au… ») ne comptent qu'à l'intérieur d'un tableau :
        // le résumé imprimé avant le tableau ne définit pas le solde de début.
        var isOpening = ContainsAny(norm, p.OpeningPhrases);
        var isClosing = !isOpening && ContainsAny(norm, p.ClosingPhrases);
        if (!isOpening && !isClosing && layout is not null && ContainsAny(norm, p.AmbiguousBalancePhrases))
        {
            if (st.RowsInSection == 0 && st.Cur?.OpeningCents is null) isOpening = true;
            else isClosing = true;
        }
        if ((isOpening || isClosing) && info.Amounts.Count > 0)
        {
            st.Flush();
            var a = info.Amounts[^1];
            var negative = a.Negative || a.Col?.Kind == StatementColumnKind.Debit || norm.Contains("debiteur", StringComparison.Ordinal);
            var cents = negative ? -a.Cents : a.Cents;
            var sec = st.Section();
            if (isOpening) sec.OpeningCents = cents;
            else
            {
                sec.ClosingCents = cents;
                st.TableEnded = true;
            }
            return;
        }
        if (isOpening || isClosing) { st.Flush(); return; }

        // Totaux des mouvements (contrôle indépendant des soldes).
        if (!info.HasDate && info.Amounts.Count > 0 && Regex.IsMatch(norm, @"^(sous[- ])?total"))
        {
            st.Flush();
            var isCreditTotal = norm.Contains("credit", StringComparison.Ordinal);
            var isDebitTotal = norm.Contains("debit", StringComparison.Ordinal);
            var inSection = st.Cur is not null && (st.Cur.Rows.Count > 0 || st.Cur.OpeningCents is not null);
            foreach (var a in info.Amounts)
            {
                var kind = a.Col?.Kind;
                var asDebit = kind == StatementColumnKind.Debit || (kind is not StatementColumnKind.Credit && isDebitTotal && !isCreditTotal);
                var asCredit = kind == StatementColumnKind.Credit || (kind is not StatementColumnKind.Debit && isCreditTotal && !isDebitTotal);
                if (asDebit == asCredit) continue;
                if (inSection)
                {
                    if (asDebit) st.Section().TotalDebitsCents = a.Cents;
                    else st.Section().TotalCreditsCents = a.Cents;
                }
                else
                {
                    if (asDebit) st.PendingDebits = a.Cents;
                    else st.PendingCredits = a.Cents;
                }
            }
            if (inSection) st.TableEnded = true;
            return;
        }

        if (layout is null) return;   // avant l'en-tête : rien d'autre à lire

        if (info.HasDate)
        {
            st.Flush();
            StartRow(st, pd, line, info, layout, pageNumber);
            return;
        }

        // Ligne sans date : suite du libellé et/ou montant de l'opération en cours.
        if (st.Current is null) return;
        if (ContainsAny(norm, p.NoisePhrases)) { st.Flush(); return; }
        var gap = prev is null ? 0 : line.Top - prev.Bottom;
        var labelCol = layout.Label;
        var xOk = labelCol is null || line.Words[0].X >= labelCol.Left - pd.MedianHeight * 2.5;
        if (st.TableEnded || gap > pd.MedianHeight * 2.0 || !xOk)
        {
            st.Flush();
            return;
        }
        if (info.Label.Length > 0) st.Current.Label = BankStatementParser.Clean(st.Current.Label + " " + info.Label);
        ApplyAmounts(st, st.Current, info);
        st.Current.Bottom = Math.Max(st.Current.Bottom, line.Bottom);
        st.Current.Right = Math.Max(st.Current.Right, line.Words.Max(w => w.Right));
        if (info.AnyRepaired) st.Current.Repaired = true;
    }
    private static void StartRow(State st, PageData pd, Line line, LineInfo info, Layout layout, int pageNumber)
    {
        var row = new StatementRow
        {
            Page = pageNumber,
            DateText = info.DateText,
            Label = info.Label,
            Top = line.Top,
            Bottom = line.Bottom,
            Left = line.Words.Min(w => w.X),
            Right = line.Words.Max(w => w.Right),
            Repaired = info.AnyRepaired
        };
        var date = ResolveDate(st, info, row);
        row.Date = date;
        st.Current = row;
        ApplyAmounts(st, row, info);
    }

    private static DateOnly? ResolveDate(State st, LineInfo info, StatementRow row)
    {
        if (info.Day is not { } d || info.Month is not { } m) return null;
        var year = info.Year;
        if (year is null)
        {
            var start = st.PeriodStart;
            var end = st.PeriodEnd;
            if (start is { } s && end is { } e)
            {
                var from = s.AddDays(-45);
                var to = e.AddDays(45);
                foreach (var y in new[] { s.Year, e.Year, s.Year - 1, e.Year + 1 }.Distinct())
                    if (TryMake(y, m, d, out var cand) && cand >= from && cand <= to)
                    {
                        year = y;
                        break;
                    }
            }
            year ??= st.DefaultYear;
        }
        if (year is null)
        {
            st.YearMissing = true;
            row.Notes.Add("Année introuvable.");
            return null;
        }
        if (!TryMake(year.Value, m, d, out var date))
        {
            row.Notes.Add($"Date impossible : {d:00}/{m:00}/{year}.");
            return null;
        }
        if (info.DateRepaired) row.Notes.Add("Date corrigée (caractères confondus) : à vérifier.");
        return date;
    }

    private static bool TryMake(int y, int m, int d, out DateOnly date)
    {
        date = default;
        if (y is < 1900 or > 2200 || m is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, m)) return false;
        date = new DateOnly(y, m, d);
        return true;
    }

    private static void ApplyAmounts(State st, StatementRow row, LineInfo info)
    {
        var tx = info.Amounts.Where(a => a.Col?.Kind is StatementColumnKind.Debit or StatementColumnKind.Credit or StatementColumnKind.Amount).ToList();
        var bal = info.Amounts.FirstOrDefault(a => a.Col?.Kind == StatementColumnKind.Balance);
        if (bal is not null)
            row.BalanceAfterCents = bal.Negative ? -bal.Cents : bal.Cents;

        if (tx.Count == 0) return;
        if (tx.Count > 1 || row.AmountCents.HasValue)
        {
            row.AmountCents = null;
            row.Notes.Add("Plusieurs montants lus sur cette opération (débit et crédit ensemble ?) : à vérifier sur l'image.");
            return;
        }
        var a = tx[0];
        row.AmountCents = a.Col!.Kind switch
        {
            StatementColumnKind.Debit => -a.Cents,
            StatementColumnKind.Credit => a.Cents,
            _ => a.Negative ? -a.Cents : a.Cents
        };
        if (a.Col.Kind == StatementColumnKind.Amount) { st.AmountColRows++; if (a.Negative) st.AmountColNegative++; }
        if (a.Repaired) row.Repaired = true;
        if (a.Cents == 0) row.Notes.Add("Montant nul.");
    }
}
