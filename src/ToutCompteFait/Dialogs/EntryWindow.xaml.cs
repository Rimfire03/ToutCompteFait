using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ToutCompteFait.Core;

namespace ToutCompteFait.Dialogs;

public partial class EntryWindow : Window
{
    private enum Mode { Expense, Income, Transfer, Refund, NoCash, Reimbursed }

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
    private readonly List<AttachItem> _atts = new();
    private readonly List<RefundItem> _refundItems = new();

    private sealed record RefundItem(RefundCandidate? Candidate, string Display)
    {
        public override string ToString() => Display;
    }

    /// <summary>Justificatif de la fenêtre : déjà enregistré (ExistingId) ou à joindre à l'enregistrement (Data).</summary>
    private sealed class AttachItem
    {
        public long? ExistingId { get; init; }
        public required string Name { get; init; }
        public long Size { get; init; }
        public byte[]? Data { get; init; }
        public bool Removed { get; set; }
        public override string ToString() => $"📎  {Name}  ({Size / 1024.0:0.#} Ko)" + (ExistingId is null ? "  — à enregistrer" : "");
    }
    private bool _init = true;

    /// <summary>L'utilisateur a choisi « Enregistrer et saisir une autre ».</summary>
    public bool SaveAndNew { get; private set; }
    public DateOnly LastDate { get; private set; }

    public EntryWindow(FiscalYear year, EntryRow? existing = null, DateOnly? defaultDate = null)
    {
        InitializeComponent();
        // Version sans remboursements aux membres : le type n'est pas proposé (sauf pour une écriture existante de ce type).
        if (!Editions.Current.HasMemberRefunds) ModeRefund.Visibility = Visibility.Collapsed;
        _year = year;
        _existing = existing;
        _accounts = _c.Accounts();
        _cats = _c.Categories(year.Id);

        _refundItems.Add(new RefundItem(null, "— Aucune dépense liée —"));
        foreach (var rc in _c.RefundCandidates(existing?.Entry.RefundOfId, existing?.Entry.Id ?? 0))
            _refundItems.Add(new RefundItem(rc,
                $"{rc.Entry.Date:dd/MM/yyyy} · {rc.Entry.Label} · {Money.Format(rc.Entry.AmountCents)} €" +
                (rc.Refunded > 0 ? $" (déjà remboursé {Money.Format(rc.Refunded)} €)" : "") + $" · {rc.YearLabel}"));
        RefundCombo.ItemsSource = _refundItems;
        RefundCombo.SelectedIndex = 0;
        AccountCombo.ItemsSource = _accounts;
        ToAccountCombo.ItemsSource = _accounts;
        AccountCombo.SelectedIndex = _accounts.Count > 0 ? 0 : -1;
        ToAccountCombo.SelectedIndex = _accounts.Count > 1 ? 1 : -1;

        var date = defaultDate ?? DateOnly.FromDateTime(DateTime.Today);
        if (!year.Contains(date)) date = year.Start;
        DatePick.SelectedDate = date.ToDateTime(TimeOnly.MinValue);
        DatePick.DisplayDateStart = null;

        if (existing is not null)
        {
            LoadExisting(existing);
            foreach (var a in _c.Attachments(existing.Entry.Id))
                _atts.Add(new AttachItem { ExistingId = a.Id, Name = a.FileName, Size = a.SizeBytes });
        }
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
        RefreshAttachList();
        Loaded += (_, _) => (existing is null ? LabelBox : AmountBox).Focus();

        // Glisser-déposer de justificatifs sur la fenêtre (dépenses, exercice ouvert).
        AttachDrop.Visibility = !App.IsReadOnly && _year.Open ? Visibility.Visible : Visibility.Collapsed;
        AttachDrop.Hint = "Images, PDF, documents · 10 Mo maximum par fichier";
        AttachDrop.Browse += (_, _) => AttachAdd_Click(this, new RoutedEventArgs());
        AllowDrop = true;
        PreviewDragOver += (_, e) => FileDrop.Accept(e, CanDropAttachments);
        PreviewDrop += (_, e) =>
        {
            if (CanDropAttachments) AddAttachmentFiles(FileDrop.Files(e));
            e.Handled = true;
        };
    }

