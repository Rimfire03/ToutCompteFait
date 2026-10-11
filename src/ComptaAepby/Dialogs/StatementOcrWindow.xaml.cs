using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ComptaAepby.Core;
using ComptaAepby.Core.Import;
using ComptaAepby.Services;

namespace ComptaAepby.Dialogs;

/// <summary>Un relevé (fichier) pour un compte : ses opérations, ses soldes et le document d'origine (image de contrôle).</summary>
public sealed class OcrSegment
{
    public string Source { get; init; } = "";
    public string FilePath { get; init; } = "";
    public ReadDocument Doc { get; init; } = null!;
    public StatementReadResult Result { get; init; } = null!;
    public long? Opening { get; set; }
    public long? Closing { get; set; }
    public List<OcrRowVm> Rows { get; set; } = new();
    public DateOnly SortKey { get; init; }
}

/// <summary>Ligne modifiable : toute saisie de l'utilisateur vaut vérification de sa part (le signalement disparaît) et
/// relance le contrôle arithmétique.</summary>
public sealed class OcrRowVm : INotifyPropertyChanged
{
    private readonly StatementOcrWindow _owner;
    public StatementRow Row { get; }
    public OcrSegment Segment { get; }
    public string Source => Segment.Source;

    public OcrRowVm(StatementRow row, OcrSegment segment, StatementOcrWindow owner)
    {
        Row = row;
        Segment = segment;
        _owner = owner;
        _dateText = row.Date is { } d ? d.ToString("dd/MM/yyyy") : row.DateText;
        _amountText = row.AmountCents is { } a ? Money.Format(a) : "";
    }

    private string _dateText;
    private string _amountText;

    public int Page => Row.Page;

    public bool Include
    {
        get => Row.Include;
        set
        {
            Row.Include = value;
            Changed(nameof(Include), nameof(Tone), nameof(Alert));
            _owner.Recheck();
        }
    }

    public string DateText
    {
        get => _dateText;
        set
        {
            _dateText = value?.Trim() ?? "";
            Row.Date = StatementChecker.TryParseUserDate(_dateText, _owner.EffectiveYear, out var d) ? d : null;
            if (Row.Date is { } ok) _dateText = ok.ToString("dd/MM/yyyy");
            Verified();
        }
    }

    public string Label
    {
        get => Row.Label;
        set
        {
            Row.Label = BankStatementParser.Clean(value ?? "");
            Verified();
        }
    }

    public string AmountText
    {
        get => _amountText;
        set
        {
            _amountText = value?.Trim() ?? "";
            Row.AmountCents = StatementChecker.TryParseUserAmount(_amountText, out var c) && c != 0 ? c : null;
            if (Row.AmountCents is { } a) _amountText = Money.Format(a);
            Verified();
        }
    }

    public string BalanceText => Row.BalanceAfterCents is { } b ? Money.Format(b) : "";

    public string Alert
    {
        get
        {
            if (!Row.Include) return "Exclue de l'import";
            var parts = new List<string>();
            if (!Row.IsComplete) parts.Add(Row.Date is null ? "date à corriger" : "montant à corriger");
            if (Row.Repaired) parts.Add("caractères corrigés : à vérifier");
            parts.AddRange(Row.Notes);
            return string.Join(" · ", parts);
        }
    }

    public string Tone => !Row.Include ? "Off" : !Row.IsComplete ? "Bad" : Row.NeedsAttention ? "Warn" : "";

