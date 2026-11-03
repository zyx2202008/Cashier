using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cashier.App.Printing;
using Cashier.Core;
using Cashier.Core.Services;

namespace Cashier.App.Views;

public partial class LedgerView : UserControl, IAppPage
{
    private readonly VoucherKind? _kind;
    private readonly VoucherStatus? _status;
    private List<Voucher> _rows = new();

    public string PageTitle { get; }
    public string PageSubtitle { get; }
    public string? Chip => _kind == null ? "全部" : KindInfo.Label(_kind.Value);

    public LedgerView(VoucherKind? kind, VoucherStatus? status, string title, string subtitle)
    {
        InitializeComponent();
        _kind = kind;
        _status = status;
        PageTitle = title;
        PageSubtitle = subtitle;

        var today = DateTime.Today;
        DateFrom.SelectedDate = new DateTime(today.Year, today.Month, 1);
        DateTo.SelectedDate = new DateTime(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));
        if (_status.HasValue) CmbStatus.SelectedIndex = (int)_status.Value + 1;

        Loaded += (_, _) =>
        {
            LoadCombos();
            BuildColumns();
            Reload();
        };
    }

    public IEnumerable<UIElement> BuildActions()
    {
        // 1.1.2：按权限收敛操作按钮，避免只读账号点击后才收到报错弹窗
        var list = new List<UIElement>();
        if (Perm.Has(AppSession.Role, Perm.CreateVoucher) &&
            _kind is null or VoucherKind.Income or VoucherKind.Expense or VoucherKind.Receivable or VoucherKind.Payable or VoucherKind.Transfer)
        {
            var add = Ui.Btn(_kind == null ? "新增单据" : "新增" + KindInfo.Label(_kind.Value), Add_Click, "Btn.Primary");
            add.Margin = new Thickness(0, 0, 6, 0);
            list.Add(add);
        }
        if (Perm.Has(AppSession.Role, Perm.CreateVoucher)) list.Add(Mk("编辑", Edit_Click));
        if (Perm.Has(AppSession.Role, Perm.AuditVoucher)) { list.Add(Mk("审核", Audit_Click)); list.Add(Mk("反审核", Unaudit_Click)); }
        if (Perm.Has(AppSession.Role, Perm.VoidVoucher)) list.Add(Mk("作废", Void_Click));
        if (_kind is VoucherKind.Receivable or VoucherKind.Payable && Perm.Has(AppSession.Role, Perm.Settle)) list.Add(Mk("核销", Settle_Click));
        list.Add(Mk("导出", Export_Click, "Btn.Ghost"));
        list.Add(Mk("打印", Print_Click, "Btn.Ghost"));
        return list;
    }

    private static Button Mk(string text, RoutedEventHandler h, string? style = null)
    {
        var b = Ui.Btn(text, h, style);
        b.Margin = new Thickness(0, 0, 6, 0);
        return b;
    }

    private void LoadCombos()
    {
        var acc = new List<ComboItem> { new() { Id = 0, Name = "全部账户" } };
        acc.AddRange(MasterDataService.ListAccounts(true).Select(a => new ComboItem { Id = a.Id, Name = a.Name }));
        CmbAccount.ItemsSource = acc;
        CmbAccount.SelectedIndex = 0;

        var partners = new List<ComboItem> { new() { Id = 0, Name = "全部往来单位" } };
        partners.AddRange(MasterDataService.ListPartners(null, true).Select(p => new ComboItem { Id = p.Id, Name = p.Name }));
        CmbPartner.ItemsSource = partners;
        CmbPartner.SelectedIndex = 0;

        BoxAccount.Visibility = _kind is null or VoucherKind.Income or VoucherKind.Expense or VoucherKind.Transfer
            ? Visibility.Visible : Visibility.Collapsed;
        BoxPartner.Visibility = _kind == VoucherKind.Transfer ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BuildColumns()
    {
        var g = Grid.Columns;
        g.Clear();
        g.Add(Ui.Col("日期", "BizDate", 92));
        g.Add(Ui.Col("单据编号", "DocNo", 118));

        if (_kind == VoucherKind.Transfer)
        {
            g.Add(Ui.Col("转出账户", "AccountName", 150));
            g.Add(Ui.Col("转入账户", "ToAccountName", 150));
            g.Add(Ui.Star("摘要", "Summary"));
            g.Add(Ui.Col("金额", "AmountText", 120, true));
            g.Add(Ui.Col("手续费", "FeeText", 90, true));
        }
        else if (_kind is VoucherKind.Receivable or VoucherKind.Payable)
        {
            g.Add(Ui.Col("往来单位", "PartnerName", 160));
            g.Add(Ui.Col("项目", "ProjectName", 130));
            g.Add(Ui.Star("摘要", "Summary"));
            g.Add(Ui.Col("金额", "AmountText", 118, true));
            g.Add(Ui.Col("已核销", "SettledText", 110, true));
            g.Add(Ui.Col("未结", "UnsettledText", 110, true));
            g.Add(Ui.Col("结清状态", "SettlementStatusText", 86));
        }
        else
        {
            g.Add(Ui.Col("类型", "KindText", 60));
            g.Add(Ui.Col("业务性质", "NatureText", 92));
            g.Add(Ui.Col("账户", "AccountName", 130));
            g.Add(Ui.Col("往来单位", "PartnerName", 140));
            g.Add(Ui.Star("摘要", "Summary"));
            g.Add(Ui.Col("收入", "InText", 108, true, "B.Pos"));
            g.Add(Ui.Col("支出", "OutText", 108, true, "B.Neg"));
        }
        g.Add(Ui.Col("状态", "StatusText", 74));
    }

    public void Refresh() => Reload();

    private void Reload()
    {
        try
        {
            var q = new VoucherQuery
            {
                Kind = _kind,
                From = DateFrom.SelectedDate?.ToString("yyyy-MM-dd"),
                To = DateTo.SelectedDate?.ToString("yyyy-MM-dd"),
                Keyword = TxtKeyword.Text.Trim(),
                Limit = 5000,
            };
            if (CmbStatus.SelectedIndex > 0) q.Status = (VoucherStatus)(CmbStatus.SelectedIndex - 1);
            if (CmbAccount.SelectedItem is ComboItem a && a.Id > 0) q.AccountId = a.Id;
            if (CmbPartner.SelectedItem is ComboItem p && p.Id > 0) q.PartnerId = p.Id;

            _rows = new VoucherService().Query(q);
            // 收支明细为资金口径：应收/应付/转账在各自页面查看
            if (_kind == null) _rows = _rows.Where(v => v.Kind is VoucherKind.Income or VoucherKind.Expense).ToList();
            Grid.ItemsSource = _rows;

            var valid = _rows.Where(v => v.Status == VoucherStatus.Audited).ToList();
            if (_kind is VoucherKind.Receivable or VoucherKind.Payable)
            {
                var label = _kind == VoucherKind.Receivable ? "应收" : "应付";
                var total = valid.Sum(v => v.AmountCents);
                var settled = valid.Sum(v => v.SettledCents);
                MetricInLabel.Text = label + "合计";
                MetricOutLabel.Text = "已核销";
                MetricNetLabel.Text = "未" + (_kind == VoucherKind.Receivable ? "收" : "付") + "金额";
                MetricIn.Text = Money.Format(total);
                MetricOut.Text = Money.Format(settled);
                MetricNet.Text = Money.Format(total - settled);
                MetricInSub.Text = $"{valid.Count} 张已审核";
                MetricOutSub.Text = "来自核销记录";
                NetSubText.Text = "单据金额 − 已核销";
            }
            else if (_kind == VoucherKind.Transfer)
            {
                MetricInLabel.Text = "转账笔数";
                MetricOutLabel.Text = "转账金额";
                MetricNetLabel.Text = "手续费";
                MetricIn.Text = valid.Count.ToString();
                MetricOut.Text = Money.Format(valid.Sum(v => v.AmountCents));
                MetricNet.Text = Money.Format(valid.Sum(v => v.FeeCents));
                MetricInSub.Text = "已审核";
                MetricOutSub.Text = "账户间调拨";
                NetSubText.Text = "单独生成支出单据";
            }
            else
            {
                var inflow = valid.Where(v => v.Kind == VoucherKind.Income).Sum(v => v.AmountCents);
                var outflow = valid.Where(v => v.Kind == VoucherKind.Expense).Sum(v => v.AmountCents);
                MetricIn.Text = Money.Format(inflow);
                MetricOut.Text = Money.Format(outflow);
                MetricNet.Text = Money.FormatSigned(inflow - outflow);
                MetricInSub.Text = $"{valid.Count(v => v.Kind == VoucherKind.Income)} 笔已审核";
                MetricOutSub.Text = $"{valid.Count(v => v.Kind == VoucherKind.Expense)} 笔已审核";
            }
            RangeText.Text = $"{q.From} 至 {q.To}";

            FootCount.Text = $"共 {_rows.Count} 条记录（含待审核与已作废）";
            var sumIn = _rows.Where(v => v.Kind == VoucherKind.Income).Sum(v => v.AmountCents);
            var sumOut = _rows.Where(v => v.Kind == VoucherKind.Expense).Sum(v => v.AmountCents);
            FootSum.Text = _kind is VoucherKind.Receivable or VoucherKind.Payable
                ? $"合计 {Money.Format(_rows.Sum(v => v.AmountCents))} · 未结 {Money.Format(_rows.Sum(v => v.UnsettledCents))}"
                : $"收入合计 {Money.Format(sumIn)}   支出合计 {Money.Format(sumOut)}   净额 {Money.FormatSigned(sumIn - sumOut)}";
        }
        catch (Exception ex)
        {
            Ui.Error(ex);
        }
    }

    private Voucher? Selected => Grid.SelectedItem as Voucher;

    private void Query_Click(object sender, RoutedEventArgs e) => Reload();
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var today = DateTime.Today;
        DateFrom.SelectedDate = new DateTime(today.Year, today.Month, 1);
        DateTo.SelectedDate = new DateTime(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));
        CmbAccount.SelectedIndex = 0;
        CmbPartner.SelectedIndex = 0;
        CmbStatus.SelectedIndex = 0;
        TxtKeyword.Text = "";
        Reload();
    }

    private void TxtKeyword_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Reload();
    }

    private void Quick_Click(object sender, RoutedEventArgs e)
    {
        var tag = (sender as Button)?.Tag?.ToString();
        var today = DateTime.Today;
        switch (tag)
        {
            case "month":
                DateFrom.SelectedDate = new DateTime(today.Year, today.Month, 1);
                DateTo.SelectedDate = new DateTime(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));
                break;
            case "last":
                var last = today.AddMonths(-1);
                DateFrom.SelectedDate = new DateTime(last.Year, last.Month, 1);
                DateTo.SelectedDate = new DateTime(last.Year, last.Month, DateTime.DaysInMonth(last.Year, last.Month));
                break;
            case "quarter":
                var q = (today.Month - 1) / 3 * 3 + 1;
                DateFrom.SelectedDate = new DateTime(today.Year, q, 1);
                DateTo.SelectedDate = new DateTime(today.Year, q + 2, DateTime.DaysInMonth(today.Year, q + 2));
                break;
            case "year":
                DateFrom.SelectedDate = new DateTime(today.Year, 1, 1);
                DateTo.SelectedDate = new DateTime(today.Year, 12, 31);
                break;
        }
        Reload();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var kind = _kind ?? VoucherKind.Income;
        var win = new VoucherWindow(kind, null) { Owner = Window.GetWindow(this) };
        if (win.ShowDialog() == true) Reload();
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        var v = Selected;
        if (v == null) { Ui.Info("请先选择一条记录。"); return; }
        var win = new VoucherWindow(v.Kind, v) { Owner = Window.GetWindow(this) };
        if (win.ShowDialog() == true) Reload();
    }

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Edit_Click(sender, e);

    private void Audit_Click(object sender, RoutedEventArgs e)
    {
        var v = Selected;
        if (v == null) { Ui.Info("请先选择一条记录。"); return; }
        if (!Ui.Confirm($"确定审核单据 {v.DocNo}（{v.AmountText}）？\n审核后单据将只读并计入正式账务。")) return;
        try
        {
            new VoucherService().Audit(v.Id);
            Reload();
            if (v.Kind is VoucherKind.Receivable or VoucherKind.Payable && v.PartnerId is long partnerId)
            {
                var invoiceKind = v.Kind == VoucherKind.Receivable ? InvoiceKind.Out : InvoiceKind.In;
                var pending = new InvoiceService().List(invoiceKind, null, partnerId, null, null, false)
                    .Count(i => Math.Abs(i.GrossCents) > Math.Abs(i.LinkedCents));
                if (pending > 0)
                    Ui.Info($"单据已审核。该单位有 {pending} 张发票尚未完全关联业务，请到发票列表核对并手工关联。");
            }
        }
        catch (Exception ex) { Ui.Error(ex); }
    }

    private void Unaudit_Click(object sender, RoutedEventArgs e)
    {
        var v = Selected;
        if (v == null) { Ui.Info("请先选择一条记录。"); return; }
        var reason = TextPromptWindow.Ask("反审核原因", $"单据 {v.DocNo} 反审核后不计入正式账务，请填写原因：", Window.GetWindow(this));
        if (reason == null) return;
        try
        {
            new VoucherService().Unaudit(v.Id, reason);
            Reload();
        }
        catch (Exception ex) { Ui.Error(ex); }
    }

    private void Void_Click(object sender, RoutedEventArgs e)
    {
        var v = Selected;
        if (v == null) { Ui.Info("请先选择一条记录。"); return; }
        var reason = TextPromptWindow.Ask("作废原因", $"确定作废单据 {v.DocNo}？作废后保留记录但不计入账务。", Window.GetWindow(this));
        if (reason == null) return;
        try
        {
            new VoucherService().Void(v.Id, reason);
            Reload();
        }
        catch (Exception ex) { Ui.Error(ex); }
    }

    private void Settle_Click(object sender, RoutedEventArgs e)
    {
        var v = Selected;
        if (v == null) { Ui.Info("请先选择一条记录。"); return; }
        if (v.Status != VoucherStatus.Audited) { Ui.Info("只有已审核单据可以核销。"); return; }
        var win = new SettleWindow(v) { Owner = Window.GetWindow(this) };
        // 1.1.3：核销窗口无论用"关闭"按钮还是标题栏 X 退出，只要可能发生过分配就要刷新列表，
        // 否则会出现"已落库但界面仍显示未结清"的假象（旧实现依赖 ShowDialog()==true，实际永假）。
        win.ShowDialog();
        Reload();
    }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var sub = $"{PageTitle} · {DateFrom.SelectedDate:yyyy-MM-dd} 至 {DateTo.SelectedDate:yyyy-MM-dd}";
            PrintHelper.ExportGrid(Grid, PageTitle, sub);
        }
        catch (Exception ex) { Ui.Error(ex); }
    }

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var sub = $"{DateFrom.SelectedDate:yyyy-MM-dd} 至 {DateTo.SelectedDate:yyyy-MM-dd} · 金额单位：元";
            PrintHelper.PrintGrid(Grid, PageTitle, sub);
        }
        catch (Exception ex) { Ui.Error(ex); }
    }
}
