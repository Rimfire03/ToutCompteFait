using System.Windows;
using System.Windows.Controls;
using ToutCompteFait.Core;

namespace ToutCompteFait.Dialogs;

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
            new TypeItem(AdjustmentType.ToNext, "Réglé cet exercice, concerne l'exercice suivant",
                "Ex. lots de loto achetés en juin pour le loto d'octobre. Retiré du résultat de cet exercice ; ajouté automatiquement à l'exercice suivant."),
            new TypeItem(AdjustmentType.FromPrev, "Réglé l'exercice précédent, concerne cet exercice",
                "Ex. lots achetés l'exercice précédent pour le loto de cet exercice. Ajouté au résultat de cet exercice. (Normalement reçu automatiquement ; à saisir à la main seulement s'il n'existait pas d'exercice précédent.)"),
            new TypeItem(AdjustmentType.Due, "Concerne cet exercice, à régler l'exercice suivant",
                "Ex. facture reçue en août mais payée en septembre. Ajouté au résultat de cet exercice ; compensé automatiquement l'exercice suivant."),
            new TypeItem(AdjustmentType.ForPast, "Réglé cet exercice, concernait un exercice passé",
                "Ex. cotisation d'un exercice précédent encaissée seulement maintenant. Retiré du résultat de cet exercice.")
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
                ErrorText.Text = "Indiquez la catégorie qui recevra ce montant l'exercice suivant.";
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
