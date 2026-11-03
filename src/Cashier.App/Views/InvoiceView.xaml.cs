using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cashier.App.Printing;
using Cashier.Core;
using Cashier.Core.Services;

namespace Cashier.App.Views;

public partial class InvoiceView : UserControl, IAppPage
{
    private readonly InvoiceKind _kind;
    private List<Invoice> _rows = new();

    public string PageTitle => _kind == InvoiceKind.Out ? "销项发票" : "进项发票";
    public string PageSubtitle => _kind == InvoiceKind.Out
        ? "对外开具的销项发票台账，支持登记、修改、作废与业务关联"
        : "供应商开具的进项发票台账，支持登记、修改、作废与业务关联";
    public string? Chip => _kind == InvoiceKind.Out ? "销项" : "进项";

    public InvoiceView(InvoiceKind kind)
    {
        InitializeComponent();
        _kind = kind;
        var today = DateTime.Today;
        DateFrom.SelectedDate = new DateTime(today.Year, 1, 1);
        DateTo.SelectedDate = new DateTime(today.Year, 12, 31);

        Loaded += (_, _) =>
        {
            LoadCombos();
            BuildColumns();
            Reload();
        };
    }

    public IEnumerable<UIElement> BuildActions()
    {
        var add = Ui.Btn("登记发票", Add_Click, "Btn.Primary");
        add.Margin = new Thickness(0, 0, 6, 0);
        var edit = Ui.Btn("修改", Edit_Click);
        var batch=Ui.Btn("批量导入",(_,_)=>
        {
            new Window {Title=(_kind==InvoiceKind.Out?"销项":"进项")+"发票批量导入",Owner=Window.GetWindow(this),Width=Math.Max(760,Math.Min(1150,SystemParameters.WorkArea.Width-24)),Height=Math.Max(500,Math.Min(740,SystemParameters.WorkArea.Height-24)),MinWidth=760,MinHeight=500,
                WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=Ui.Br("B.Bg"),Content=new ImportView(_kind)}.ShowDialog();
            Reload();
        });
        batch.Margin=new Thickness(0,0,6,0);batch.IsEnabled=Perm.Has(AppSession.Role,Perm.Import)&&Perm.Has(AppSession.Role,Perm.Invoice);
        batch.ToolTip="下载 Excel 模板，预览并批量登记当前类型发票";ToolTipService.SetShowOnDisabled(batch,true);
        edit.Margin = new Thickness(0, 0, 6, 0);
        var link = Ui.Btn("关联业务", Link_Click);
        link.Margin = new Thickness(0, 0, 6, 0);
        var voidBtn = Ui.Btn("作废", Void_Click);
        voidBtn.Margin = new Thickness(0, 0, 6, 0);
        var exp = Ui.Btn("导出", Export_Click, "Btn.Ghost");
        exp.Margin = new Thickness(0, 0, 6, 0);
        return new UIElement[] { add, batch, edit, link, voidBtn, exp, Ui.Btn("打印", Print_Click, "Btn.Ghost") };
    }

    private void LoadCombos()
    {
        var selected=(CmbPartner.SelectedItem as ComboItem)?.Id ?? 0;
        var items = new List<ComboItem> { new() { Id = 0, Name = "全部往来单位" } };
        items.AddRange(MasterDataService.ListPartners(_kind == InvoiceKind.Out ? "customer" : "supplier", true)
            .Select(p => new ComboItem { Id = p.Id, Name = p.Name }));
        CmbPartner.ItemsSource = items;
        CmbPartner.SelectedItem=items.FirstOrDefault(p=>p.Id==selected) ?? items[0];
    }

    private void BuildColumns()
    {
        Grid.Columns.Clear();
        Grid.Columns.Add(Ui.Col("登记日期", "RegDate", 92));
        Grid.Columns.Add(Ui.Col("发票号码", "InvoiceNo", 164));
        Grid.Columns.Add(Ui.Col("发票代码", "CodeText", 118));
        Grid.Columns.Add(Ui.Col(_kind == InvoiceKind.Out ? "客户" : "供应商", "PartnerName", 134));
        Grid.Columns.Add(Ui.Col("发票类型", "InvoiceType", 108));
        Grid.Columns.Add(Ui.Col("税率", "RateLabel", 54, true));
        Grid.Columns.Add(Ui.Col("含税金额", "GrossText", 110, true));
        Grid.Columns.Add(Ui.Col("税额", "TaxText", 96, true));
        Grid.Columns.Add(Ui.Col("已关联", "LinkedText", 96, true));
        Grid.Columns.Add(Ui.Col("状态", "StatusLabel", 66));
    }

