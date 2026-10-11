using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ComptaAepby.Core;

namespace ComptaAepby.Dialogs;

public partial class EntryWindow : Window
{
    private enum Mode { Expense, Income, Transfer, Refund, NoCash }

    private sealed record CatItem(Category Cat, string Display)
    {
        public override string ToString() => Display;
    }

    private sealed class AllocRow
    {
        public required ComboBox Combo { get; init; }
        public required TextBox Amount { get; init; }
        public required Grid Root { get; init; }
        public bool AmountTouched { get; set; }
    }

    private readonly Compta _c = App.Compta;
    private readonly FiscalYear _year;
    private readonly EntryRow? _existing;
    private readonly List<Account> _accounts;
    private readonly List<Category> _cats;
    private readonly List<AllocRow> _rows = new();
    private bool _init = true;

    /// <summary>L'utilisateur a choisi « Enregistrer et saisir une autre ».</summary>
    public bool SaveAndNew { get; private set; }
    public DateOnly LastDate { get; private set; }

    public EntryWindow(FiscalYear year, EntryRow? existing = null, DateOnly? defaultDate = null)
    {
        InitializeComponent();
        _year = year;
        _existing = existing;
        _accounts = _c.Accounts();
        _cats = _c.Categories(year.Id);

        AccountCombo.ItemsSource = _accounts;
        ToAccountCombo.ItemsSource = _accounts;
        AccountCombo.SelectedIndex = _accounts.Count > 0 ? 0 : -1;
        ToAccountCombo.SelectedIndex = _accounts.Count > 1 ? 1 : -1;

        var date = defaultDate ?? DateOnly.FromDateTime(DateTime.Today);
        if (!year.Contains(date)) date = year.Start;
        DatePick.SelectedDate = date.ToDateTime(TimeOnly.MinValue);
        DatePick.DisplayDateStart = null;

        if (existing is not null) LoadExisting(existing);
        else
        {
            AddAllocRow();
            Title = "Nouvelle écriture";
        }
        _init = true;
        ApplyModeVisibility();
        _init = false;
        SyncSingleRow();
        RefreshStatus();
        Loaded += (_, _) => (existing is null ? LabelBox : AmountBox).Focus();
    }

    // ------------------------------------------------------------------ mode et affichage

