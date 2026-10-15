using System.Windows;
using System.Windows.Controls;
using ToutCompteFait.Core;

namespace ToutCompteFait.Dialogs;

/// <summary>Saisie ou modification d'une immobilisation (matériel amorti sur sa durée d'utilisation).</summary>
public sealed class AssetWindow : Window
{
    private readonly TextBox _label = new();
    private readonly DatePicker _date = new();
    private readonly TextBox _cost = new();
    private readonly TextBox _years = new() { Text = "4" };
    private readonly ComboBox _prorata = new();
    private readonly CheckBox _existing = new()
    {
        Content = "Bien déjà possédé (repris à sa valeur nette, sans achat dans le livre)",
        ToolTip = "À cocher pour un bien acheté lors d'un exercice déjà validé : saisissez sa valeur nette, la date de début de l'exercice ouvert et la durée restante."
    };
    private readonly ComboBox _category = new() { IsEditable = true };
    private readonly TextBox _note = new();
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Foreground = System.Windows.Media.Brushes.Gray };
    private readonly long _threshold;
    private readonly bool _locked;

    public string AssetLabel => _label.Text.Trim();
    public DateOnly AcquiredOn => DateOnly.FromDateTime(_date.SelectedDate ?? DateTime.Today);
    public long CostCents { get; private set; }
    public int Years { get; private set; }
    public AssetProrata Prorata => _prorata.SelectedIndex == 1 ? AssetProrata.Daily : AssetProrata.Monthly;
    public bool Existing => _existing.IsChecked == true;
    public string? CategoryName => string.IsNullOrWhiteSpace(_category.Text) ? null : _category.Text.Trim();
    public string? Note => string.IsNullOrWhiteSpace(_note.Text) ? null : _note.Text.Trim();

    /// <param name="locked">Bien déjà compté dans un exercice validé : seuls libellé, catégorie et commentaire changent.</param>
    public AssetWindow(string title, IEnumerable<string> chargeCategories, long thresholdCents, FixedAsset? existing = null,
        bool locked = false, string? label = null, DateOnly? date = null, long? costCents = null, string? category = null)
    {
        _threshold = thresholdCents;
        _locked = locked;
        Title = title;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("BrushSurface");

        _prorata.ItemsSource = new[] { "Au mois (le mois d'acquisition compte en entier)", "Au jour" };
        _prorata.SelectedIndex = 0;
        _category.ItemsSource = chargeCategories.ToList();

        if (existing is not null)
        {
            _label.Text = existing.Label;
            _date.SelectedDate = existing.AcquiredOn.ToDateTime(TimeOnly.MinValue);
            _cost.Text = Money.Format(existing.CostCents);
            _years.Text = existing.Years.ToString();
            _prorata.SelectedIndex = existing.Prorata == AssetProrata.Daily ? 1 : 0;
            _existing.IsChecked = existing.Existing;
            _category.Text = existing.CategoryName ?? "";
            _note.Text = existing.Note ?? "";
        }
        else
        {
            _label.Text = label ?? "";
            _date.SelectedDate = (date ?? DateOnly.FromDateTime(DateTime.Today)).ToDateTime(TimeOnly.MinValue);
            if (costCents is { } c) _cost.Text = Money.Format(c);
            _category.Text = category ?? "";
        }
        if (locked)
        {
            _date.IsEnabled = _cost.IsEnabled = _years.IsEnabled = _prorata.IsEnabled = _existing.IsEnabled = false;
            _hint.Text = "Ce bien est déjà compté dans un exercice validé : sa valeur, sa date, sa durée et son mode sont figés.";
        }

        var panel = new StackPanel { Margin = new Thickness(20) };
        void Row(string caption, UIElement field)
        {
            panel.Children.Add(new TextBlock { Text = caption, Margin = new Thickness(0, 8, 0, 3) });
            panel.Children.Add(field);
        }
        Row("Libellé", _label);
        Row("Date d'acquisition (mise en service)", _date);
        Row("Valeur d'achat (€) — ou valeur nette pour un bien déjà possédé", _cost);
        Row("Durée d'amortissement (années)", _years);
        Row("Prorata de la première année", _prorata);
        panel.Children.Add(_existing);
        _existing.Margin = new Thickness(0, 10, 0, 0);
        Row("Catégorie de charge de l'achat (facultatif)", _category);
        Row("Commentaire (facultatif)", _note);
        panel.Children.Add(_hint);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "Annuler", IsCancel = true, Style = (Style)Application.Current.FindResource("SecondaryButton") };
        var ok = new Button { Content = "Enregistrer", IsDefault = true, Margin = new Thickness(0), Style = (Style)Application.Current.FindResource("PrimaryButton") };
        ok.Click += (_, _) => Accept();
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => { _label.Focus(); _label.SelectAll(); };
    }

    private void Accept()
    {
        if (string.IsNullOrWhiteSpace(_label.Text)) { Ui.Error("Le libellé est obligatoire."); return; }
        if (!Money.TryParse(_cost.Text, out var cents) || cents <= 0) { Ui.Error("Indiquez une valeur supérieure à zéro."); return; }
        if (!int.TryParse(_years.Text.Trim(), out var years) || years is < 1 or > 50) { Ui.Error("La durée est de 1 à 50 ans."); return; }
        if (_date.SelectedDate is null) { Ui.Error("Indiquez la date d'acquisition."); return; }
        if (!_locked && !Existing && _threshold > 0 && cents < _threshold &&
            !Ui.Confirm($"Cette valeur ({Money.Format(cents)} €) est inférieure au seuil du petit matériel ({Money.Format(_threshold)} €) : " +
                        "elle peut rester une charge directe, sans amortissement.\n\nImmobiliser quand même ?", "Petit matériel"))
            return;
        CostCents = cents;
        Years = years;
        DialogResult = true;
    }
}
