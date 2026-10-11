using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ToutCompteFait.Controls;

/// <summary>Zone de dépôt de fichiers (cadre pointillé, icône, « Glissez-déposez … ou Parcourir »). Le dépôt lui-même est géré par la fenêtre ; la zone s'illumine pendant le survol et ouvre le sélecteur au clic.</summary>
public partial class DropZone : System.Windows.Controls.UserControl
{
    public event RoutedEventHandler? Browse;

    public DropZone()
    {
        InitializeComponent();
        Title = "Glissez-déposez vos fichiers ici ou <b>Parcourir</b>";
        // handledEventsToo : la fenêtre marque les évènements Preview comme traités.
        AddHandler(DragDrop.PreviewDragEnterEvent, new DragEventHandler((_, _) => Highlight(true)), true);
        AddHandler(DragDrop.PreviewDragLeaveEvent, new DragEventHandler((_, _) => Highlight(false)), true);
        AddHandler(DragDrop.PreviewDropEvent, new DragEventHandler((_, _) => Highlight(false)), true);
    }

    /// <summary>Texte principal ; « Parcourir » est souligné en bleu.</summary>
    public string Title
    {
        set
        {
            TitleText.Inlines.Clear();
            var parts = value.Split(new[] { "<b>", "</b>" }, StringSplitOptions.None);
            for (var i = 0; i < parts.Length; i++)
                TitleText.Inlines.Add(i % 2 == 1
                    ? new System.Windows.Documents.Run(parts[i]) { Foreground = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)), TextDecorations = TextDecorations.Underline }
                    : new System.Windows.Documents.Run(parts[i]));
        }
    }

    public string Hint { set => HintText.Text = value; }

    private void Highlight(bool on)
    {
        Back.Background = new SolidColorBrush(on ? Color.FromRgb(0xDB, 0xEA, 0xFE) : Color.FromRgb(0xF5, 0xF9, 0xFF));
        Dash.StrokeThickness = on ? 2.5 : 1.5;
    }

    private void Root_Click(object sender, MouseButtonEventArgs e) => Browse?.Invoke(this, new RoutedEventArgs());
}