    // ------------------------------------------------------------------ mode et affichage

    private Mode CurrentMode =>
        ModeIncome.IsChecked == true ? Mode.Income :
        ModeReimbursed.IsChecked == true ? Mode.Reimbursed :
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
        RefundPanel.Visibility = m == Mode.Reimbursed ? Visibility.Visible : Visibility.Collapsed;
        AllocSection.Visibility = m == Mode.Transfer ? Visibility.Collapsed : Visibility.Visible;
        AccountLabel.Text = m == Mode.Transfer ? "Depuis le compte" : m is Mode.Income or Mode.Reimbursed ? "Compte crédité" : "Compte débité";
        ModeHelp.Text = m switch
        {
            Mode.Expense => "Argent qui sort d'un compte : achat, facture, frais bancaires…",
            Mode.Income => "Argent qui entre sur un compte : adhésion, loto, subvention…",
            Mode.Reimbursed => "Argent remboursé à l'association (fournisseur, assurance…) : encaissé sur un compte, mais déduit de la charge concernée au lieu de compter comme un produit.",
            Mode.Transfer => "Déplacement d'argent entre deux comptes de l'association (banque → livret…) : n'a aucun effet sur le résultat.",
            Mode.Refund => "Remboursement d'un membre de l'équipe qui a payé un achat avec son compte personnel : c'est une dépense, à ventiler sur la charge concernée.",
            _ => "Refacturation interne ou régularisation : aucun argent ne bouge, mais un produit et une charge de même montant doivent être enregistrés."
        };
        AllocHelp.Text = m == Mode.NoCash
            ? "Saisissez le produit ET la charge (montants positifs et égaux : le total des produits doit égaler le total des charges)."
            : "Indiquez où va l'argent. Une seule ligne reprend le montant de l'opération ; avec plusieurs lignes, la dernière reprend automatiquement le reste : le total est toujours égal au montant.";
        AmountLabel.Text = m == Mode.NoCash ? "Montant refacturé (€)" : "Montant (€)";
        // Justificatifs : pour les dépenses (et si l'écriture en a déjà).
        AttachSection.Visibility = m is Mode.Expense or Mode.Refund || _atts.Any(a => !a.Removed)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ remboursement lié à une dépense

    private RefundCandidate? SelectedRefund => (RefundCombo.SelectedItem as RefundItem)?.Candidate;

