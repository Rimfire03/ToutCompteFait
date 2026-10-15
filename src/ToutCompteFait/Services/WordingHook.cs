using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ToutCompteFait.Core;

namespace ToutCompteFait.Services;

/// <summary>Applique le vocabulaire de la version (<see cref="Wording"/>) à tous les textes affichés : libellés, boutons,
/// titres, en-têtes de colonnes, info-bulles. Évite de dupliquer chaque écran : le texte source reste celui de la version
/// historique, l'adaptation se fait à l'affichage. Sans effet hors Autoentreprise.</summary>
public static class WordingHook
{
    public static void Install()
    {
        if (!Editions.Current.IsBusiness) return;
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBlock, object> Watched = new();

    private static bool Has(string? s) => s is not null && s.IndexOf("xercice", StringComparison.OrdinalIgnoreCase) >= 0;

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            switch (sender)
            {
                case TextBlock tb:
                    AdaptTextBlock(tb);
                    break;
                case DataGrid grid:
                    foreach (var col in grid.Columns)
                        if (col.Header is string h && Has(h)) col.Header = Wording.Adapt(h);
                    break;
                case Window w:
                    if (Has(w.Title)) w.Title = Wording.Adapt(w.Title);
                    AdaptContent(w);
                    break;
                case ContentControl cc:
                    AdaptContent(cc);
                    break;
            }
            if (sender is FrameworkElement fe && fe.ToolTip is string tip && Has(tip)
                && !BindingOperations.IsDataBound(fe, FrameworkElement.ToolTipProperty))
                fe.ToolTip = Wording.Adapt(tip);
        }
        catch
        {
            // Cosmétique : un texte non adapté vaut mieux qu'un écran en erreur.
        }
    }

    private static void AdaptContent(ContentControl cc)
    {
        if (cc.Content is string s && Has(s) && !BindingOperations.IsDataBound(cc, ContentControl.ContentProperty))
            cc.Content = Wording.Adapt(s);
        if (cc is HeaderedContentControl hc && hc.Header is string h && Has(h) && !BindingOperations.IsDataBound(hc, HeaderedContentControl.HeaderProperty))
            hc.Header = Wording.Adapt(h);
    }

    private static void AdaptTextBlock(TextBlock tb)
    {
        // Texte composé de plusieurs « runs » (mise en forme) : laissé tel quel pour ne pas perdre la mise en forme.
        if (tb.Inlines.Count > 1) return;
        if (!BindingOperations.IsDataBound(tb, TextBlock.TextProperty) && Has(tb.Text)) tb.Text = Wording.Adapt(tb.Text);
        // Textes modifiés après l'affichage (bandeaux, titres de page, résumés).
        if (!Watched.TryAdd(tb, true)) return;
        var d = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        d?.AddValueChanged(tb, (_, _) =>
        {
            if (BindingOperations.IsDataBound(tb, TextBlock.TextProperty) || !Has(tb.Text)) return;
            tb.Text = Wording.Adapt(tb.Text);
        });
    }
}
