using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ToutCompteFait.Core;
using ToutCompteFait.Core.Security;
using ToutCompteFait.Dialogs;

namespace ToutCompteFait.Views;

public sealed class AdjustmentRow
{
    public required Adjustment Source { get; init; }
    public string Label => Source.Label;
    public string Nature { get; init; } = "";
    public string Category { get; init; } = "";
    public string TypeText { get; init; } = "";
    public string Effect { get; init; } = "";
    public string Origin { get; init; } = "";
    public bool Unassigned => Source.CategoryId is null;
}

public partial class ReportsView : UserControl, IPage
{
    private readonly FiscalYear _year;
    private readonly Compta _c = App.Compta;

    public ReportsView(FiscalYear year)
    {
        InitializeComponent();
        _year = year;
        TitleText.Text = $"Reports entre exercices — exercice {year.Label}";
        var can = App.Can(Permission.WriteLedger) && year.Open;
        AddButton.IsEnabled = can;
        Reload();
    }

    public void Reload()
    {
        var cats = _c.Categories(_year.Id).ToDictionary(c => c.Id);
        var years = _c.Years().ToDictionary(y => y.Id);
        var all = _c.Adjustments(_year.Id);
        var prev = _c.PreviousYear(_year);
        var srcYears = new Dictionary<long, string>();
        foreach (var a in all.Where(a => a.SourceId is not null))
            if (prev is not null) srcYears[a.Id] = prev.Label;

        AdjGrid.ItemsSource = all.Select(a => new AdjustmentRow
        {
            Source = a,
            Nature = a.Kind == CategoryKind.Product ? "Produit" : "Charge",
            Category = a.CategoryId is { } id && cats.TryGetValue(id, out var c) ? c.Name : "⚠ À affecter",
            TypeText = Labels.Of(a.Type),
            Effect = (a.Sign > 0 ? "+" : "−") + Money.Format(a.AmountCents),
            Origin = a.SourceId is not null ? $"Reporté de {srcYears.GetValueOrDefault(a.Id, Wording.Adapt("l'exercice précédent"))}" : "Saisi à la main"
        }).ToList();

        Empty.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var un = all.Count(a => a.CategoryId is null);
        if (un > 0)
        {
            WarnText.Text = $"⚠ {un} report(s) sans catégorie : sélectionnez-les puis « Modifier / affecter » pour choisir la catégorie. " +
                            "Tant que c'est en attente, l'exercice ne peut pas être validé.";
            WarnBanner.Visibility = Visibility.Visible;
        }
        else WarnBanner.Visibility = Visibility.Collapsed;
        Grid_SelectionChanged(this, null!);
    }

    private AdjustmentRow? Selected => AdjGrid.SelectedItem as AdjustmentRow;

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var can = App.Can(Permission.WriteLedger) && _year.Open && Selected is not null;
        EditButton.IsEnabled = can;
        DeleteButton.IsEnabled = can && Selected!.Source.SourceId is null;
    }

    private void Grid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (App.Can(Permission.WriteLedger) && _year.Open && Selected is not null) Edit_Click(sender, e);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new AdjustmentWindow(_year, null) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true) Reload();
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        var dlg = new AdjustmentWindow(_year, row.Source) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true) Reload();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row || row.Source.SourceId is not null) return;
        if (!Ui.Confirm($"Supprimer le report « {row.Label} » ({Money.Format(row.Source.AmountCents)} €) ?")) return;
        if (Ui.Try(() => _c.DeleteAdjustment(row.Source.Id))) Reload();
    }
}

