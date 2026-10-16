using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using ToutCompteFait.Core;

namespace ToutCompteFait.Services;

/// <summary>Applique le vocabulaire de la version (<see cref="Wording"/>) à tous les textes affichés : libellés, boutons,
/// titres, en-têtes de colonnes, info-bulles. Évite de dupliquer chaque écran : le texte source reste celui de la version
/// historique, l'adaptation se fait à l'affichage. Sans effet hors Autoentreprise.
/// Principe : à chaque chargement d'un élément, puis après chaque mise en page d'une fenêtre (texte modifié par le code,
/// onglet ouvert, ligne affichée), le contenu de la fenêtre est parcouru et les textes concernés sont adaptés avant
/// l'affichage ; le parcours ne touche que les textes contenant le mot à remplacer.</summary>
public static class WordingHook
{
    private static readonly ConditionalWeakTable<Window, object> Watched = new();
    private static bool _pending;

    public static void Install()
    {
        if (!Editions.Current.IsBusiness) return;
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));
        // Textes modifiés par le code après le chargement (titres de page, bandeaux, résumés) : balayage périodique.
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) =>
        {
            try
            {
                foreach (var w in Application.Current.Windows.OfType<Window>().Where(w => w.IsVisible)) Walk(w);
            }
            catch { /* cosmétique */ }
        };
        timer.Start();
    }

    private static bool Has(string? s) => s is not null && s.IndexOf("xercice", StringComparison.OrdinalIgnoreCase) >= 0;

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not DependencyObject d) return;
            // Contenu qui vient d'être chargé : la fenêtre est parcourue une fois pour toute la salve de chargements.
            var window = Window.GetWindow(d);
            if (window is null) return;
            Watched.TryAdd(window, true);
            Schedule(window);
        }
        catch
        {
            // Cosmétique : un texte non adapté vaut mieux qu'un écran en erreur.
        }
    }

    /// <summary>Planifie un parcours de la fenêtre avant le prochain affichage (un seul à la fois).</summary>
    private static void Schedule(Window window)
    {
        if (_pending) return;
        _pending = true;
        window.Dispatcher.BeginInvoke(new Action(() =>
        {
            _pending = false;
            try { Walk(window); }
            catch { /* cosmétique */ }
        }), DispatcherPriority.DataBind);
    }

    private static void Walk(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        // Un élément est atteignable par l'arbre logique et par l'arbre visuel : un seul passage par élément et par parcours.
        var seen = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is not (FrameworkElement or FrameworkContentElement) || !seen.Add(node)) continue;
            Adapt(node);
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) stack.Push(child);
            if (node is Visual or System.Windows.Media.Media3D.Visual3D)
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) stack.Push(VisualTreeHelper.GetChild(node, i));
        }
    }

    private static void Adapt(DependencyObject node)
    {
        switch (node)
        {
            case TextBlock tb:
                // Plusieurs « runs » (mise en forme) : chacun est adapté séparément (voir Run).
                // (Inlines n'est lu qu'ensuite : y accéder invalide la mise en page et relancerait ce parcours.)
                if (Has(tb.Text) && !BindingOperations.IsDataBound(tb, TextBlock.TextProperty) && tb.Inlines.Count <= 1)
                    tb.Text = Wording.Adapt(tb.Text);
                break;
            case Run run:
                if (Has(run.Text) && !BindingOperations.IsDataBound(run, Run.TextProperty)
                    && !(run.Parent is TextBlock ptb && BindingOperations.IsDataBound(ptb, TextBlock.TextProperty)))
                    run.Text = Wording.Adapt(run.Text);
                break;
            case DataGrid grid:
                foreach (var col in grid.Columns)
                    if (col.Header is string h && Has(h)) col.Header = Wording.Adapt(h);
                break;
            case Window w:
                if (Has(w.Title)) w.Title = Wording.Adapt(w.Title);
                break;
        }
        if (node is ContentControl cc && cc.Content is string s && Has(s) && !BindingOperations.IsDataBound(cc, ContentControl.ContentProperty))
            cc.Content = Wording.Adapt(s);
        if (node is HeaderedContentControl hc && hc.Header is string hs && Has(hs) && !BindingOperations.IsDataBound(hc, HeaderedContentControl.HeaderProperty))
            hc.Header = Wording.Adapt(hs);
        if (node is FrameworkElement fe && fe.ToolTip is string tip && Has(tip) && !BindingOperations.IsDataBound(fe, FrameworkElement.ToolTipProperty))
            fe.ToolTip = Wording.Adapt(tip);
    }
}
