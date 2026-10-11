using System.Windows;
using System.Windows.Controls;
using ComptaAepby.Core;

namespace ComptaAepby.Dialogs;

/// <summary>Demande la date de clôture d'un exercice (saisie manuelle ; par défaut la fin prévue de l'exercice).
/// Les écritures datées après cette date seront déplacées dans l'exercice suivant.</summary>
public sealed class CloseYearWindow : Window
{
    private readonly FiscalYear _year;
    private readonly Func<DateOnly, int> _countAfter;
    private readonly DatePicker _picker = new() { Width = 170, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };

    public DateOnly ClosingDate { get; private set; }

    public CloseYearWindow(FiscalYear year, Func<DateOnly, int> countAfter)
    {
        _year = year;
        _countAfter = countAfter;
        ClosingDate = year.End;
        Title = $"Valider l'exercice {year.Label}";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("BrushBackground");

        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(new TextBlock
        {
            Text = "Date de clôture de l'exercice",
            Style = (Style)Application.Current.FindResource("TitleText")
        });
        root.Children.Add(new TextBlock
        {
            Text = $"Exercice {year.Label} : du {year.Start:dd/MM/yyyy} au {year.End:dd/MM/yyyy} (fin prévue).\n" +
                   "Saisissez la date à laquelle l'exercice est réellement clôturé. Les écritures du livre datées après " +
                   "cette date seront ajoutées à l'exercice suivant (créé si besoin).",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 14),
            Style = (Style)Application.Current.FindResource("MutedText")
        });

        root.Children.Add(new TextBlock { Text = "Date de clôture", Margin = new Thickness(0, 0, 0, 4) });
        _picker.SelectedDate = year.End.ToDateTime(TimeOnly.MinValue);
        _picker.SelectedDateChanged += (_, _) => Refresh();
        root.Children.Add(_picker);
        root.Children.Add(_info);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var ok = new Button { Content = "Valider l'exercice", IsDefault = true, Style = (Style)Application.Current.FindResource("PrimaryButton") };
        ok.Click += (_, _) =>
        {
            if (_picker.SelectedDate is not { } d) { _info.Text = "Saisissez une date de clôture."; return; }
            var date = DateOnly.FromDateTime(d);
            if (date < _year.Start)
            {
                _info.Text = $"La date de clôture ne peut pas précéder le début de l'exercice ({_year.Start:dd/MM/yyyy}).";
                return;
            }
            ClosingDate = date;
            DialogResult = true;
        };
        var cancel = new Button { Content = "Annuler", IsCancel = true, Style = (Style)Application.Current.FindResource("SecondaryButton") };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;
        Refresh();
    }

    private void Refresh()
    {
        if (_picker.SelectedDate is not { } d) { _info.Text = ""; return; }
        var date = DateOnly.FromDateTime(d);
        var lines = new List<string>();
        if (date < _year.Start)
        {
            _info.Text = $"⚠ Avant le début de l'exercice ({_year.Start:dd/MM/yyyy}).";
            return;
        }
        var after = _countAfter(date);
        lines.Add(after == 0
            ? "Aucune écriture du livre n'est postérieure à cette date."
            : $"{after} écriture(s) postérieure(s) au {date:dd/MM/yyyy} seront déplacée(s) dans l'exercice suivant.");
        if (date != _year.End)
            lines.Add($"La fin de l'exercice passe du {_year.End:dd/MM/yyyy} au {date:dd/MM/yyyy} ; l'exercice suivant commence le {date.AddDays(1):dd/MM/yyyy}.");
        _info.Text = string.Join("\n", lines);
    }
}
