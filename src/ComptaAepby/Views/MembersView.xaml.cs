using System.Windows.Controls;
using ComptaAepby.Core;

namespace ComptaAepby.Views;

public partial class MembersView : UserControl, IPage
{
    private readonly FiscalYear _year;

    public MembersView(FiscalYear year)
    {
        InitializeComponent();
        _year = year;
        TitleText.Text = $"Remboursements aux membres — exercice {year.Label}";
        Reload();
    }

    public void Reload()
    {
        var c = App.Compta;
        var cats = c.Categories(_year.Id).ToDictionary(x => x.Id);
        var refunds = c.Entries(_year.Id).Where(r => r.Entry.Kind == EntryKind.MemberRefund).ToList();
        Detail.ItemsSource = refunds.Select(r => new
        {
            Date = r.Entry.Date.ToString("dd/MM/yyyy"),
            Member = r.Entry.MemberName,
            r.Entry.Label,
            Categories = string.Join(", ", r.Allocations.Select(a => cats[a.CategoryId].Name)),
            Amount = Money.Format(r.Entry.AmountCents)
        }).ToList();
        ByMember.ItemsSource = refunds
            .GroupBy(r => (r.Entry.MemberName ?? "").Trim(), StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new { Member = g.Key, Count = g.Count(), Total = Money.Format(g.Sum(r => r.Entry.AmountCents)) })
            .OrderBy(x => x.Member).ToList();
    }
}