    private void RefundCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        var rc = SelectedRefund;
        if (rc is null) { RefundInfo.Text = ""; return; }
        RefundInfo.Text = $"Dépense de {Money.Format(rc.Entry.AmountCents)} € ; déjà remboursé {Money.Format(rc.Refunded)} € ; " +
                          $"reste {Money.Format(rc.Remaining)} € (un remboursement partiel est possible).";
        if (_init) return;
        // Préremplissage : montant restant et catégorie de charge de la dépense d'origine.
        if (!Money.TryParse(AmountBox.Text, out var cents) || cents <= 0) AmountBox.Text = Money.Format(rc.Remaining);
        var last = _rows.Count > 0 ? _rows[^1] : null;
        if (_rows.Count == 1 && last is not null && last.Combo.SelectedItem is null)
        {
            var origRow = _c.EntryById(rc.Entry.Id);
            var origAlloc = origRow?.Allocations.FirstOrDefault();
            if (origAlloc is not null)
            {
                var origCat = _c.Categories(rc.Entry.YearId).FirstOrDefault(c => c.Id == origAlloc.CategoryId);
                var target = origCat is null ? null
                    : last.Combo.Items.OfType<CatItem>().FirstOrDefault(i => i.Cat.Kind == origCat.Kind
                        && string.Equals(i.Cat.Name, origCat.Name, StringComparison.CurrentCultureIgnoreCase));
                if (target is not null) last.Combo.SelectedItem = target;
            }
        }
        SyncSingleRow();
        RefreshStatus();
    }

    // ------------------------------------------------------------------ justificatifs

    private void RefreshAttachList()
    {
        var keep = AttachList.SelectedItem;
        AttachList.ItemsSource = null;
        AttachList.ItemsSource = _atts.Where(a => !a.Removed).ToList();
        if (keep is not null && AttachList.Items.Contains(keep)) AttachList.SelectedItem = keep;
        var editable = !App.IsReadOnly && _year.Open;
        AttachDrop.IsEnabled = editable;
        AttachRemoveButton.IsEnabled = editable;
        AttachSection.Visibility = CurrentMode is Mode.Expense or Mode.Refund || _atts.Any(a => !a.Removed)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AttachAdd_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Joindre un justificatif",
            Multiselect = true,
            Filter = "Justificatifs (PDF, images, documents)|*.pdf;*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.tif;*.tiff;*.webp;*.txt;*.doc;*.docx;*.xls;*.xlsx;*.odt;*.ods|Tous les fichiers (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;
        AddAttachmentFiles(dlg.FileNames);
    }

    /// <summary>Joint des fichiers (choisis ou déposés) : contrôle du type et de la taille, réduction éventuelle des images.</summary>
    private void AddAttachmentFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                var info = new System.IO.FileInfo(path);
                var name = Compta.CheckAttachmentFile(info.Name, info.Length);
                var data = System.IO.File.ReadAllBytes(path);
                if (OptimizeBox.IsChecked == true)
                {
                    var opt = ImageOptimizer.Optimize(name, data);
                    if (opt.Optimized) { name = opt.Name; data = opt.Data; }
                }
                _atts.Add(new AttachItem { Name = name, Size = data.Length, Data = data });
            }
            catch (Exception ex) when (ex is ComptaException or System.IO.IOException or UnauthorizedAccessException)
            {
                Ui.Error($"{System.IO.Path.GetFileName(path)} : {ex.Message}");
            }
        }
        RefreshAttachList();
    }

    private void AttachOpen_Click(object sender, RoutedEventArgs e)
    {
        if (AttachList.SelectedItem is not AttachItem item) { if (AttachList.Items.Count == 1) AttachList.SelectedIndex = 0; else return; }
        item = (AttachItem)AttachList.SelectedItem;
        try
        {
            var data = item.Data ?? _c.AttachmentData(item.ExistingId!.Value);
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ToutCompteFait-justificatifs", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            var file = System.IO.Path.Combine(dir, item.Name);
            System.IO.File.WriteAllBytes(file, data);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is ComptaException or System.IO.IOException or System.ComponentModel.Win32Exception
                                       or UnauthorizedAccessException)
        {
            Ui.Error("Impossible d'ouvrir le justificatif : " + ex.Message);
        }
    }

    /// <summary>Enregistre une copie du justificatif sélectionné (ou du seul justificatif) à l'endroit choisi.</summary>
    private void AttachSave_Click(object sender, RoutedEventArgs e)
    {
        if (AttachList.SelectedItem is null && AttachList.Items.Count == 1) AttachList.SelectedIndex = 0;
        if (AttachList.SelectedItem is not AttachItem item)
        {
            Ui.Info("Sélectionnez d'abord un justificatif dans la liste.", "Télécharger");
            return;
        }
        var ext = System.IO.Path.GetExtension(item.Name);
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Enregistrer le justificatif",
            FileName = item.Name,
            Filter = ext.Length > 0 ? $"Fichier {ext}|*{ext}|Tous les fichiers (*.*)|*.*" : "Tous les fichiers (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var data = item.Data ?? _c.AttachmentData(item.ExistingId!.Value);
            System.IO.File.WriteAllBytes(dlg.FileName, data);
        }
        catch (Exception ex) when (ex is ComptaException or System.IO.IOException or UnauthorizedAccessException)
        {
            Ui.Error("Impossible d'enregistrer le justificatif : " + ex.Message);
        }
    }
    private bool CanDropAttachments =>
        !App.IsReadOnly && _year.Open && AttachSection.Visibility == Visibility.Visible;

    private void AttachRemove_Click(object sender, RoutedEventArgs e)
    {
        if (AttachList.SelectedItem is not AttachItem item) return;
        if (item.ExistingId is null) _atts.Remove(item);
        else item.Removed = true;   // retiré de la base à l'enregistrement de l'écriture
        RefreshAttachList();
    }

    // ------------------------------------------------------------------ ventilation

    private IEnumerable<CatItem> ItemsForMode()
    {
        var charges = _cats.Where(c => c.Kind == CategoryKind.Charge).Select(c => new CatItem(c, "Charge · " + c.Name));
        var products = _cats.Where(c => c.Kind == CategoryKind.Product).Select(c => new CatItem(c, "Produit · " + c.Name));
        var m = CurrentMode;
        if (m == Mode.Reimbursed) return charges;
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

    private void ResetAlloc_Click(object sender, RoutedEventArgs e)
    {
        _rows.Clear();
        AllocPanel.Children.Clear();
        AddAllocRow();
        SyncSingleRow();
        RefreshStatus();
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
                var gap = (m is Mode.Income or Mode.Reimbursed ? cents : -cents) - v;
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
        Mode.Income or Mode.Reimbursed => kind == CategoryKind.Product ? 1 : -1,
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
        var dir = m is Mode.Income or Mode.Reimbursed or Mode.NoCash ? Direction.In : Direction.Out;

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
            string.IsNullOrWhiteSpace(NoteBox.Text) ? null : NoteBox.Text.Trim(),
            m == Mode.Reimbursed ? SelectedRefund?.Entry.Id : null);
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
        if (e.FromBank)
        {
            // Importée de la banque : le relevé fait foi, le montant n'est plus modifiable.
            AmountBox.IsReadOnly = true;
            AmountBox.Background = System.Windows.Media.Brushes.WhiteSmoke;
            AmountBox.ToolTip = "Écriture importée de la banque : le montant est celui du relevé et ne peut pas être modifié.";
        }
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
            _ when e.Direction == Direction.In =>
                // Argent encaissé ne touchant que des charges = remboursement reçu (déduit de la charge).
                e.RefundOfId is not null || row.Allocations.Count > 0 && row.Allocations.All(al => _cats.Any(c => c.Id == al.CategoryId && c.Kind == CategoryKind.Charge))
                    ? Mode.Reimbursed : Mode.Income,
            _ => Mode.Expense
        };
        ModeIncome.IsChecked = mode == Mode.Income;
        ModeReimbursed.IsChecked = mode == Mode.Reimbursed;
        ModeExpense.IsChecked = mode == Mode.Expense;
        ModeTransfer.IsChecked = mode == Mode.Transfer;
        ModeRefund.IsChecked = mode == Mode.Refund;
        if (mode == Mode.Refund) ModeRefund.Visibility = Visibility.Visible;
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
        if (e.RefundOfId is { } origId)
            RefundCombo.SelectedItem = _refundItems.FirstOrDefault(i => i.Candidate?.Entry.Id == origId) ?? _refundItems[0];
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
        long savedId;
        try
        {
            savedId = _c.SaveEntry(entry, allocs, strict: true);
        }
        catch (ComptaException ex)
        {
            ErrorText.Text = ex.Message;
            return;
        }
        // Justificatifs : appliqués une fois l'écriture enregistrée (l'écriture l'est déjà : on ne bloque pas la fermeture).
        try
        {
            foreach (var a in _atts.Where(a => a.Removed && a.ExistingId is not null)) _c.RemoveAttachment(a.ExistingId!.Value);
            foreach (var a in _atts.Where(a => a.ExistingId is null && !a.Removed && a.Data is not null)) _c.AddAttachment(savedId, a.Name, a.Data!);
        }
        catch (ComptaException ex)
        {
            Ui.Error("L'écriture est enregistrée, mais un justificatif n'a pas pu être appliqué : " + ex.Message);
        }
        SaveAndNew = andNew;
        LastDate = entry.Date;
        DialogResult = true;
    }
}
