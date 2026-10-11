using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ToutCompteFait.Core;
using ToutCompteFait.Core.Security;
using ToutCompteFait.Dialogs;

namespace ToutCompteFait.Views;

/// <summary>Choix proposé dans la colonne « Ventilation » d'une écriture à ventiler.</summary>
public sealed record CategoryOption(long CategoryId, string Display)
{
    public override string ToString() => Display;
}

public sealed class LivreRow
{
    public long Id { get; init; }
    public int Number { get; init; }
    public string DateText { get; init; } = "";
    public string Label { get; init; } = "";
    public bool Pointed { get; init; }
    public string AccountText { get; init; } = "";
    public string InText { get; init; } = "";
    public string OutText { get; init; } = "";
    public string BalanceText { get; init; } = "";
    public string Breakdown { get; init; } = "";

    /// <summary>« 📎 n » quand l'écriture a des justificatifs joints.</summary>
    public string AttachText { get; init; } = "";

    /// <summary>Dépense : « ↩ remboursé x € (reste y €) » ; remboursement : « ↩ de &lt;dépense d'origine&gt; ».</summary>
    public string RefundText { get; init; } = "";

    /// <summary>Ventilation incohérente avec le montant, ou date hors exercice : à corriger (rouge).</summary>
    public bool Problem { get; init; }

    /// <summary>Explication affichée en info-bulle sur la ligne quand elle est à ventiler ou à corriger (sinon null).</summary>
    public string? ProblemTip { get; init; }

    /// <summary>Aucune ventilation : à classer en produit ou en charge (jaune).</summary>
    public bool ToFill { get; init; }

    /// <summary>La liste déroulante d'affectation rapide est proposée (à ventiler + droit d'écrire + exercice ouvert).</summary>
    public bool CanAssign { get; init; }

    public IReadOnlyList<CategoryOption> Options { get; init; } = Array.Empty<CategoryOption>();
    public bool IsTransfer { get; init; }

    /// <summary>Écriture annulée ou écriture d'annulation : conservée, grisée, plus modifiable.</summary>
    public bool IsFrozen { get; init; }
    public required EntryRow Source { get; init; }
}

public sealed class OperationLine
{
    public string Name { get; init; } = "";
    public string ResultText { get; init; } = "";
    public Brush ResultBrush { get; init; } = Brushes.Black;
    public string Detail { get; init; } = "";
}

public sealed class InvertBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public partial class LivreView : UserControl, IPage
{
    private readonly FiscalYear _year;
    private readonly Compta _c = App.Compta;
    private readonly bool _canWrite;
    private DateOnly? _lastDate;
    private bool _loading;
    private List<LivreRow> _all = new();

    public LivreView(FiscalYear year)
    {
        InitializeComponent();
        AllowDrop = true;
        PreviewDragOver += Livre_DragOver;
        PreviewDrop += Livre_Drop;
        _year = year;
        _canWrite = App.Can(Permission.WriteLedger) && year.Open;
        TitleText.Text = $"Livre — exercice {year.Label}";
        AddButton.IsEnabled = _canWrite;
        // L'import du relevé se fait par dates : il reste possible depuis un exercice validé (il alimente les exercices ouverts).
        BankButton.IsEnabled = App.Can(Permission.WriteLedger);
        EditButton.IsEnabled = false;
        DeleteButton.IsEnabled = false;
        ResetButton.IsEnabled = false;
        MergeTransfersButton.IsEnabled = _canWrite;
        if (year.Validated)
            SubText.Text = "Exercice validé : consultation seule (aucune ligne ne peut être ajoutée, modifiée ou retirée). " +
                           "L'import du relevé bancaire reste possible : les lignes vont dans l'exercice correspondant à leur date.";
        else if (App.IsReadOnly)
            SubText.Text = "Poste en lecture seule.";
        else if (!App.HasRole(Permission.WriteLedger))
            SubText.Text = "Votre rôle (" + RoleInfo.DisplayName(App.Session.Role) + ") permet la consultation seule.";
        else
            SubText.Text = "Double-clic sur une ligne pour la modifier. Jaune : à classer en produit ou en charge. Rouge : ventilation à corriger.";

        _loading = true;
        var accs = new List<Account> { new(0, "Tous les comptes", AccountKind.Other, 0, true) };
        accs.AddRange(_c.Accounts(includeInactive: true));
        AccountFilter.ItemsSource = accs;
        AccountFilter.SelectedIndex = 0;
        _loading = false;
        Reload();
    }