    public void Refresh() => Reload();

    private void Reload()
    {
        try
        {
            var partnerId = (CmbPartner.SelectedItem as ComboItem)?.Id;
            var from = DateFrom.SelectedDate?.ToString("yyyy-MM-dd");
            var to = DateTo.SelectedDate?.ToString("yyyy-MM-dd");
            _rows = new InvoiceService().List(_kind, TxtKeyword.Text.Trim(),
                partnerId is > 0 ? partnerId : null, from, to, includeVoid: true);

            if (CmbStatus.SelectedIndex == 1) _rows = _rows.Where(i => i.Status == InvoiceStatus.Valid).ToList();
            if (CmbStatus.SelectedIndex == 2) _rows = _rows.Where(i => i.Status == InvoiceStatus.Void).ToList();

            Grid.ItemsSource = _rows;
            var valid = _rows.Where(i => i.Status == InvoiceStatus.Valid).ToList();
            MetricGross.Text = Money.Format(valid.Sum(i => i.GrossCents));
            MetricNet.Text = Money.Format(valid.Sum(i => i.NetCents));
            MetricTax.Text = Money.Format(valid.Sum(i => i.TaxCents));
            MetricGrossSub.Text = $"{valid.Count} 张有效发票";
            MetricNetSub.Text = "含税 ÷ (1 + 税率)";
            MetricTaxSub.Text = "含税 − 不含税";
            RangeText.Text = $"{from} 至 {to}";
            FootText.Text = $"共 {_rows.Count} 张（有效 {valid.Count}，作废 {_rows.Count - valid.Count}） · " +
                            $"价税合计 {Money.Format(valid.Sum(i => i.GrossCents))} · 已关联业务 {Money.Format(valid.Sum(i => i.LinkedCents))}";
        }
        catch (Exception ex) { Ui.Error(ex); }
    }

    private Invoice? Selected => Grid.SelectedItem as Invoice;

    private void Query_Click(object sender, RoutedEventArgs e) => Reload();
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var today = DateTime.Today;
        DateFrom.SelectedDate = new DateTime(today.Year, 1, 1);
        DateTo.SelectedDate = new DateTime(today.Year, 12, 31);
        CmbPartner.SelectedIndex = 0;
        CmbStatus.SelectedIndex = 0;
        TxtKeyword.Text = "";
        Reload();
    }
    private void TxtKeyword_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) Reload(); }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var win = new InvoiceWindow(_kind, null) { Owner = Window.GetWindow(this) };
        var saved=win.ShowDialog()==true;
        if(win.PartnerDirectoryChanged) LoadCombos();
        if(saved) Reload();
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        var inv = Selected;
        if (inv == null) { Ui.Info("请先选择一张发票。"); return; }
        var win = new InvoiceWindow(_kind, inv) { Owner = Window.GetWindow(this) };
        if (win.ShowDialog() == true) Reload();
    }

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Edit_Click(sender, e);

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        var inv = Selected;
        if (inv == null) { Ui.Info("请先选择一张发票。"); return; }
        if (inv.Status == InvoiceStatus.Void) { Ui.Info("已作废发票不能关联业务。"); return; }
        var win = new InvoiceLinkWindow(inv) { Owner = Window.GetWindow(this) };
        if (win.ShowDialog() == true) Reload();
    }

    private void Void_Click(object sender, RoutedEventArgs e)
    {
        var inv = Selected;
        if (inv == null) { Ui.Info("请先选择一张发票。"); return; }
        var reason = TextPromptWindow.Ask("作废发票", $"确定作废发票 {inv.InvoiceNo}？作废后不计入有效发票金额。", Window.GetWindow(this));
        if (reason == null) return;
        try
        {
            new InvoiceService().Void(inv.Id, reason);
            Reload();
        }
        catch (Exception ex) { Ui.Error(ex); }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        try { PrintHelper.ExportGrid(Grid, PageTitle, $"{DateFrom.SelectedDate:yyyy-MM-dd} 至 {DateTo.SelectedDate:yyyy-MM-dd}"); }
        catch (Exception ex) { Ui.Error(ex); }
    }

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        try { PrintHelper.PrintGrid(Grid, PageTitle, $"{DateFrom.SelectedDate:yyyy-MM-dd} 至 {DateTo.SelectedDate:yyyy-MM-dd}"); }
        catch (Exception ex) { Ui.Error(ex); }
    }
}
