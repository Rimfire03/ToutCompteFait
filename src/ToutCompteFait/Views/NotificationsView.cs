using System.Windows.Controls;
using TomLine.UI.Wpf.Controls;
using ToutCompteFait.Core;

namespace ToutCompteFait.Views;

/// <summary>Centre de notifications : toutes les alertes en cours (déclarations URSSAF, écritures à ventiler, exercice à
/// clôturer), consultables à tout moment. Chaque carte renvoie vers l'écran concerné. Lecture seule. L'affichage est
/// celui du cœur (<see cref="NotificationCenterView"/>) ; seule la liste vient de l'application.</summary>
public sealed class NotificationsView : UserControl, IPage
{
    private readonly NotificationCenterView _center;

    public NotificationsView(FiscalYear year, Action<string> open)
    {
        _center = new NotificationCenterView(
            () => App.Compta.BuildNotifications(DateOnly.FromDateTime(DateTime.Today), year.Id), open,
            "Les alertes en cours, à retrouver à tout moment : échéances de déclaration, écritures à ventiler, exercice à clôturer, ainsi que les informations à venir (prochaine déclaration). " +
            "Les alertes disparaissent d'elles-mêmes dès que la situation est réglée.");
        Content = _center;
    }

    public void Reload() => _center.Reload();
}