    public void Reload()
    {
        _loading = true;
        var keep = (EntriesGrid.SelectedItem as LivreRow)?.Id;
        var rows = _c.Entries(_year.Id, includeCancelled: ShowCancelled.IsChecked == true);
        var catList = _c.Categories(_year.Id);
        var cats = catList.ToDictionary(c => c.Id);
        var accounts = _c.Accounts(includeInactive: true).ToDictionary(a => a.Id);
        var attachCounts = _c.AttachmentCounts(_year.Id);
        var refunded = _c.RefundedAmounts(_year.Id);
        var refundOrigins = _c.RefundOrigins(_year.Id);
        var filter = AccountFilter.SelectedItem as Account;
        var accountFilter = filter is { Id: > 0 } ? filter.Id : (long?)null;

        // Listes d'affectation rapide : charges d'abord pour une sortie, produits d'abord pour une entrée.
        var placeholder = new CategoryOption(0, "Affecter à…");
        var charges = catList.Where(c => c.Kind == CategoryKind.Charge).Select(c => new CategoryOption(c.Id, "Charge · " + c.Name)).ToList();
        var products = catList.Where(c => c.Kind == CategoryKind.Product).Select(c => new CategoryOption(c.Id, "Produit · " + c.Name)).ToList();
        var outOptions = new[] { placeholder }.Concat(charges).Concat(products).ToList();
        var inOptions = new[] { placeholder }.Concat(products).Concat(charges).ToList();

        long running = accountFilter is { } fa
            ? _c.Opening(_year.Id, fa)
            : accounts.Keys.Sum(id => _c.Opening(_year.Id, id));

        var list = new List<LivreRow>();
        var n = 0;
        var problems = 0;
        var toFill = 0;
        foreach (var r in rows)
        {
            n++;
            var e = r.Entry;
            var gap = e.Frozen ? 0 : Compta.Imbalance(e, r.Allocations, cats);
            var needs = Compta.NeedsAllocation(r) && !e.Frozen;
            if (needs) toFill++;
            else if (gap != 0) problems++;

            string AccName(long? id) => id is { } i && accounts.TryGetValue(i, out var a) ? a.Name : "—";
            long inC = 0, outC = 0;
            string accText;
            long effect;
            if (e.Kind == EntryKind.NoCash)
            {
                accText = "(sans mouvement)";
                effect = 0;
                if (accountFilter is not null) continue;
            }
            else if (e.Kind == EntryKind.Transfer)
            {
                accText = $"{AccName(e.AccountId)} → {AccName(e.ToAccountId)}";
                if (accountFilter is { } f)
                {
                    if (e.AccountId == f) { outC = e.AmountCents; effect = -e.AmountCents; accText = "→ " + AccName(e.ToAccountId); }
                    else if (e.ToAccountId == f) { inC = e.AmountCents; effect = e.AmountCents; accText = "← " + AccName(e.AccountId); }
                    else continue;
                }
                else effect = 0;
            }
            else
            {
                accText = AccName(e.AccountId);
                if (accountFilter is { } f && e.AccountId != f) continue;
                if (e.Direction == Direction.In) { inC = e.AmountCents; effect = e.AmountCents; }
                else { outC = e.AmountCents; effect = -e.AmountCents; }
            }
            running += effect;

            var tips = new List<string>();
            if (needs)
                tips.Add($"Aucune ventilation : cette écriture de {Money.Format(Math.Abs(e.NetCash))} € n'est classée dans aucun produit ni aucune charge. " +
                         "Choisissez la catégorie dans la colonne « Ventilation » (ou « Modifier »).");
            else if (gap != 0)
            {
                var absGap = Money.Format(Math.Abs(gap));
                tips.Add(e.Kind == EntryKind.NoCash
                    ? $"Opération sans mouvement : le total des produits et celui des charges doivent être égaux (écart de {absGap} €). Corrigez avec « Modifier »."
                    : gap > 0
                        ? $"La ventilation ne couvre pas tout le montant : il reste {absGap} € à ventiler. Complétez ou ajoutez une ligne avec « Modifier »."
                        : $"La ventilation dépasse le montant de l'écriture de {absGap} € (ou une catégorie est du mauvais type : produit au lieu de charge). Corrigez avec « Modifier ».");
            }
            if (!_year.Contains(e.Date))
                tips.Add($"La date {e.Date:dd/MM/yyyy} est hors de l'exercice ({_year.Start:dd/MM/yyyy} → {_year.End:dd/MM/yyyy}) : corrigez la date.");

            var transferAll = e.Kind == EntryKind.Transfer && accountFilter is null;
            var breakdown = string.Join(" · ", r.Allocations.Select(a =>
                $"{cats[a.CategoryId].Name} {Money.Format(a.AmountCents)}"));
            if (e.Kind == EntryKind.MemberRefund) breakdown = $"Remb. {e.MemberName}" + (breakdown.Length > 0 ? " · " + breakdown : "");
            if (needs) breakdown = "À ventiler";
            else if (gap != 0) breakdown = (breakdown.Length > 0 ? breakdown + " · " : "") + $"⚠ reste {Money.Format(gap)}";
            if (e.Kind == EntryKind.Transfer && breakdown.Length == 0) breakdown = "Virement interne";
            if (e.Cancelled) breakdown = "ANNULÉE" + (breakdown.Length > 0 ? " · " + breakdown : "");
            else if (e.IsReversal) breakdown = "Annulation" + (breakdown.Length > 0 ? " · " + breakdown : "");
            if (e.Frozen)
                tips.Add(e.Cancelled
                    ? $"Écriture n° {e.Seq} annulée par une écriture inverse : conservée pour la piste d'audit, elle n'est plus modifiable."
                    : $"Écriture n° {e.Seq} : annulation d'une autre écriture (même montant, sens inverse). L'ensemble n'a aucun effet sur les comptes.");

            list.Add(new LivreRow
            {
                Id = e.Id, Number = e.Seq > 0 ? (int)e.Seq : n, IsFrozen = e.Frozen, DateText = e.Date.ToString("dd/MM/yyyy"), Label = e.Label,
                Pointed = e.Pointed, AccountText = accText,
                InText = transferAll ? "" : Money.FormatOrBlank(inC),
                OutText = transferAll ? "" : Money.FormatOrBlank(outC),
                BalanceText = Money.Format(running), Breakdown = breakdown,
                AttachText = attachCounts.TryGetValue(e.Id, out var attachN) ? "📎 " + attachN : "",
                RefundText = refundOrigins.TryGetValue(e.Id, out var origin) ? "↩ de " + origin
                    : refunded.TryGetValue(e.Id, out var back)
                        ? back >= e.AmountCents ? $"↩ remboursé en totalité ({Money.Format(back)} €)"
                                                : $"↩ remboursé {Money.Format(back)} € (reste {Money.Format(e.AmountCents - back)} €)"
                        : "",
                Problem = (!needs && gap != 0) || !_year.Contains(e.Date),
                ToFill = needs,
                ProblemTip = tips.Count > 0 ? string.Join("\n", tips) : null,
                CanAssign = needs && _canWrite,
                Options = e.Direction == Direction.In ? inOptions : outOptions,
                IsTransfer = e.Kind == EntryKind.Transfer, Source = r
            });
        }

        _all = list;
        ApplyFilterAndShow(keep);

        // Bandeaux
        if (toFill > 0)
        {
            FillText.Text = $"🟡 {toFill} écriture(s) à classer en produit ou en charge (en jaune)." +
                            (_canWrite ? " Choisissez la catégorie directement dans la colonne « Ventilation »." : "");
            FillBanner.Visibility = Visibility.Visible;
        }
        else FillBanner.Visibility = Visibility.Collapsed;

        var outside = rows.Count(r => !_year.Contains(r.Entry.Date));
        if (problems > 0 || outside > 0)
        {
            var parts = new List<string>();
            if (problems > 0) parts.Add($"{problems} écriture(s) dont la ventilation est incohérente avec le montant");
            if (outside > 0) parts.Add($"{outside} écriture(s) datée(s) hors de l'exercice");
            IssueText.Text = "⚠ À corriger avant de valider l'exercice : " + string.Join(" ; ", parts) + ".";
            IssueBanner.Visibility = Visibility.Visible;
        }
        else IssueBanner.Visibility = Visibility.Collapsed;

        ShowBalancesAndOperations();
        _loading = false;
    }

