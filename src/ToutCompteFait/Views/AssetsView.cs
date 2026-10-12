using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ToutCompteFait.Core;
using ToutCompteFait.Core.Security;
using ToutCompteFait.Dialogs;

namespace ToutCompteFait.Views;

/// <summary>Immobilisations : matériel acheté et amorti sur sa durée d'utilisation. Le registre vaut pour tous les
/// exercices ; les colonnes de dotation et de valeur nette portent sur l'exercice affiché.</summary>
public sealed class AssetsView : UserControl, IPage
{
    private sealed record Row(FixedAsset Asset, string Label, string Acquired, string Cost, string Duration, string Mode,
        string Depreciation, string Cumulative, string Net, string State);

    private readonly Compta _c = App.Compta;
    private readonly FiscalYear _year;
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Button _add = new() { Content = "＋  Nouvelle immobilisation" };
    private readonly Button _edit = new() { Content = "Modifier" };
    private readonly Button _dispose = new() { Content = "Sortie du bien…" };
    private readonly Button _undispose = new() { Content = "Annuler la sortie" };
    private readonly Button _delete = new() { Content = "Supprimer" };

    public AssetsView(FiscalYear year)
    {
        _year = year;
        var root = new DockPanel();
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = $"Immobilisations — exercice {year.Label}", Style = Sty("TitleText") });
        head.Children.Add(new TextBlock
        {
            Style = Sty("MutedText"), TextWrapping = TextWrapping.Wrap,
            Text = "Un matériel durable est amorti : sa valeur est étalée en charges sur sa durée d'utilisation (ex. 1 000 € sur 4 ans = 250 € par an) " +
                   "au lieu de peser en une seule fois sur l'exercice d'achat. La valeur nette figure à l'actif du bilan. " +
                   "Le petit matériel sous le seuil réglé dans Paramètres reste une charge directe."
        });
        var bar = new WrapPanel { Margin = new Thickness(0, 12, 0, 8) };
        _add.Style = Sty("PrimaryButton");
        foreach (var b in new[] { _edit, _dispose, _undispose }) { b.Style = Sty("SecondaryButton"); bar.Children.Add(b); }
        bar.Children.Insert(0, _add);
        _delete.Style = Sty("DangerButton");
        bar.Children.Add(_delete);
        head.Children.Add(bar);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        _summary.Foreground = System.Windows.Media.Brushes.Gray;
        DockPanel.SetDock(_summary, Dock.Bottom);
        root.Children.Add(_summary);

        void Col(string header, string path, double width, bool right = false, bool star = false)
        {
            var style = right ? Sty("RightCell") : Sty("LeftCell");
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = header, Binding = new System.Windows.Data.Binding(path),
                Width = star ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width),
                ElementStyle = style, HeaderStyle = right ? Sty("HeaderRight") : null
            });
        }
        Col("Bien", nameof(Row.Label), 0, star: true);
        Col("Acquisition", nameof(Row.Acquired), 92);
        Col("Valeur", nameof(Row.Cost), 90, right: true);
        Col("Durée", nameof(Row.Duration), 70);
        Col("Prorata", nameof(Row.Mode), 70);
        Col("Dotation de l'exercice", nameof(Row.Depreciation), 130, right: true);
        Col("Amortissements cumulés", nameof(Row.Cumulative), 150, right: true);
        Col("Valeur nette (clôture)", nameof(Row.Net), 140, right: true);
        Col("État", nameof(Row.State), 150);
        root.Children.Add(_grid);
        Content = root;

        _add.Click += (_, _) => Add();
        _edit.Click += (_, _) => Edit();
        _dispose.Click += (_, _) => Dispose();
        _undispose.Click += (_, _) => Undispose();
        _delete.Click += (_, _) => Delete();
        _grid.MouseDoubleClick += (_, _) => Edit();
        _grid.SelectionChanged += (_, _) => UpdateButtons();
        Reload();
    }

    private static Style Sty(string key) => (Style)Application.Current.FindResource(key);

    private bool CanManage => App.Can(Permission.ManageStructure) && !App.IsReadOnly;
    private Row? Selected => _grid.SelectedItem as Row;

    private IEnumerable<string> ChargeNames() =>
        _c.Categories(_year.Id).Where(k => k.Kind == CategoryKind.Charge).Select(k => k.Name);

    private bool IsFrozen(FixedAsset a) => _c.Years().Where(y => y.Validated).Any(y => y.End >= a.AcquiredOn);

    private void UpdateButtons()
    {
        var sel = Selected?.Asset;
        _add.IsEnabled = CanManage;
        _edit.IsEnabled = sel is not null && CanManage;
        _dispose.IsEnabled = sel is { Disposed: false } && CanManage;
        _undispose.IsEnabled = sel is { Disposed: true } && CanManage;
        _delete.IsEnabled = sel is not null && CanManage;
    }

    public void Reload()
    {
        var keep = Selected?.Asset.Id;
        var assets = _c.FixedAssets();
        var rows = new List<Row>();
        long dep = 0, net = 0, reclass = 0;
        foreach (var a in assets)
        {
            var l = AssetCalc.ForYear(a, _year.Start, _year.End);
            dep += l.Depreciation;
            net += l.Closing;
            reclass += l.Reclass;
            var state = a.DisposedOn is { } d ? $"Sorti le {d:dd/MM/yyyy}"
                : a.AcquiredOn > _year.End ? "Pas encore acquis"
                : l.Closing == 0 ? "Totalement amorti"
                : a.Existing ? "En service (repris)" : "En service";
            rows.Add(new Row(a, a.Label, a.AcquiredOn.ToString("dd/MM/yyyy"), Money.Format(a.CostCents) + " €", a.Years + " an(s)",
                a.Prorata == AssetProrata.Daily ? "Jour" : "Mois",
                l.Depreciation == 0 ? "" : Money.Format(l.Depreciation) + " €",
                l.CumulativeAtClose == 0 ? "" : Money.Format(l.CumulativeAtClose) + " €",
                Money.Format(l.Closing) + " €", state));
        }
        _grid.ItemsSource = rows;
        if (keep is { } id) _grid.SelectedItem = rows.FirstOrDefault(r => r.Asset.Id == id);
        _summary.Text = assets.Count == 0
            ? "Aucune immobilisation. Ajoutez un bien ici, ou depuis le Livre (« Immobiliser… » sur une dépense)."
            : $"Exercice {_year.Label} : dotations aux amortissements {Money.Format(dep)} € · valeur nette des immobilisations à la clôture {Money.Format(net)} €" +
              (reclass > 0 ? $" · achats immobilisés retirés des charges {Money.Format(reclass)} €." : ".");
        UpdateButtons();
    }

    private void Add()
    {
        var dlg = new AssetWindow("Nouvelle immobilisation", ChargeNames(), _c.AssetThresholdCents()) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        if (Ui.Try(() => _c.AddFixedAsset(dlg.AssetLabel, dlg.AcquiredOn, dlg.CostCents, dlg.Years, dlg.Prorata, dlg.Existing, dlg.CategoryName, null, dlg.Note)))
            Reload();
    }

    private void Edit()
    {
        if (Selected?.Asset is not { } a || !CanManage) return;
        var dlg = new AssetWindow("Modifier l'immobilisation", ChargeNames(), 0, a, locked: IsFrozen(a)) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        if (Ui.Try(() => _c.UpdateFixedAsset(a.Id, dlg.AssetLabel, dlg.AcquiredOn, dlg.CostCents, dlg.Years, dlg.Prorata, dlg.Existing,
                dlg.CategoryName, dlg.Note)))
            Reload();
    }

    private void Dispose()
    {
        if (Selected?.Asset is not { } a) return;
        var suggested = DateTime.Today.ToString("dd/MM/yyyy");
        var text = PromptWindow.Ask(Window.GetWindow(this), "Sortie du bien",
            $"« {a.Label} » : date de la vente, de la mise au rebut ou de la perte (jj/mm/aaaa).\n" +
            "La valeur nette restante est passée en charge ; l'encaissement éventuel est un produit à saisir dans le Livre.",
            suggested, "Enregistrer la sortie");
        if (text is null) return;
        if (!DateOnly.TryParseExact(text.Trim(), "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            Ui.Error("Date illisible : écrivez-la jj/mm/aaaa.");
            return;
        }
        if (Ui.Try(() => _c.DisposeFixedAsset(a.Id, d))) Reload();
    }

    private void Undispose()
    {
        if (Selected?.Asset is not { } a) return;
        if (Ui.Confirm($"Annuler la sortie de « {a.Label} » ? Le bien redevient en service.", "Annuler la sortie") &&
            Ui.Try(() => _c.DisposeFixedAsset(a.Id, null)))
            Reload();
    }

    private void Delete()
    {
        if (Selected?.Asset is not { } a) return;
        if (Ui.Confirm($"Supprimer l'immobilisation « {a.Label} » du registre ?\n\nL'achat redevient une charge dans le livre.", "Supprimer") &&
            Ui.Try(() => _c.DeleteFixedAsset(a.Id)))
            Reload();
    }
}
