using System.IO;
using System.Windows;
using System.Windows.Input;
using Cashier.App.Printing;
using Cashier.Core;
using Cashier.Core.Services;

namespace Cashier.App.Views;

public partial class PeriodInvoiceCashDetailWindow : Window
{
    private readonly PeriodInvoiceCashDetail _detail;
    private readonly List<ReportResult> _reports;

    public PeriodInvoiceCashDetailWindow(long partnerId, string from, string to, InvoiceKind direction)
    {
        InitializeComponent();
        var service = new ReportService();
        _detail = service.GetPeriodInvoiceCashDetail(partnerId, from, to, direction);
        _reports = service.BuildPeriodInvoiceCashDetailReports(_detail);

        TitleText.Text = _detail.IsOut ? "期间开票与收款明细" : "期间收票与付款明细";
        Title = TitleText.Text;
        ChipText.Text = _detail.IsOut ? "销项" : "进项";
        PartnerText.Text = $"往来单位：{_detail.PartnerCode} {_detail.PartnerName}";
        SummaryText.Text = $"统计期间：{_detail.From} 至 {_detail.To} · 发票：蓝 {_detail.BlueCount} 张 / 红 {_detail.RedCount} 张 / 净 {_detail.NetCount} 张，"
            + $"含税 {Money.Format(_detail.GrossCents)}，不含税 {Money.Format(_detail.NetCents)}，税额 {Money.Format(_detail.TaxCents)}；"
            + $"{(_detail.IsOut ? "收款" : "付款")} {Money.Format(_detail.CashCents)}，票款差额 {Money.FormatSigned(_detail.DifferenceCents)}。";
        InvoiceSectionText.Text = $"共 {_detail.Invoices.Count} 张 · 含税合计 {Money.Format(_detail.GrossCents)}";
        CashSectionTitle.Text = _detail.IsOut ? "收款明细" : "付款明细";
        CashSectionText.Text = $"共 {_detail.Vouchers.Count} 笔 · 合计 {Money.Format(_detail.CashCents)}";

        InvoiceGrid.Columns.Add(Ui.Col("登记日期", nameof(Invoice.RegDate), 100));
        InvoiceGrid.Columns.Add(Ui.Col("发票号码", nameof(Invoice.InvoiceNo), 200));
        InvoiceGrid.Columns.Add(Ui.Col("发票类型", nameof(Invoice.InvoiceType), 160));
        InvoiceGrid.Columns.Add(Ui.Col("含税金额", nameof(Invoice.GrossText), 130, right: true));
        InvoiceGrid.Columns.Add(Ui.Col("不含税金额", nameof(Invoice.NetText), 130, right: true));
        InvoiceGrid.Columns.Add(Ui.Col("税额", nameof(Invoice.TaxText), 110, right: true));
        InvoiceGrid.ItemsSource = _detail.Invoices;

        CashGrid.Columns.Add(Ui.Col("业务日期", nameof(Voucher.BizDate), 100));
        CashGrid.Columns.Add(Ui.Col("单据编号", nameof(Voucher.DocNo), 150));
        CashGrid.Columns.Add(Ui.Col("业务性质", nameof(Voucher.NatureText), 120));
        CashGrid.Columns.Add(Ui.Col("金额", nameof(Voucher.AmountText), 130, right: true));
        CashGrid.Columns.Add(Ui.Star("摘要", nameof(Voucher.Summary)));
        CashGrid.ItemsSource = _detail.Vouchers;
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var invalid = Path.GetInvalidFileNameChars();
            var baseName = new string($"{_detail.PartnerName}_{(_detail.IsOut ? "销项" : "进项")}_{_detail.From}_{_detail.To}_明细"
                .Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            PrintHelper.ExportReports(baseName, _reports.ToArray());
        }
        catch (Exception ex) { Ui.Error(ex); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }
}
