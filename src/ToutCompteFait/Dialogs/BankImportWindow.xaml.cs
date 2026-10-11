using System.IO;
using System.Windows;
using System.Windows.Controls;
using ToutCompteFait.Core;
using ToutCompteFait.Core.Import;

namespace ToutCompteFait.Dialogs;

/// <summary>Choix proposé pour une ligne douteuse ; <see cref="Decision"/> null = invite « choisissez ».</summary>
public sealed record DecisionOption(BankDecision? Decision, string Display);

public sealed class BankPreviewRow
{
    public string DateText { get; init; } = "";
    public string Label { get; init; } = "";
    public string AmountText { get; init; } = "";
    public string ActionText { get; init; } = "";
    public string Tone { get; init; } = "";
    public bool NeedsDecision { get; init; }
    public IReadOnlyList<DecisionOption> Options { get; init; } = Array.Empty<DecisionOption>();
    public DecisionOption? Selected { get; set; }
}

public partial class BankImportWindow : Window
{
    private sealed record ColumnItem(int Index, string Text)
    {
        public override string ToString() => Text;
    }

    private static readonly ColumnItem None = new(-1, "— aucune —");

    private readonly Compta _c = App.Compta;
    private BankCsvTable? _table;
    private List<BankLine> _lines = new();
    private BankImportPlan? _plan;
    private bool _loading;
    private string? _statementText;       // texte du fichier (CSV / OFX) : source des soldes du relevé
    private StatementPrecheck? _precheck;
    private bool _wasFirstImport;

    /// <summary>Nombre de lignes créées sans ventilation (pour informer l'appelant).</summary>
    public BankImportResult? Result { get; private set; }

    public BankImportWindow()
    {
        InitializeComponent();
        var accounts = _c.Accounts();
        AccountCombo.ItemsSource = accounts;
        AccountCombo.SelectedItem = accounts.FirstOrDefault(a => a.Kind == AccountKind.Bank) ?? accounts.FirstOrDefault();

        // Glisser-déposer d'un relevé (CSV, OFX, PDF ou image) depuis l'Explorateur.
        AllowDrop = true;
        DragOver += (_, e) => FileDrop.Accept(e);
        Drop += (_, e) =>
        {
            var files = FileDrop.Files(e);
            if (files.Length > 0) LoadFiles(files);
            e.Handled = true;
        };
    }

    /// <summary>Charge un ou plusieurs fichiers de relevé une fois la fenêtre affichée (utilisé quand les fichiers ont été
    /// déposés sur le Livre).</summary>
    public void LoadFilesWhenShown(string[] files)
    {
        void Handler(object? s, EventArgs e)
        {
            ContentRendered -= Handler;
            LoadFiles(files);
        }
        ContentRendered += Handler;
    }