    private void ApplyFilterAndShow(long? keepId)
    {
        IEnumerable<LivreRow> q = _all;
        var text = SearchBox.Text.Trim();
        if (text.Length > 0)
            q = q.Where(r => r.Label.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                             || r.Breakdown.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                             || r.InText.Contains(text) || r.OutText.Contains(text)
                             || (r.Source.Entry.MemberName?.Contains(text, StringComparison.CurrentCultureIgnoreCase) ?? false));
        if (OnlyIssues.IsChecked == true) q = q.Where(r => r.Problem);
        if (OnlyToFill.IsChecked == true) q = q.Where(r => r.ToFill);
        var shown = q.ToList();
        EntriesGrid.ItemsSource = shown;
        if (keepId is { } id)
        {
            var sel = shown.FirstOrDefault(r => r.Id == id);
            if (sel is not null) { EntriesGrid.SelectedItem = sel; EntriesGrid.ScrollIntoView(sel); }
        }
        else if (shown.Count > 0 && string.IsNullOrEmpty(text) && OnlyIssues.IsChecked != true && OnlyToFill.IsChecked != true)
        {
            EntriesGrid.ScrollIntoView(shown[^1]);
        }
    }

    private void ShowBalancesAndOperations()
    {
        var b = _c.ComputeBilan(_year.Id);
        BalancesPanel.Children.Clear();
        void Add(string label, string value, bool bold = false)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 26, 0) };
            sp.Children.Add(new TextBlock { Text = label + " ", Foreground = (Brush)FindResource("BrushTextSecondary") });
            sp.Children.Add(new TextBlock { Text = value + " €", FontWeight = bold ? FontWeights.Bold : FontWeights.SemiBold });
            BalancesPanel.Children.Add(sp);
        }
        foreach (var a in b.Accounts) Add($"{a.Account.Name} :", Money.Format(a.Closing));
        Add("Trésorerie totale :", Money.Format(b.ClosingTotal), true);
        Add("Résultat de l'exercice :", Money.Format(b.Result), true);

        var okBrush = new SolidColorBrush(Color.FromRgb(0x04, 0x78, 0x57));
        var badBrush = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C));
        OperationsList.ItemsSource = b.Operations.Select(o => new OperationLine
        {
            Name = o.Operation.Name,
            ResultText = (o.Result > 0 ? "+" : "") + Money.Format(o.Result) + " €",
            ResultBrush = o.Result >= 0 ? okBrush : badBrush,
            Detail = $"Produits {Money.Format(o.ProductsTotal)} € − charges {Money.Format(o.ChargesTotal)} €"
        }).ToList();
    }

    // ------------------------------------------------------------------ actions

    private LivreRow? Selected => EntriesGrid.SelectedItem as LivreRow;

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var live = Selected is { IsFrozen: false };
        EditButton.IsEnabled = _canWrite && live;
        DeleteButton.IsEnabled = _canWrite && live;
        ResetButton.IsEnabled = _canWrite && Selected is { IsFrozen: false, Source.Allocations.Count: > 0 };
    }

    private void Grid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_canWrite && Selected is not null && e.OriginalSource is DependencyObject d && !IsInInteractiveCell(d))
            Edit_Click(sender, e);
    }

    private static bool IsInInteractiveCell(DependencyObject d)
    {
        while (d is not null)
        {
            if (d is CheckBox or ComboBox) return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var date = _lastDate ?? _all.LastOrDefault()?.Source.Entry.Date;
        while (true)
        {
            var dlg = new EntryWindow(_year, null, date) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true) break;
            _lastDate = dlg.LastDate;
            date = dlg.LastDate;
            Reload();
            if (!dlg.SaveAndNew) break;
        }
        Reload();
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        var dlg = new EntryWindow(_year, row.Source) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true) Reload();
    }

    private void MergeTransfers_Click(object sender, RoutedEventArgs e)
    {
        var pairs = _c.FindTransferPairs(_year.Id);
        if (pairs.Count == 0) { Ui.Info("Aucun virement en double détecté : aucune paire sortie / entrée de même montant sur deux comptes."); return; }
        var accounts = _c.Accounts(includeInactive: true).ToDictionary(a => a.Id, a => a.Name);
        string Name(long? id) => id is { } i && accounts.TryGetValue(i, out var n) ? n : "?";
        var sample = string.Join("\n", pairs.Take(8).Select(p =>
            $"• {p.Out.Date:dd/MM/yyyy} {Money.Format(p.Out.AmountCents)} € : {Name(p.Out.AccountId)} → {Name(p.In.AccountId)}  « {p.Out.Label} »"));
        if (!Ui.Confirm($"{pairs.Count} virement(s) apparaissent deux fois (une sortie et une entrée) :\n\n{sample}" +
                        (pairs.Count > 8 ? $"\n… et {pairs.Count - 8} autre(s)." : "") +
                        "\n\nLes fusionner en un seul virement entre comptes chacun ? La ventilation éventuelle de ces écritures sera retirée " +
                        "(un virement n'a ni produit ni charge).", "Fusionner les virements"))
            return;
        var merged = 0;
        foreach (var p in pairs)
        {
            if (!Ui.Try(() => _c.MergeIntoTransfer(p.Out.Id, p.In.Id))) break;
            merged++;
        }
        Reload();
        if (merged > 0) Ui.Info($"{merged} virement(s) fusionné(s).");
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row || row.Source.Allocations.Count == 0) return;
        var en = row.Source.Entry;
        if (!Ui.Confirm($"Réinitialiser la ventilation de cette écriture ?\n\n{en.Date:dd/MM/yyyy} — {en.Label}\n{Money.Format(en.AmountCents)} €\n\n" +
                        "Toutes ses lignes de ventilation sont retirées : l'écriture redevient « à ventiler » (en jaune). " +
                        "Le montant, la date et le libellé ne changent pas.", "Réinitialiser la ventilation")) return;
        if (Ui.Try(() => _c.ClearAllocations(en.Id))) Reload();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { IsFrozen: false } row) return;
        var en = row.Source.Entry;
        var reason = PromptWindow.Ask(Window.GetWindow(this), "Annuler l'écriture",
            $"Annuler l'écriture n° {en.Seq} ?\n\n{en.Date:dd/MM/yyyy} — {en.Label}\n{Money.Format(en.AmountCents)} €\n\n" +
            "Une écriture ne se supprime jamais : une écriture inverse est ajoutée (même date, même montant, sens opposé). " +
            "L'original reste dans le livre (case « Annulations ») et les deux n'ont aucun effet sur les comptes.\n\n" +
            "Motif de l'annulation (facultatif) :", "", "Annuler l'écriture");
        if (reason is null) return;
        if (Ui.Try(() => _c.CancelEntry(en.Id, reason))) Reload();
    }

    private void ShowCancelled_Click(object sender, RoutedEventArgs e)
    {
        if (!_loading) Reload();
    }

    // ------------------------------------------------------------------ exports

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ExportButton };
        void Item(string header, Action action)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }
        Item("Livre-journal (PDF)…", () => ExportFile("pdf"));
        Item("Livre-journal (CSV pour tableur)…", () => ExportFile("csv"));
        Item("FEC — Fichier des Écritures Comptables (contrôle fiscal)…", () => ExportFile("fec"));
        menu.Items.Add(new Separator());
        Item("Numéros de compte comptables (FEC)…", () =>
        {
            new AccountNumbersWindow { Owner = Window.GetWindow(this) }.ShowDialog();
        });
        menu.IsOpen = true;
    }

    private void ExportFile(string kind)
    {
        var settings = App.Settings;
        var fileLabel = _year.Label.Replace('/', '-');
        string defaultName;
        string filter;
        switch (kind)
        {
            case "pdf": defaultName = $"Livre-journal {fileLabel}.pdf"; filter = "PDF (*.pdf)|*.pdf"; break;
            case "csv": defaultName = $"Livre-journal {fileLabel}.csv"; filter = "CSV (*.csv)|*.csv"; break;
            default:
                if (string.IsNullOrWhiteSpace(settings.Siren))
                {
                    var siren = PromptWindow.Ask(Window.GetWindow(this), "Export FEC",
                        "Le nom du fichier FEC commence par le numéro SIREN de l'association (9 chiffres). " +
                        "Laissez vide si vous ne l'avez pas : le fichier s'appellera « 000000000FEC… » et pourra être renommé.\n\nSIREN :",
                        "", "Continuer");
                    if (siren is null) return;
                    settings.Siren = new string(siren.Where(char.IsDigit).ToArray());
                    settings.Save();
                }
                defaultName = Compta.FecFileName(settings.Siren, _year.End); filter = "FEC (*.txt)|*.txt"; break;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = defaultName, Filter = filter, Title = "Exporter" };
        if (dlg.ShowDialog() != true) return;
        var ok = Ui.Try(() =>
        {
            switch (kind)
            {
                case "pdf": File.WriteAllBytes(dlg.FileName, _c.ExportLedgerPdf(_year.Id, settings.OrganizationName)); break;
                case "csv": File.WriteAllText(dlg.FileName, _c.ExportLedgerCsv(_year.Id), new System.Text.UTF8Encoding(true)); break;
                default: File.WriteAllText(dlg.FileName, _c.ExportFec(_year.Id), new System.Text.UTF8Encoding(false)); break;
            }
        });
        if (ok) Ui.Info("Export enregistré :\n" + dlg.FileName);
    }

    /// <summary>Un relevé déposé sur le Livre ouvre directement l'import avec ce fichier.</summary>
    private void Livre_DragOver(object sender, DragEventArgs e) =>
        FileDrop.Accept(e, App.Can(Permission.WriteLedger));

    private void Livre_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!App.Can(Permission.WriteLedger)) return;
        var files = FileDrop.Files(e);
        if (files.Length == 0) return;
        OpenBankImport(files);
    }

    private void BankImport_Click(object sender, RoutedEventArgs e) => OpenBankImport(null);

    private void OpenBankImport(string[]? files)
    {
        var dlg = new BankImportWindow { Owner = Window.GetWindow(this) };
        if (files is not null) dlg.LoadFilesWhenShown(files);
        var shown = dlg.ShowDialog() == true;
        if (shown || dlg.Result is not null)   // plusieurs relevés : certains peuvent être importés avant une annulation
        {
            Reload();
            if (dlg.Result is { Created: > 0 }) OnlyToFill.IsChecked = false;
        }
    }

    /// <summary>Affectation rapide : la catégorie choisie reçoit la totalité de l'écriture.</summary>
    private void Assign_DropDownClosed(object? sender, EventArgs e)
    {
        if (sender is not ComboBox { DataContext: LivreRow row } combo) return;
        if (combo.SelectedItem is not CategoryOption { CategoryId: > 0 } choice)
        {
            combo.SelectedIndex = 0;
            return;
        }
        if (Ui.Try(() => _c.AssignCategory(row.Id, choice.CategoryId))) Reload();
        else combo.SelectedIndex = 0;
    }

    private void Pointed_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.DataContext is not LivreRow row) return;
        var wanted = cb.IsChecked == true;
        if (!_canWrite || !Ui.Try(() => _c.SetPointed(row.Id, wanted)))
        {
            cb.IsChecked = row.Pointed;
            return;
        }
        Reload();
    }

    /// <summary>Affiche uniquement les écritures à ventiler (utilisé par le mode diagnostic).</summary>
    public void ShowOnlyToFill()
    {
        OnlyToFill.IsChecked = true;
        ApplyFilterAndShow(null);
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsLoaded && AccountFilter.Items.Count == 0) return;
        Reload();
    }

    private void Filter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || EntriesGrid is null) return;
        ApplyFilterAndShow(null);
    }

    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ApplyFilterAndShow(null);
    }
}