    private void Verified()
    {
        // L'utilisateur a regardé et saisi la valeur : le signalement de lecture ne s'applique plus.
        Row.Repaired = false;
        Row.Notes.Clear();
        Changed(nameof(DateText), nameof(Label), nameof(AmountText), nameof(Tone), nameof(Alert));
        _owner.Recheck();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed(params string[] names)
    {
        foreach (var n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public void Refresh() => Changed(nameof(Tone), nameof(Alert));
}

public partial class StatementOcrWindow : Window
{
    private sealed record AccountItem(string Key, string Display);

    private readonly List<(string Path, ReadDocument Doc)> _docs = new();
    private List<OcrSegment> _segments = new();
    private List<OcrSegment> _selected = new();
    private List<OcrRowVm> _rows = new();
    private readonly List<StatementProfile> _profiles = new();
    private int _page;
    private OcrSegment? _pageSegment;
    private double _zoom = 720;
    private bool _loading;
    private CancellationTokenSource? _cts;

    /// <summary>Opérations retenues du compte choisi, prêtes pour le rapprochement avec le livre.</summary>
    public List<BankLine> Lines { get; private set; } = new();
    public string? SourcePath { get; private set; }

    /// <summary>Texte d'état affiché (diagnostic).</summary>
    public string StatusTitle => CheckTitle.Text;
    public string StatusDetails => CheckDetails.Text;
    public bool CanUse => UseButton.IsEnabled;

    public StatementOcrWindow(IReadOnlyList<string>? paths = null)
    {
        InitializeComponent();
        _profiles.AddRange(StatementProfiles.BuiltIn());
        _profiles.AddRange(StatementProfiles.LoadCustom(Path.Combine(AppContext.BaseDirectory, "profils-releves")));
        ProfileCombo.ItemsSource = _profiles;
        ProfileCombo.SelectedIndex = 1;   // profil Caisse d'Épargne (mots-clés adaptés) par défaut
        Loaded += async (_, _) =>
        {
            if (paths is { Count: > 0 }) await LoadAsync(paths);
        };
        Closed += (_, _) => _cts?.Cancel();
    }

    public int? EffectiveYear
    {
        get
        {
            if (int.TryParse(YearBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y is >= 2000 and <= 2100) return y;
            var r = _selected.FirstOrDefault()?.Result;
            return r?.PeriodEnd?.Year ?? r?.PeriodStart?.Year;
        }
    }

    // ------------------------------------------------------------------ chargement

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choisir un ou plusieurs relevés (PDF ou image)",
            Multiselect = true,
            Filter = "Relevés imprimés (*.pdf;*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp)|*.pdf;*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp|Tous les fichiers (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        await LoadAsync(dlg.FileNames);
    }

    private async Task LoadAsync(IReadOnlyList<string> paths)
    {
        var bad = paths.FirstOrDefault(p => !StatementDocumentReader.IsSupported(p));
        if (bad is not null)
        {
            Ui.Error("Format non pris en charge : " + Path.GetFileName(bad) + "\nChoisissez des PDF ou des images (PNG, JPEG, TIFF, BMP).");
            return;
        }
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        BrowseButton.IsEnabled = false;
        UseButton.IsEnabled = false;
        FileText.Text = paths.Count == 1 ? paths[0] : $"{paths.Count} relevés";
        CheckTitle.Text = "Lecture en cours…";
        CheckDetails.Text = "";
        try
        {
            _docs.Clear();
            var i = 0;
            foreach (var path in paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                i++;
                var name = Path.GetFileName(path);
                var progress = new Progress<string>(s => CheckDetails.Text = $"Relevé {i} / {paths.Count} ({name}) : {s}");
                var doc = await StatementDocumentReader.ReadAsync(path, progress, _cts.Token);
                _docs.Add((path, doc));
            }
            SourcePath = paths[0];
            MethodText.Text = "Lecture : " + string.Join(", ", _docs.Select(d => d.Doc.Method).Distinct());
            Reparse();
            _page = 0;
            ShowPage();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (OcrUnavailableException ex)
        {
            Ui.Error(ex.Message, "OCR indisponible");
            CheckTitle.Text = "Lecture impossible.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException or NotSupportedException)
        {
            Ui.Error("Le fichier n'a pas pu être lu :\n" + ex.Message);
            CheckTitle.Text = "Lecture impossible.";
        }
        finally
        {
            BrowseButton.IsEnabled = true;
        }
    }

    private static string ShortSource(string path)
    {
        var n = Path.GetFileNameWithoutExtension(path);
        // « 2025_10_31_Releve comptes pro … » → « 2025-10 »
        var m = System.Text.RegularExpressions.Regex.Match(n, @"^(\d{4})[_\-](\d{2})");
        return m.Success ? $"{m.Groups[1].Value}-{m.Groups[2].Value}" : (n.Length > 10 ? n[..10] : n);
    }

    private void Reparse()
    {
        if (_docs.Count == 0 || ProfileCombo.SelectedItem is not StatementProfile profile) return;
        int? year = int.TryParse(YearBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var y) ? y : null;
        var segs = new List<OcrSegment>();
        foreach (var (path, doc) in _docs)
        {
            var sections = StatementLayoutParser.ParseSections(doc.StatementPages, profile, year);
            foreach (var res in sections)
            {
                var first = res.Rows.Select(r => r.Date).FirstOrDefault(d => d.HasValue) ?? res.PeriodEnd;
                var seg = new OcrSegment
                {
                    Source = ShortSource(path),
                    FilePath = path,
                    Doc = doc,
                    Result = res,
                    Opening = res.OpeningCents,
                    Closing = res.ClosingCents,
                    SortKey = first ?? DateOnly.MinValue
                };
                seg.Rows = res.Rows.Select(r => new OcrRowVm(r, seg, this)).ToList();
                segs.Add(seg);
            }
        }
        _segments = segs;

        var items = segs.GroupBy(s => s.Result.AccountNumber ?? "")
            .Select(g =>
            {
                var any = g.First().Result;
                var label = string.IsNullOrWhiteSpace(any.AccountLabel) ? "Compte" : any.AccountLabel;
                var num = string.IsNullOrEmpty(g.Key) ? "" : " n° " + g.Key;
                var ops = g.Sum(s => s.Rows.Count);
                return new AccountItem(g.Key, $"{label}{num} — {ops} opération(s)" + (g.Count() > 1 ? $", {g.Count()} relevés" : ""));
            })
            .OrderByDescending(i => i.Display.Contains("COURANT", StringComparison.OrdinalIgnoreCase))
            .ToList();
        _loading = true;
        AccountPick.ItemsSource = items;
        AccountPick.SelectedIndex = items.Count > 0 ? 0 : -1;
        _loading = false;
        SelectAccount();
    }

    private void SelectAccount()
    {
        var key = (AccountPick.SelectedItem as AccountItem)?.Key;
        _selected = key is null
            ? new List<OcrSegment>()
            : _segments.Where(s => (s.Result.AccountNumber ?? "") == key).OrderBy(s => s.SortKey).ThenBy(s => s.Source, StringComparer.Ordinal).ToList();
        _rows = _selected.SelectMany(s => s.Rows).ToList();
        RowsGrid.ItemsSource = _rows;
        _loading = true;
        var single = _selected.Count == 1;
        BalanceBox.Visibility = single ? Visibility.Visible : Visibility.Collapsed;
        OpeningBox.Text = single && _selected[0].Opening is { } o ? Money.Format(o) : "";
        ClosingBox.Text = single && _selected[0].Closing is { } c ? Money.Format(c) : "";
        _loading = false;
        MessagesText.Text = string.Join("\n", _selected.SelectMany(s => s.Result.Messages).Distinct());
        Recheck();
    }

    // ------------------------------------------------------------------ contrôle

    private static long? ParseBalance(string text) =>
        StatementChecker.TryParseUserAmount(text, out var c) ? c : null;

    public void Recheck()
    {
        if (!IsLoaded || _selected.Count == 0)
        {
            if (IsLoaded) UseButton.IsEnabled = false;
            return;
        }
        if (_selected.Count == 1)
        {
            _selected[0].Opening = ParseBalance(OpeningBox.Text);
            _selected[0].Closing = ParseBalance(ClosingBox.Text);
        }

        var details = new List<string>();
        var failed = false;
        var allVerified = true;
        var incomplete = 0;
        var included = 0;
        long sum = 0;
        var multi = _selected.Count > 1;
        foreach (var seg in _selected)
        {
            var rows = seg.Rows.Select(r => r.Row).ToList();
            var c = StatementChecker.Evaluate(rows, seg.Opening, seg.Closing, seg.Result.TotalDebitsCents, seg.Result.TotalCreditsCents);
            incomplete += c.IncompleteRows;
            included += c.IncludedRows;
            sum += c.SumCents;
            if (c.State == StatementCheckState.Mismatch) failed = true;
            if (c.State != StatementCheckState.Verified) allVerified = false;
            if (multi)
            {
                var head = c.State switch
                {
                    StatementCheckState.Verified => "✔",
                    StatementCheckState.Mismatch => "✘",
                    _ => "?"
                };
                details.Add($"{head} Relevé {seg.Source} : {c.IncludedRows} opération(s)" +
                            (c.State == StatementCheckState.Verified ? " — chiffres justes." : ""));
                if (c.State != StatementCheckState.Verified)
                    details.AddRange(c.Details.Where(d => d.StartsWith('✘') || d.StartsWith("Soldes") || d.Contains("incomplète")).Select(d => "    " + d));
            }
            else details.AddRange(c.Details);
        }
        // Enchaînement : le solde de fin d'un relevé est le solde de début du suivant.
        for (var i = 1; i < _selected.Count; i++)
        {
            var a = _selected[i - 1];
            var b = _selected[i];
            if (a.Closing is { } ce && b.Opening is { } op)
            {
                if (ce == op) continue;
                failed = true;
                details.Add($"✘ Rupture entre {a.Source} (solde de fin {Money.Format(ce)}) et {b.Source} (solde de début {Money.Format(op)}) : relevé manquant ou doublon ?");
            }
        }

        var state = failed ? StatementCheckState.Mismatch : allVerified ? StatementCheckState.Verified : StatementCheckState.Unverifiable;
        foreach (var r in _rows) r.Refresh();
        var check = new StatementCheck(state, incomplete, included, sum, details);

        switch (check.State)
        {
            case StatementCheckState.Verified when check.IncompleteRows == 0:
                CheckBanner.Background = (Brush)FindResource("BrushSuccessLight");
                CheckTitle.Text = $"✔ {(multi ? $"{_selected.Count} relevés vérifiés" : "Relevé vérifié")} : les chiffres tombent juste. " +
                                  $"{check.IncludedRows} opération(s) prête(s).";
                break;
            case StatementCheckState.Mismatch:
                CheckBanner.Background = (Brush)FindResource("BrushDangerLight");
                CheckTitle.Text = "✘ Le relevé ne tombe pas juste : import bloqué. Comparez avec l'image et corrigez la ligne fautive.";
                break;
            case StatementCheckState.Unverifiable:
                CheckBanner.Background = (Brush)FindResource("BrushWarningLight");
                CheckTitle.Text = "Aucun contrôle arithmétique possible : saisissez l'ancien et le nouveau solde (en haut) pour vérifier.";
                break;
            default:
                CheckBanner.Background = (Brush)FindResource("BrushWarningLight");
                CheckTitle.Text = "Des lignes sont incomplètes : corrigez-les ou décochez « Inclure ».";
                break;
        }
        CheckDetails.Text = string.Join("\n", check.Details.Take(24)) + (check.Details.Count > 24 ? "\n…" : "");

        AckBox.Visibility = check.CanImportAfterManualCheck ? Visibility.Visible : Visibility.Collapsed;
        if (!check.CanImportAfterManualCheck) AckBox.IsChecked = false;
        UseButton.IsEnabled = check.CanImport || (check.CanImportAfterManualCheck && AckBox.IsChecked == true);
        UseButton.Content = $"Utiliser ces {check.IncludedRows - check.IncompleteRows} opérations";
    }

    private void Balance_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_loading) Recheck();
    }

    private void Ack_Changed(object sender, RoutedEventArgs e) => Recheck();

    private void Account_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && IsLoaded) SelectAccount();
    }

    private void Profile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_docs.Count > 0 && IsLoaded) Reparse();
    }

    private void Year_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_docs.Count > 0) Reparse();
    }

    // ------------------------------------------------------------------ image de contrôle

    private void Rows_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RowsGrid.SelectedItem is not OcrRowVm vm) return;
        _pageSegment = vm.Segment;
        if (vm.Row.Page >= 1 && vm.Row.Page <= vm.Segment.Doc.Pages.Count) _page = vm.Row.Page - 1;
        ShowPage(vm.Row);
    }

    private void ShowPage(StatementRow? highlight = null)
    {
        Overlay.Children.Clear();
        var seg = _pageSegment ?? _selected.FirstOrDefault();
        var doc = seg?.Doc ?? _docs.FirstOrDefault().Doc;
        if (doc is null || doc.Pages.Count == 0) return;
        _page = Math.Clamp(_page, 0, doc.Pages.Count - 1);
        var rp = doc.Pages[_page];
        PageText.Text = (seg is null ? "" : seg.Source + " · ") + $"Page {_page + 1} / {doc.Pages.Count}" + (rp.UsedOcr ? " (OCR)" : "");
        PageImage.Source = rp.Image;
        if (rp.Image is null) return;
        var w = _zoom;
        var h = _zoom * rp.Page.Height / Math.Max(rp.Page.Width, 1);
        PageImage.Width = w;
        PageImage.Height = h;
        Overlay.Width = w;
        Overlay.Height = h;
        if (highlight is null || highlight.Page != rp.Page.Number) return;
        var k = w / Math.Max(rp.Page.Width, 1);
        var pad = 3.0;
        var rect = new System.Windows.Shapes.Rectangle
        {
            Width = Math.Max((highlight.Right - highlight.Left) * k + pad * 2, 12),
            Height = Math.Max((highlight.Bottom - highlight.Top) * k + pad * 2, 8),
            Stroke = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(40, 0xDC, 0x26, 0x26))
        };
        Canvas.SetLeft(rect, highlight.Left * k - pad);
        Canvas.SetTop(rect, highlight.Top * k - pad);
        Overlay.Children.Add(rect);
        ImageScroll.ScrollToVerticalOffset(Math.Max(0, highlight.Top * k - ImageScroll.ViewportHeight / 3));
    }

    private void PrevPage_Click(object sender, RoutedEventArgs e)
    {
        _page--;
        ShowPage();
    }

    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        _page++;
        ShowPage();
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        _zoom = Math.Min(_zoom * 1.25, 2400);
        ShowPage((RowsGrid.SelectedItem as OcrRowVm)?.Row);
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        _zoom = Math.Max(_zoom / 1.25, 320);
        ShowPage((RowsGrid.SelectedItem as OcrRowVm)?.Row);
    }

    // ------------------------------------------------------------------ validation

    private void Use_Click(object sender, RoutedEventArgs e)
    {
        if (!UseButton.IsEnabled) return;
        Lines = StatementChecker.ToBankLines(_rows.Select(r => r.Row));
        if (Lines.Count == 0)
        {
            Ui.Error("Aucune opération à importer.");
            return;
        }
        DialogResult = true;
    }
}
