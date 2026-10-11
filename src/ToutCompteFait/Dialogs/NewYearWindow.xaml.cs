using System.Windows;
using ToutCompteFait.Core;

namespace ToutCompteFait.Dialogs;

public partial class NewYearWindow : Window
{
    public DateOnly Start { get; private set; }
    public DateOnly End { get; private set; }
    public string YearLabel { get; private set; } = "";
    private bool _labelEdited;

    public NewYearWindow(DateOnly start, DateOnly end)
    {
        InitializeComponent();
        StartPicker.SelectedDate = start.ToDateTime(TimeOnly.MinValue);
        EndPicker.SelectedDate = end.ToDateTime(TimeOnly.MinValue);
        LabelBox.Text = Compta.DefaultLabel(start, end);
        LabelBox.TextChanged += (_, _) => { if (LabelBox.IsKeyboardFocused) _labelEdited = true; };
    }

    private void Dates_Changed(object? sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_labelEdited || StartPicker.SelectedDate is not { } s || EndPicker.SelectedDate is not { } en) return;
        LabelBox.Text = Compta.DefaultLabel(DateOnly.FromDateTime(s), DateOnly.FromDateTime(en));
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (StartPicker.SelectedDate is not { } s || EndPicker.SelectedDate is not { } en)
        {
            ErrorText.Text = "Renseignez les deux dates.";
            return;
        }
        Start = DateOnly.FromDateTime(s);
        End = DateOnly.FromDateTime(en);
        if (End <= Start) { ErrorText.Text = "La date de fin doit être après la date de début."; return; }
        YearLabel = LabelBox.Text.Trim();
        if (YearLabel.Length == 0) { ErrorText.Text = "Donnez un nom à l'exercice."; return; }
        DialogResult = true;
    }
}
