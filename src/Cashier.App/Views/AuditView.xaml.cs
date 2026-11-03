using System.Windows;
using System.Windows.Controls;
using Cashier.Core;
using Cashier.Core.Services;

namespace Cashier.App.Views;

public partial class AuditView : UserControl, IAppPage
{
    public string PageTitle => "审核中心";
    public string PageSubtitle => "待审核与草稿单据集中处理，支持批量审核";
    public string? Chip => null;

    public AuditView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            BuildColumns();
            Reload();
        };
    }

    public IEnumerable<UIElement> BuildActions()
    {
        var refresh = Ui.Btn("刷新", (_, _) => Reload(), "Btn.Primary");
        refresh.Margin = new Thickness(0, 0, 6, 0);
        var exp = Ui.Btn("导出", (_, _) =>
        {
            try { Printing.PrintHelper.ExportGrid(Grid, "待审核单据", DateTime.Now.ToString("yyyy-MM-dd")); }
            catch (Exception ex) { Ui.Error(ex); }
        }, "Btn.Ghost");
        return new UIElement[] { refresh, exp };
    }

    private void BuildColumns()
    {
        Grid.Columns.Clear();
        Grid.Columns.Add(Ui.Col("日期", "BizDate", 92));
        Grid.Columns.Add(Ui.Col("单据编号", "DocNo", 118));
        Grid.Columns.Add(Ui.Col("类型", "KindText", 62));
        Grid.Columns.Add(Ui.Col("业务性质", "NatureText", 92));
        Grid.Columns.Add(Ui.Col("往来单位", "PartnerName", 150));
        Grid.Columns.Add(Ui.Star("摘要", "Summary"));
        Grid.Columns.Add(Ui.Col("金额", "AmountText", 118, true));
        Grid.Columns.Add(Ui.Col("状态", "StatusText", 74));
        Grid.Columns.Add(Ui.Col("制单人", "CreatedBy", 80));
        Grid.Columns.Add(Ui.Col("制单时间", "CreatedAt", 140));
    }

    public void Refresh() => Reload();

    private void Reload()
    {
        try
        {
            var q = new VoucherQuery
            {
                Keyword = TxtKeyword.Text.Trim(),
                Limit = 2000,
            };
            if (CmbKind.SelectedIndex > 0) q.Kind = (VoucherKind)(CmbKind.SelectedIndex - 1);

            var all = new VoucherService().Query(q)
                .Where(v => v.RefId == null && (v.Status == VoucherStatus.Draft || v.Status == VoucherStatus.Pending))
                .ToList();
            Grid.ItemsSource = all;
            FootText.Text = $"待处理 {all.Count} 张 · 合计 {Money.Format(all.Sum(v => v.AmountCents))}";
        }
        catch (Exception ex) { Ui.Error(ex); }
    }

    private void Query_Click(object sender, RoutedEventArgs e) => Reload();

    private void BatchAudit_Click(object sender, RoutedEventArgs e)
    {
        var list = Grid.SelectedItems.Cast<Voucher>().ToList();
        if (list.Count == 0) { Ui.Info("请先选择要审核的单据（可多选）。"); return; }
        if (!Ui.Confirm($"确定审核选中的 {list.Count} 张单据？合计 {Money.Format(list.Sum(v => v.AmountCents))}。\n审核后单据只读并计入正式账务。")) return;

        var svc = new VoucherService();
        var ok = 0;
        var errors = new List<string>();
        foreach (var v in list)
        {
            try
            {
                if (v.Status == VoucherStatus.Draft) svc.Submit(v.Id);
                svc.Audit(v.Id);
                ok++;
            }
            catch (Exception ex)
            {
                errors.Add($"{v.DocNo}：{ex.Message}");
            }
        }
        Reload();
        if (errors.Count == 0) Ui.Info($"已成功审核 {ok} 张单据。");
        else Ui.Info($"成功 {ok} 张，失败 {errors.Count} 张：\n" + string.Join("\n", errors.Take(10)), "部分未完成");
    }
}
