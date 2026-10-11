using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ToutCompteFait.Core;
using ToutCompteFait.Core.Security;

namespace ToutCompteFait.Dialogs;

/// <summary>Numéros de compte comptable des catégories et des comptes bancaires, utilisés par l'export FEC, et réglages
/// d'export (nom de l'association, SIREN). Paramétrage : modifiable à tout moment, y compris pour les exercices déjà
/// validés (il n'entre pas dans leur empreinte).</summary>
public sealed class AccountNumbersWindow : Window
{
    public sealed class Row
    {
        public string Group { get; init; } = "";
        public string Name { get; init; } = "";
        public string Auto { get; init; } = "";
        public string Number { get; set; } = "";
        public string Original { get; init; } = "";
        public CategoryKind? Kind { get; init; }
        public long? AccountId { get; init; }
    }

    private readonly Compta _c = App.Compta;
    private readonly List<Row> _rows = new();
    private readonly TextBox _org = new() { Margin = new Thickness(0, 2, 0, 8) };
    private readonly TextBox _siren = new() { Margin = new Thickness(0, 2, 0, 8) };

    public AccountNumbersWindow()
    {
        Title = "Numéros de compte comptable (FEC)";
        Width = 720;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("BrushBackground");

        var canEdit = App.Can(Permission.ManageStructure) && !App.IsReadOnly;
        foreach (var a in _c.Accounts(includeInactive: true))
            _rows.Add(new Row { Group = "Compte de trésorerie", Name = a.Name, AccountId = a.Id,
                                Number = a.AccountNumber ?? "", Original = a.AccountNumber ?? "", Auto = "automatique (512…, 530…)" });
        foreach (var k in _c.CategoryAccountNumbers())
            _rows.Add(new Row { Group = k.Kind == CategoryKind.Product ? "Produit (classe 7)" : "Charge (classe 6)", Name = k.Name, Kind = k.Kind,
                                Number = k.Number ?? "", Original = k.Number ?? "", Auto = k.AutoNumber });

        var root = new DockPanel { Margin = new Thickness(18) };
        var top = new StackPanel();
        top.Children.Add(new TextBlock
        {
            Text = "Le fichier FEC demandé en cas de contrôle fiscal range chaque écriture sur un compte comptable. " +
                   "Saisissez le numéro de compte de vos produits et charges (ex. 706000, 606000) ; à défaut, un numéro automatique est utilisé. " +
                   "Ces numéros peuvent être complétés ou corrigés à tout moment, même pour un exercice déjà validé.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
        });
        top.Children.Add(new TextBlock { Text = "Nom de l'association (en-tête du livre exporté)" });
        _org.Text = App.Settings.OrganizationName ?? "";
        top.Children.Add(_org);
        top.Children.Add(new TextBlock { Text = "SIREN (9 chiffres, nom du fichier FEC)" });
        _siren.Text = App.Settings.Siren ?? "";
        top.Children.Add(_siren);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var close = new Button { Content = "Fermer", IsCancel = true, Style = (Style)Application.Current.FindResource("SecondaryButton") };
        var save = new Button { Content = "Enregistrer", IsDefault = true, Margin = new Thickness(0), Style = (Style)Application.Current.FindResource("PrimaryButton") };
        save.Click += Save_Click;
        buttons.Children.Add(close);
        buttons.Children.Add(save);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var grid = new DataGrid
        {
            ItemsSource = _rows, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
            SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Type", Binding = new Binding(nameof(Row.Group)), IsReadOnly = true, Width = 150 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Nom", Binding = new Binding(nameof(Row.Name)), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "N° de compte", Binding = new Binding(nameof(Row.Number)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus },
                                                  IsReadOnly = !canEdit, Width = 120 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Par défaut", Binding = new Binding(nameof(Row.Auto)), IsReadOnly = true, Width = 150 });
        root.Children.Add(grid);
        save.IsEnabled = canEdit;
        Content = root;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var siren = new string(_siren.Text.Where(char.IsDigit).ToArray());
        if (siren.Length != 0 && siren.Length != 9)
        {
            Ui.Error("Le SIREN comporte 9 chiffres.");
            return;
        }
        var ok = Ui.Try(() =>
        {
            using var tx = _c.Transaction();
            foreach (var r in _rows.Where(r => r.Number.Trim() != r.Original))
            {
                if (r.AccountId is { } id) _c.SetAccountNumber(id, r.Number);
                else _c.SetCategoryAccountNumber(r.Kind!.Value, r.Name, r.Number);
            }
            tx.Commit();
        });
        if (!ok) return;
        App.Settings.OrganizationName = _org.Text.Trim();
        App.Settings.Siren = siren;
        App.Settings.Save();
        DialogResult = true;
    }
}