    private Mode CurrentMode =>
        ModeIncome.IsChecked == true ? Mode.Income :
        ModeTransfer.IsChecked == true ? Mode.Transfer :
        ModeRefund.IsChecked == true ? Mode.Refund :
        ModeNoCash.IsChecked == true ? Mode.NoCash : Mode.Expense;

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded && _init && _existing is null && _rows.Count == 0) return;
        ApplyModeVisibility();
        // Recalcule les catégories proposées pour chaque ligne.
        foreach (var r in _rows)
        {
            var selected = (r.Combo.SelectedItem as CatItem)?.Cat.Id;
            FillCombo(r.Combo);
            if (selected is { } id) r.Combo.SelectedItem = r.Combo.Items.OfType<CatItem>().FirstOrDefault(i => i.Cat.Id == id);
        }
        SyncSingleRow();
        RefreshStatus();
    }

    private void ApplyModeVisibility()
    {
        var m = CurrentMode;
        AccountPanel.Visibility = m == Mode.NoCash ? Visibility.Collapsed : Visibility.Visible;
        ToAccountPanel.Visibility = m == Mode.Transfer ? Visibility.Visible : Visibility.Collapsed;
        MemberPanel.Visibility = m == Mode.Refund ? Visibility.Visible : Visibility.Collapsed;
        AllocSection.Visibility = m == Mode.Transfer ? Visibility.Collapsed : Visibility.Visible;
        AccountLabel.Text = m == Mode.Transfer ? "Depuis le compte" : m == Mode.Income ? "Compte crédité" : "Compte débité";
        ModeHelp.Text = m switch
        {
            Mode.Expense => "Argent qui sort d'un compte : achat, facture, frais bancaires…",
            Mode.Income => "Argent qui entre sur un compte : adhésion, loto, subvention, remboursement reçu…",
            Mode.Transfer => "Déplacement d'argent entre deux comptes de l'association (banque → livret…) : n'a aucun effet sur le résultat.",
            Mode.Refund => "Remboursement d'un membre de l'équipe qui a payé un achat avec son compte personnel : c'est une dépense, à ventiler sur la charge concernée.",
            _ => "Refacturation interne ou régularisation : aucun argent ne bouge, mais un produit et une charge de même montant doivent être enregistrés."
        };
        AllocHelp.Text = m == Mode.NoCash
            ? "Saisissez le produit ET la charge (montants positifs et égaux : le total des produits doit égaler le total des charges)."
            : "Indiquez où va l'argent. Une seule ligne reprend le montant de l'opération ; avec plusieurs lignes, la dernière reprend automatiquement le reste : le total est toujours égal au montant.";
        AmountLabel.Text = m == Mode.NoCash ? "Montant refacturé (€)" : "Montant (€)";
    }

    // ------------------------------------------------------------------ ventilation

    private IEnumerable<CatItem> ItemsForMode()
    {
        var charges = _cats.Where(c => c.Kind == CategoryKind.Charge).Select(c => new CatItem(c, "Charge · " + c.Name));
        var products = _cats.Where(c => c.Kind == CategoryKind.Product).Select(c => new CatItem(c, "Produit · " + c.Name));
        var m = CurrentMode;
        return m is Mode.Income ? products.Concat(charges)
             : m is Mode.NoCash ? products.Concat(charges)
             : charges.Concat(products);
    }

    private void FillCombo(ComboBox combo)
    {
        combo.ItemsSource = ItemsForMode().ToList();
    }

    private AllocRow AddAllocRow(Category? cat = null, long shownCents = 0, bool hasAmount = false)
    {
        var root = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });

        var combo = new ComboBox { IsEditable = false };
        FillCombo(combo);
        if (cat is not null) combo.SelectedItem = combo.Items.OfType<CatItem>().FirstOrDefault(i => i.Cat.Id == cat.Id);
        var amount = new TextBox { HorizontalContentAlignment = HorizontalAlignment.Right };
        if (hasAmount) amount.Text = Money.Format(shownCents);
        Grid.SetColumn(amount, 2);

        var remove = new Button
        {
            Content = "✕", Style = (Style)FindResource("SecondaryButton"), Padding = new Thickness(0, 5, 0, 5),
            Margin = new Thickness(6, 0, 0, 0), ToolTip = "Retirer cette ligne"
        };
        Grid.SetColumn(remove, 3);
        root.Children.Add(combo);
        root.Children.Add(amount);
        root.Children.Add(remove);

        var row = new AllocRow { Combo = combo, Amount = amount, Root = root, AmountTouched = hasAmount };
        combo.SelectionChanged += (_, _) => { SyncSingleRow(); RefreshStatus(); };
        amount.TextChanged += (_, _) =>
        {
            if (amount.IsKeyboardFocused) row.AmountTouched = true;
            if (_rows.Count > 0 && _rows[^1] != row) SyncSingleRow();   // la dernière ligne reprend le reste
            RefreshStatus();
        };
        remove.Click += (_, _) =>
        {
            if (_rows.Count == 1) { combo.SelectedItem = null; amount.Text = ""; row.AmountTouched = false; }
            else { _rows.Remove(row); AllocPanel.Children.Remove(root); }
            SyncSingleRow();
            RefreshStatus();
        };
        _rows.Add(row);
        AllocPanel.Children.Add(root);
        return row;
    }

    private void AddAlloc_Click(object sender, RoutedEventArgs e)
    {
        // Passer à plusieurs lignes : la première garde le montant total jusqu'à ce qu'on le réduise.
        var r = AddAllocRow();
        SyncSingleRow();
        // Propose directement le reste à ventiler.
        if (TryDraft(out var entry, out var allocs, out _) && entry is not null)
        {
            var gap = Compta.Imbalance(entry, allocs, _cats.ToDictionary(c => c.Id));
            if (gap != 0) r.Amount.Text = Money.Format(Math.Abs(gap));
        }
        r.Combo.Focus();
    }

    private void Amount_Changed(object sender, TextChangedEventArgs e)
    {
        if (_init) return;
        SyncSingleRow();
        RefreshStatus();
    }

    /// <summary>Une seule ligne de ventilation : elle est forcément égale au montant de l'opération (suit le montant,
    /// non modifiable). Dès qu'il y a plusieurs lignes, les montants se saisissent librement.</summary>
    private void SyncSingleRow()
    {
        if (_syncing || _rows.Count == 0) return;
        _syncing = true;
        try
        {
            var m = CurrentMode;
            var last = _rows[^1];
            // Opération sans mouvement : produits et charges se saisissent librement (une ligne seule = le montant).
            var forced = _rows.Count == 1 || m != Mode.NoCash;
            foreach (var r in _rows) r.Amount.IsReadOnly = forced && r == last;
            if (!forced || !Money.TryParse(AmountBox.Text, out var cents) || cents <= 0) return;

            string text;
            var cat = (last.Combo.SelectedItem as CatItem)?.Cat;
            if (_rows.Count == 1 && (cat is null || m == Mode.NoCash)) text = Money.Format(cents);
            else if (cat is null) text = "";
            else
            {
                // La dernière ligne reprend le reste : l'ensemble de la ventilation est forcément égal au montant.
                long v = 0;
                foreach (var r in _rows.Take(_rows.Count - 1))
                {
                    if (r.Combo.SelectedItem is not CatItem it || !Money.TryParse(r.Amount.Text, out var shown)) continue;
                    var stored = shown * Factor(m, it.Cat.Kind);
                    v += it.Cat.Kind == CategoryKind.Product ? stored : -stored;
                }
                var gap = (m == Mode.Income ? cents : -cents) - v;
                var storedLast = cat.Kind == CategoryKind.Product ? gap : -gap;
                var shownLast = storedLast * Factor(m, cat.Kind);
                text = shownLast > 0 ? Money.Format(shownLast) : "";
            }
            if (last.Amount.Text != text) last.Amount.Text = text;
        }
        finally
        {
            _syncing = false;
        }
    }

    private bool _syncing;

    /// <summary>Signe appliqué à un montant saisi pour que les produits comptent en plus et les charges en moins
    /// (recette), et inversement pour une dépense.</summary>
    private int Factor(Mode m, CategoryKind kind) => m switch
    {
        Mode.Income => kind == CategoryKind.Product ? 1 : -1,
        Mode.NoCash => 1,
        _ => kind == CategoryKind.Charge ? 1 : -1
    };

    // ------------------------------------------------------------------ construction du brouillon

    private bool TryDraft(out Entry? entry, out List<Allocation> allocs, out string? error)
    {
        entry = null;
        allocs = new List<Allocation>();
        error = null;
        var m = CurrentMode;

        if (!Money.TryParse(AmountBox.Text, out var cents) || cents <= 0)
        {
            error = "Saisissez un montant supérieur à zéro (ex. 1 234,56).";
            return false;
        }
        if (DatePick.SelectedDate is not { } dt) { error = "Choisissez une date."; return false; }
        var date = DateOnly.FromDateTime(dt);

        long? acc = null, to = null;
        if (m != Mode.NoCash)
        {
            if (AccountCombo.SelectedItem is not Account a) { error = "Choisissez le compte."; return false; }
            acc = a.Id;
        }
        if (m == Mode.Transfer)
        {
            if (ToAccountCombo.SelectedItem is not Account t) { error = "Choisissez le compte d'arrivée."; return false; }
            to = t.Id;
        }

        var kind = m switch
        {
            Mode.Transfer => EntryKind.Transfer,
            Mode.Refund => EntryKind.MemberRefund,
            Mode.NoCash => EntryKind.NoCash,
            _ => EntryKind.Normal
        };
        var dir = m == Mode.Income || m == Mode.NoCash ? Direction.In : Direction.Out;

        if (m != Mode.Transfer)
        {
            foreach (var r in _rows)
            {
                var item = r.Combo.SelectedItem as CatItem;
                var text = r.Amount.Text;
                if (item is null && string.IsNullOrWhiteSpace(text)) continue; // ligne vide
                if (item is null) { error = "Une ligne de ventilation n'a pas de catégorie."; return false; }
                if (!Money.TryParse(text, out var shown) || shown == 0)
                {
                    error = $"Montant invalide pour « {item.Cat.Name} ».";
                    return false;
                }
                allocs.Add(new Allocation(item.Cat.Id, shown * Factor(m, item.Cat.Kind)));
            }
        }

        entry = new Entry(_existing?.Entry.Id ?? 0, _year.Id, date, LabelBox.Text.Trim(), PointedBox.IsChecked == true, kind,
            acc, dir, to, cents, m == Mode.Refund ? MemberBox.Text.Trim() : null,
            string.IsNullOrWhiteSpace(NoteBox.Text) ? null : NoteBox.Text.Trim());
        return true;
    }

    private void RefreshStatus()
    {
        if (_init && !IsLoaded && _rows.Count == 0) return;
        if (CurrentMode == Mode.Transfer)
        {
            OperationBox.Visibility = Visibility.Collapsed;
            return;
        }
        var ok = TryDraft(out var entry, out var allocs, out var err);
        if (!ok || entry is null)
        {
            BalanceBox.Background = new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9));
            BalanceText.Foreground = new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69));
            BalanceText.Text = AmountBox.Text.Trim().Length == 0 ? "Saisissez le montant pour ventiler." : (err ?? "");
            OperationBox.Visibility = Visibility.Collapsed;
            return;
        }
        var cats = _cats.ToDictionary(c => c.Id);
        var gap = Compta.Imbalance(entry, allocs, cats);
        if (gap == 0)
        {
            BalanceBox.Background = new SolidColorBrush(Color.FromRgb(0xEC, 0xFD, 0xF5));
            BalanceText.Foreground = new SolidColorBrush(Color.FromRgb(0x04, 0x78, 0x57));
            BalanceText.Text = "✔ Ventilation complète : " + Money.Format(entry.AmountCents) + " €";
        }
        else
        {
            BalanceBox.Background = new SolidColorBrush(Color.FromRgb(0xFE, 0xF2, 0xF2));
            BalanceText.Foreground = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C));
            BalanceText.Text = $"Reste à ventiler : {Money.Format(gap)} €" +
                               (_rows.Count > 0 ? "  (le total des lignes doit être égal au montant)" : "");
        }
        ShowOperationHint(allocs, cats);
    }

    /// <summary>Rentabilité des opérations touchées par les catégories choisies, avec cette écriture comprise.</summary>
    private void ShowOperationHint(List<Allocation> allocs, Dictionary<long, Category> cats)
    {
        var ops = allocs.Select(a => cats[a.CategoryId].OperationId).Where(o => o is not null).Select(o => o!.Value).Distinct().ToList();
        if (ops.Count == 0) { OperationBox.Visibility = Visibility.Collapsed; return; }

        var bilan = _c.ComputeBilan(_year.Id);
        var totals = bilan.Products.Concat(bilan.Charges).Where(l => l.CategoryId is not null)
            .ToDictionary(l => l.CategoryId!.Value, l => l.Total);
        if (_existing is not null)
            foreach (var a in _existing.Allocations) totals[a.CategoryId] = totals.GetValueOrDefault(a.CategoryId) - a.AmountCents;
        foreach (var a in allocs) totals[a.CategoryId] = totals.GetValueOrDefault(a.CategoryId) + a.AmountCents;

        var lines = new List<string>();
        foreach (var opId in ops)
        {
            var op = bilan.Operations.FirstOrDefault(o => o.Operation.Id == opId);
            if (op is null) continue;
            long p = cats.Values.Where(c => c.OperationId == opId && c.Kind == CategoryKind.Product).Sum(c => totals.GetValueOrDefault(c.Id));
            long ch = cats.Values.Where(c => c.OperationId == opId && c.Kind == CategoryKind.Charge).Sum(c => totals.GetValueOrDefault(c.Id));
            // + régularisations (reports) déjà prises en compte par le bilan : elles sont dans Total, donc dans totals.
            lines.Add($"Opération « {op.Operation.Name} » après cette écriture : produits {Money.Format(p)} € − charges {Money.Format(ch)} € = rentabilité {Money.Format(p - ch)} €");
        }
        OperationText.Text = string.Join("\n", lines);
        OperationBox.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ édition d'une écriture existante

    private void LoadExisting(EntryRow row)
    {
        var e = row.Entry;
        Title = "Modifier l'écriture";
        TitleText.Text = "Modifier l'écriture";
        DatePick.SelectedDate = e.Date.ToDateTime(TimeOnly.MinValue);
        LabelBox.Text = e.Label;
        AmountBox.Text = Money.Format(e.AmountCents);
        NoteBox.Text = e.Note ?? "";
        PointedBox.IsChecked = e.Pointed;
        MemberBox.Text = e.MemberName ?? "";

        if (e.AccountId is { } a) AccountCombo.SelectedItem = _accounts.FirstOrDefault(x => x.Id == a);
        if (e.ToAccountId is { } t) ToAccountCombo.SelectedItem = _accounts.FirstOrDefault(x => x.Id == t);

        var mode = e.Kind switch
        {
            EntryKind.Transfer => Mode.Transfer,
            EntryKind.MemberRefund => Mode.Refund,
            EntryKind.NoCash => Mode.NoCash,
            _ => e.Direction == Direction.In ? Mode.Income : Mode.Expense
        };
        ModeIncome.IsChecked = mode == Mode.Income;
        ModeExpense.IsChecked = mode == Mode.Expense;
        ModeTransfer.IsChecked = mode == Mode.Transfer;
        ModeRefund.IsChecked = mode == Mode.Refund;
        ModeNoCash.IsChecked = mode == Mode.NoCash;
        // Modifier le type d'une écriture existante reste possible, mais le choix est présenté d'emblée.

        var cats = _cats.ToDictionary(c => c.Id);
        _rows.Clear();
        AllocPanel.Children.Clear();
        foreach (var al in row.Allocations)
        {
            var cat = cats[al.CategoryId];
            AddAllocRow(cat, al.AmountCents * Factor(mode, cat.Kind), hasAmount: true);
        }
        if (_rows.Count == 0) AddAllocRow();
    }

    // ------------------------------------------------------------------ enregistrement

    private void Save_Click(object sender, RoutedEventArgs e) => Save(false);
    private void SaveNew_Click(object sender, RoutedEventArgs e) => Save(true);

    private void Save(bool andNew)
    {
        ErrorText.Text = "";
        if (string.IsNullOrWhiteSpace(LabelBox.Text)) { ErrorText.Text = "Le libellé est obligatoire."; LabelBox.Focus(); return; }
        if (!TryDraft(out var entry, out var allocs, out var err) || entry is null)
        {
            ErrorText.Text = err ?? "Saisie incomplète.";
            return;
        }
        try
        {
            _c.SaveEntry(entry, allocs, strict: true);
        }
        catch (ComptaException ex)
        {
            ErrorText.Text = ex.Message;
            return;
        }
        SaveAndNew = andNew;
        LastDate = entry.Date;
        DialogResult = true;
    }
}