    /// <summary>Un CSV / OFX seul, ou un ou plusieurs relevés imprimés (PDF, images).</summary>
    public void LoadFiles(string[] files)
    {
        if (files.Length > 1)
        {
            if (!files.All(Services.StatementDocumentReader.IsSupported))
            {
                Ui.Error("Plusieurs fichiers : seuls des relevés PDF ou images peuvent être importés ensemble.\nPour un CSV ou un OFX, choisissez un seul fichier.");
                return;
            }
            LoadFromPrinted(files);
            return;
        }
        LoadFromFile(files[0]);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choisir le relevé téléchargé depuis la banque",
            Filter = "Relevés bancaires (*.csv;*.ofx;*.qfx;*.txt;*.pdf;*.png;*.jpg;*.jpeg;*.tif;*.tiff)|*.csv;*.ofx;*.qfx;*.txt;*.pdf;*.png;*.jpg;*.jpeg;*.tif;*.tiff|" +
                     "Fichiers CSV / OFX (*.csv;*.ofx;*.qfx;*.txt)|*.csv;*.ofx;*.qfx;*.txt|" +
                     "Relevés imprimés PDF ou images (*.pdf;*.png;*.jpg;*.jpeg;*.tif;*.tiff)|*.pdf;*.png;*.jpg;*.jpeg;*.tif;*.tiff|Tous les fichiers (*.*)|*.*"
        };
        dlg.Multiselect = true;
        if (dlg.ShowDialog() != true) return;
        LoadFiles(dlg.FileNames);
    }

    /// <summary>Lit un relevé (CSV ou OFX) et calcule l'aperçu.</summary>
    public void LoadFromFile(string path)
    {
        // PDF ou image : lecture (texte numérique ou OCR) avec écran de vérification obligatoire.
        if (Services.StatementDocumentReader.IsSupported(path))
        {
            LoadFromPrinted(new[] { path });
            return;
        }
        try
        {
            var text = BankStatementParser.ReadText(path);
            _statementText = text;
            FileText.Text = path;
            var detectNote = DetectAccount(text);
            if (BankStatementParser.LooksLikeOfx(text))
            {
                _table = null;
                MappingBox.Visibility = Visibility.Collapsed;
                var warns = new List<string>();
                _lines = BankStatementParser.ParseOfx(text, warns);
                WarningText.Text = JoinNotes(detectNote, warns.Count > 0 ? string.Join("\n", warns.Take(5)) : "");
            }
            else
            {
                _table = BankStatementParser.ReadCsv(text);
                MappingBox.Visibility = Visibility.Visible;
                FillColumnChoices(BankStatementParser.DetectMapping(_table));
                _lines = ParseCurrentMapping(out var warnText);
                WarningText.Text = JoinNotes(detectNote, warnText);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Error("Fichier illisible :\n" + ex.Message);
            return;
        }
        Recompute();
    }

    private static string JoinNotes(string a, string b) =>
        a.Length == 0 ? b : b.Length == 0 ? a : a + "\n" + b;

    /// <summary>Reconnaît le compte du relevé d'après les identifiants saisis sur les comptes (en-tête du fichier) et le
    /// sélectionne. Renvoie une remarque à afficher (vide si rien à signaler).</summary>
    private string DetectAccount(string text)
    {
        if (!_c.HasStatementIdentifiers()) return "";
        var found = _c.MatchStatementAccounts(text);
        if (found.Count == 1)
        {
            var wasLoading = _loading;
            _loading = true;
            AccountCombo.SelectedItem = (AccountCombo.ItemsSource as IEnumerable<Account>)?.FirstOrDefault(a => a.Id == found[0].Account.Id)
                                        ?? AccountCombo.SelectedItem;
            _loading = wasLoading;
            return $"Compte reconnu d'après le relevé : {found[0].Account.Name} (« {found[0].Keyword} »).";
        }
        return found.Count > 1
            ? "Plusieurs comptes correspondent à ce relevé (" + string.Join(", ", found.Select(f => f.Account.Name)) + ") : choisissez le compte."
            : "Aucun compte reconnu d'après l'en-tête du relevé : vérifiez le compte choisi ci-dessus.";
    }

    /// <summary>Relevés imprimés (PDF numérique ou scan) : lecture, contrôle des soldes et correction éventuelle dans
    /// un écran dédié ; seules les opérations vérifiées sont renvoyées pour le rapprochement habituel.</summary>
    private void LoadFromPrinted(IReadOnlyList<string> paths)
    {
        var ocr = new StatementOcrWindow(paths) { Owner = this };
        if (ocr.ShowDialog() != true || ocr.Lines.Count == 0) return;
        _table = null;
        _statementText = null;
        MappingBox.Visibility = Visibility.Collapsed;
        _lines = ocr.Lines;
        FileText.Text = paths.Count == 1 ? paths[0] : $"{paths.Count} relevés PDF/images";
        WarningText.Text = "Relevé(s) imprimé(s) lu(s) : les montants ont été vérifiés par le contrôle des soldes à l'écran précédent.";
        Recompute();
    }

    private void FillColumnChoices(BankCsvMapping? m)
    {
        _loading = true;
        var width = _table!.ColumnCount;
        var headerRow = m is { FirstDataRow: > 0 } ? _table.Rows[Math.Min(m.FirstDataRow - 1, _table.Rows.Count - 1)] : null;
        var sampleRow = _table.Rows.Skip(m?.FirstDataRow ?? 0).FirstOrDefault() ?? Array.Empty<string>();
        var items = new List<ColumnItem>();
        for (var i = 0; i < width; i++)
        {
            var head = headerRow is not null && i < headerRow.Length && headerRow[i].Length > 0 ? headerRow[i] : $"Colonne {i + 1}";
            var sample = i < sampleRow.Length ? sampleRow[i] : "";
            if (sample.Length > 22) sample = sample[..22] + "…";
            items.Add(new ColumnItem(i, $"{head}  ({sample})"));
        }
        var optional = new List<ColumnItem> { None };
        optional.AddRange(items);

        DateCol.ItemsSource = items;
        LabelCol.ItemsSource = items;
        AmountCol.ItemsSource = optional;
        DebitCol.ItemsSource = optional;
        CreditCol.ItemsSource = optional;

        ColumnItem? Pick(IReadOnlyList<ColumnItem> list, int? idx) =>
            idx is { } i ? list.FirstOrDefault(c => c.Index == i) : null;
        DateCol.SelectedItem = Pick(items, m?.DateColumn) ?? items.FirstOrDefault();
        LabelCol.SelectedItem = Pick(items, m?.LabelColumn) ?? items.Skip(1).FirstOrDefault();
        AmountCol.SelectedItem = Pick(optional, m?.AmountColumn) ?? None;
        DebitCol.SelectedItem = Pick(optional, m?.DebitColumn) ?? None;
        CreditCol.SelectedItem = Pick(optional, m?.CreditColumn) ?? None;
        _firstDataRow = m?.FirstDataRow ?? 0;
        _loading = false;
    }

    private int _firstDataRow;

    private List<BankLine> ParseCurrentMapping(out string warningText)
    {
        warningText = "";
        if (_table is null || DateCol.SelectedItem is not ColumnItem d || LabelCol.SelectedItem is not ColumnItem l)
            return new List<BankLine>();
        int? Opt(ComboBox c) => c.SelectedItem is ColumnItem { Index: >= 0 } ci ? ci.Index : null;
        var amount = Opt(AmountCol);
        var debit = Opt(DebitCol);
        var credit = Opt(CreditCol);
        if (amount is null && (debit is null || credit is null))
        {
            warningText = "Choisissez la colonne « Montant », ou les deux colonnes « Débit » et « Crédit ».";
            return new List<BankLine>();
        }
        var mapping = new BankCsvMapping(d.Index, l.Index, amount, amount is null ? debit : null, amount is null ? credit : null, _firstDataRow);
        var warns = new List<string>();
        var lines = BankStatementParser.ParseCsv(_table, mapping, warns);
        if (warns.Count > 0) warningText = string.Join("\n", warns.Take(4)) + (warns.Count > 4 ? $"\n… et {warns.Count - 4} autre(s) remarque(s)." : "");
        return lines;
    }

    private void Settings_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        if (_table is not null && sender != AccountCombo)
        {
            _lines = ParseCurrentMapping(out var w);
            WarningText.Text = w;
        }
        Recompute();
    }

    private void Recompute()
    {
        _plan = null;
        ImportButton.IsEnabled = false;
        if (AccountCombo.SelectedItem is not Account acc || _lines.Count == 0)
        {
            PreviewGrid.ItemsSource = null;
            SummaryText.Text = _lines.Count == 0 && FileText.Text.Length > 0 ? "Aucune opération lue dans ce fichier." : "";
            return;
        }
        try
        {
            _plan = _c.PlanBankImport(acc.Id, _lines);
            if (OfferNextYear(_plan)) _plan = _c.PlanBankImport(acc.Id, _lines);
        }
        catch (ComptaException ex)
        {
            Ui.Error(ex.Message);
            return;
        }
        _precheck = null;
        if (_statementText is not null)
        {
            var balances = BankStatementParser.ExtractBalances(_statementText, _lines);
            _wasFirstImport = _c.IsFirstImport(acc.Id);
            _precheck = _c.PrecheckStatement(acc.Id, _lines, balances);
        }
        _rows = _plan.Lines.Select(p => new BankPreviewRow
        {
            DateText = p.Line.Date.ToString("dd/MM/yyyy"),
            Label = p.Line.Label,
            AmountText = Money.Format(p.Line.AmountCents),
            ActionText = p.Status switch
            {
                BankLineStatus.New => "➕ Nouvelle écriture, à ventiler",
                BankLineStatus.LabelUpdated => $"✎ Intitulé mis à jour (avant : « {p.OldLabel} »)",
                BankLineStatus.Unchanged => "= Déjà à jour",
                BankLineStatus.YearValidated => "🔒 Exercice validé : ignorée",
                BankLineStatus.ToConfirm => "❓ À valider",
                _ => "⚠ Date hors de tout exercice : ignorée"
            },
            Tone = p.Status switch
            {
                BankLineStatus.New => "New",
                BankLineStatus.LabelUpdated => "Update",
                BankLineStatus.ToConfirm => "Doubt",
                _ => p.Status == BankLineStatus.Unchanged ? "" : "Skip"
            },
            NeedsDecision = p.Status == BankLineStatus.ToConfirm,
            Options = p.Status == BankLineStatus.ToConfirm ? BuildOptions(p) : Array.Empty<DecisionOption>()
        }).ToList();
        foreach (var r in _rows.Where(r => r.NeedsDecision))
        {
            // Une seule contrepartie sur un autre compte et aucun autre candidat : proposée d'office (modifiable).
            var only = r.Options.Where(o => o.Decision is not null && o.Decision.Kind is BankDecisionKind.Match or BankDecisionKind.Transfer).ToList();
            r.Selected = only.Count == 1 && only[0].Decision!.Kind == BankDecisionKind.Transfer ? only[0] : r.Options[0];
        }
        PreviewGrid.ItemsSource = _rows;
        AlignBox.Visibility = _rows.Any(r => r.NeedsDecision) ? Visibility.Visible : Visibility.Collapsed;

        UpdateSummary();
    }

    private List<BankPreviewRow> _rows = new();
    private StatementBalanceReport? _balanceReport;
    private bool _offeredNextYear;

    /// <summary>Des lignes sont datées après la fin du dernier exercice (typiquement : l'exercice vient d'être clôturé
    /// et le suivant n'existe pas encore) : propose de créer l'exercice suivant. Vrai s'il a été créé.</summary>
    private bool OfferNextYear(BankImportPlan plan)
    {
        if (_offeredNextYear || !App.Can(ToutCompteFait.Core.Security.Permission.ManageStructure)) return false;
        var last = _c.Years().OrderBy(y => y.End).LastOrDefault();
        if (last is null) return false;
        var after = plan.Lines.Where(l => l.Status == BankLineStatus.NoYear && l.Line.Date > last.End).ToList();
        if (after.Count == 0) return false;
        _offeredNextYear = true;
        var start = last.End.AddDays(1);
        var end = start.AddYears(1).AddDays(-1);
        if (!Ui.Confirm($"{after.Count} opération(s) du relevé sont datées après la fin du dernier exercice ({last.Label}, " +
                        $"fin le {last.End:dd/MM/yyyy}), et aucun exercice ne les accueille.\n\n" +
                        $"Créer l'exercice suivant, du {start:dd/MM/yyyy} au {end:dd/MM/yyyy}, pour les recevoir ?",
                "Exercice suivant"))
            return false;
        _c.CreateYear(start, end, cloneStructure: true);
        _c.SyncCarry(last.Id);
        return true;
    }

    private static IReadOnlyList<DecisionOption> BuildOptions(BankLinePlan p)
    {
        var list = new List<DecisionOption> { new(null, "❓ Choisissez…") };
        foreach (var c in p.Candidates!)
        {
            if (c.IsTransfer)
                list.Add(new DecisionOption(new BankDecision(BankDecisionKind.Transfer, c.EntryId),
                    $"↔ Virement entre comptes : même opération que l'écriture du {c.Date:dd/MM/yyyy} « {Short(c.Label)} » " +
                    $"de l'autre compte ({c.DaysApart} j d'écart" + (c.Allocated ? ", sa ventilation sera retirée" : "") + ")"));
            else
                list.Add(new DecisionOption(new BankDecision(BankDecisionKind.Match, c.EntryId),
                    $"Rapprocher de l'écriture du {c.Date:dd/MM/yyyy} « {Short(c.Label)} » ({c.DaysApart} j d'écart" +
                    (c.Allocated ? "" : ", non ventilée") + ")"));
        }
        list.Add(new DecisionOption(new BankDecision(BankDecisionKind.CreateNew), "Créer une nouvelle écriture (à ventiler)"));
        list.Add(new DecisionOption(new BankDecision(BankDecisionKind.Skip), "Ignorer cette ligne"));
        return list;
    }

    private static string Short(string s) => s.Length > 34 ? s[..34] + "…" : s;

    private void Decision_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (_plan is null) return;
        var undecided = _rows.Count(r => r.NeedsDecision && r.Selected?.Decision is null);
        var creates = _plan.Count(BankLineStatus.New) + _rows.Count(r => r.NeedsDecision && r.Selected?.Decision?.Kind == BankDecisionKind.CreateNew);
        var matches = _rows.Count(r => r.NeedsDecision && r.Selected?.Decision?.Kind is BankDecisionKind.Match or BankDecisionKind.Transfer);
        var u = _plan.Count(BankLineStatus.LabelUpdated);
        var same = _plan.Count(BankLineStatus.Unchanged);
        var skipped = _plan.Count(BankLineStatus.NoYear) + _plan.Count(BankLineStatus.YearValidated)
                      + _rows.Count(r => r.NeedsDecision && r.Selected?.Decision?.Kind == BankDecisionKind.Skip);
        SummaryText.Text = $"{_plan.Lines.Count} opération(s) : {creates} nouvelle(s) à ventiler · {u + matches} rapprochée(s) (intitulé mis à jour) · " +
                           $"{same} déjà à jour · {skipped} ignorée(s)" + (undecided > 0 ? $" · ❓ {undecided} à valider" : "") + "." +
                           (_precheck is { Note.Length: > 0 } pc ? "\n" + pc.Note : "");
        ImportButton.IsEnabled = undecided == 0 && (creates + u + matches > 0 || (PointBox.IsChecked == true && same > 0));
        ImportButton.ToolTip = undecided > 0 ? "Validez d'abord les lignes en orange (correspondance incertaine)." : null;
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_plan is null) return;
        try
        {
            var decisions = new Dictionary<int, BankDecision>();
            for (var i = 0; i < _rows.Count; i++)
                if (_rows[i].NeedsDecision && _rows[i].Selected?.Decision is { } d) decisions[i] = d;
            if (_precheck is { Inconsistent: true } && !Ui.Confirm(
                    "Le contrôle des soldes signale une incohérence :\n\n" + _precheck.Note + "\n\nImporter quand même ?", "Contrôle des soldes"))
                return;
            Result = _c.ApplyBankImport(_plan, PointBox.IsChecked == true, decisions, AlignBox.IsChecked == true);
            if (_statementText is not null && AccountCombo.SelectedItem is Account acc)
                _balanceReport = _c.FinishStatementImport(acc.Id, _lines,
                    BankStatementParser.ExtractBalances(_statementText, _lines), _wasFirstImport);
        }
        catch (ComptaException ex)
        {
            Ui.Error(ex.Message);
            return;
        }
        Ui.Info($"Import terminé : {Result.Created} écriture(s) créée(s) à ventiler, {Result.LabelsUpdated} intitulé(s) mis à jour" +
                (Result.TransfersLinked > 0 ? $", {Result.TransfersLinked} virement(s) entre comptes reconnu(s) (une seule écriture)" : "") +
                (Result.Skipped > 0 ? $", {Result.Skipped} ignorée(s)" : "") + ".\n\n" +
                (Result.Created > 0 ? "Les nouvelles lignes sont en jaune dans le Livre : choisissez leur produit ou leur charge directement dans la liste." : "") +
                (_balanceReport is { Text.Length: > 0 } br ? "\n\n" + br.Text : ""),
            "Relevé importé");
        DialogResult = true;
    }
}


