using System.Windows;
using System.Windows.Controls;
using ComptaAepby.Core;

namespace ComptaAepby.Dialogs;

public partial class AdjustmentWindow : Window
{
    private sealed record TypeItem(AdjustmentType Type, string Label, string Help)
    {
        public override string ToString() => Label;
    }

    private readonly Compta _c = App.Compta;
    private readonly FiscalYear _year;
    private readonly Adjustment? _existing;
    private readonly List<Category> _cats;
    private readonly List<Category> _nextCats;
    private bool _ready;

    public AdjustmentWindow(FiscalYear year, Adjustment? existing)
    {
        InitializeComponent();
        _year = year;
        _existing = existing;
        _cats = _c.Categories(year.Id);
        var next = _c.NextYear(year);
        _nextCats = next is null ? new List<Category>() : _c.Categories(next.Id);

        TypeCombo.ItemsSource = new[]
        {
            new TypeItem(AdjustmentType.ToNext, "Réglé cette année, concerne l'année prochaine",
                "Ex. lots de loto achetés en juin pour le loto d'octobre. Retiré du résultat de cette année ; ajouté automatiquement à l'année suivante."),
            new TypeItem(AdjustmentType.FromPrev, "Réglé l'an dernier, concerne cette année",
                "Ex. lots achetés l'an dernier pour le loto de cette année. Ajouté au résultat de cette année. (Normalement reçu automatiquement ; à saisir à la main seulement s'il n'existait pas d'exercice précédent.)"),
            new TypeItem(AdjustmentType.Due, "Concerne cette année, à régler l'an prochain",
                "Ex. facture reçue en août mais payée en septembre. Ajouté au résultat de cette année ; compensé automatiquement l'an prochain."),
            new TypeItem(AdjustmentType.ForPast, "Réglé cette année, concernait une année passée",
                "Ex. cotisation d'une année précédente encaissée seulement maintenant. Retiré du résultat de cette année.")
        };

        if (existing is null)
        {
            TypeCombo.SelectedIndex = 0;
            TitleText.Text = "Nouveau report";
        }
        else
        {
            TitleText.Text = existing.SourceId is null ? "Modifier le report" : "Affecter le report";
            TypeCombo.SelectedItem = TypeCombo.Items.OfType<TypeItem>().First(t => t.Type == existing.Type);
            KindCharge.IsChecked = existing.Kind == CategoryKind.Charge;
            KindProduct.IsChecked = existing.Kind == CategoryKind.Product;
            LabelBox.Text = existing.Label;
            AmountBox.Text = Money.Format(existing.AmountCents);
            NoteBox.Text = existing.Note ?? "";
        }
        _ready = true;
        FillCategories();
        if (existing is not null)
        {
            CatCombo.SelectedItem = _cats.FirstOrDefault(c => c.Id == existing.CategoryId);
            if (existing.TargetName is { Length: > 0 } tn && existing.SourceId is null) TargetCombo.Text = tn;
            if (existing.SourceId is not null)
            {
                CarriedNote.Visibility = Visibility.Visible;
                TypeCombo.IsEnabled = false;
                KindCharge.IsEnabled = false;
                KindProduct.IsEnabled = false;
                LabelBox.IsEnabled = false;
                AmountBox.IsEnabled = false;
                TargetPanel.Visibility = Visibility.Collapsed;
            }
        }
    }

    private CategoryKind CurrentKind => KindProduct.IsChecked == true ? CategoryKind.Product : CategoryKind.Charge;
    private AdjustmentType CurrentType => (TypeCombo.SelectedItem as TypeItem)?.Type ?? AdjustmentType.ToNext;

    private void FillCategories()
    {
        if (!_ready) return;
        var kind = CurrentKind;
        CatCombo.ItemsSource = _cats.Where(c => c.Kind == kind).ToList();
        var names = (_nextCats.Count > 0 ? _nextCats : _cats).Where(c => c.Kind == kind).Select(c => c.Name).ToList();
        TargetCombo.ItemsSource = names;
        if (_existing is null && CatCombo.Items.Count > 0 && CatCombo.SelectedItem is null) CatCombo.SelectedIndex = -1;
    }

    private void Kind_Changed(object sender, RoutedEventArgs e) => FillCategories();

    private void Type_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (TypeCombo.SelectedItem is not TypeItem t) return;
        TypeHelp.Text = t.Help;
        var needsTarget = t.Type is AdjustmentType.ToNext or AdjustmentType.Due;
        if (TargetPanel is not null && (_existing?.SourceId is null))
            TargetPanel.Visibility = needsTarget ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        var cat = CatCombo.SelectedItem as Category;
        try
        {
            if (_existing is not null && _existing.SourceId is not null)
            {
                _c.UpdateAdjustment(_existing.Id, cat?.Id, _existing.Label, _existing.AmountCents, null, NoteBox.Text);
                DialogResult = true;
                return;
            }
            if (!Money.TryParse(AmountBox.Text, out var cents) || cents <= 0)
            {
                ErrorText.Text = "Saisissez un montant supérieur à zéro.";
                return;
            }
            if (cat is null)
            {
                ErrorText.Text = "Choisissez la catégorie concernée.";
                return;
            }
            var type = CurrentType;
            var target = type is AdjustmentType.ToNext or AdjustmentType.Due ? TargetCombo.Text.Trim() : null;
            if (type == AdjustmentType.ToNext && string.IsNullOrEmpty(target))
            {
                ErrorText.Text = "Indiquez la catégorie qui recevra ce montant l'an prochain.";
                return;
            }
            if (type == AdjustmentType.Due && string.IsNullOrEmpty(target)) target = cat.Name;

            if (_existing is null)
                _c.AddAdjustment(_year.Id, type, CurrentKind, cat.Id, LabelBox.Text, cents, target, NoteBox.Text);
            else
                _c.UpdateAdjustment(_existing.Id, cat.Id, LabelBox.Text, cents, target, NoteBox.Text);
            DialogResult = true;
        }
        catch (ComptaException ex)
        {
            ErrorText.Text = ex.Message;
        }
    }
}
