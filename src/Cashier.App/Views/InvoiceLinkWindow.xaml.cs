using System.Windows;
using System.Windows.Controls;
using Cashier.Core;
using Cashier.Core.Services;

namespace Cashier.App.Views;

public partial class InvoiceLinkWindow : Window
{
    private readonly Invoice _inv;

    public InvoiceLinkWindow(Invoice inv)
    {
        InitializeComponent();
        _inv = inv;
        TitleText.Text = "关联业务 · " + inv.InvoiceNo;
        KindText.Text = inv.KindLabel + (inv.GrossCents < 0 ? " · 红字" : "");

        GridBiz.Columns.Clear();
        GridBiz.Columns.Add(Ui.Col("日期", "BizDate", 92));
        GridBiz.Columns.Add(Ui.Col("单据编号", "DocNo", 118));
        GridBiz.Columns.Add(Ui.Star("摘要", "Summary"));
        GridBiz.Columns.Add(Ui.Col(inv.GrossCents < 0 ? "原票关联" : "单据金额", "AmountText", 110, true));
        GridBiz.Columns.Add(Ui.Col(inv.GrossCents < 0 ? "已冲红" : "已开票", "InvoiceLinkedText", 110, true));
        GridBiz.Columns.Add(Ui.Col(inv.GrossCents < 0 ? "可冲红" : "未开票", "UninvoicedText", 110, true));

        GridLinks.Columns.Clear();
        GridLinks.Columns.Add(Ui.Col("业务日期", "BizDate", 92));
        GridLinks.Columns.Add(Ui.Col("单据编号", "DocNo", 118));
        GridLinks.Columns.Add(Ui.Star("摘要", "Summary"));
        GridLinks.Columns.Add(Ui.Col("关联金额", "AmountText", 110, true));

        LoadData();
    }

    private void LoadData()
    {
        var fresh = new InvoiceService().Get(_inv.Id) ?? _inv;
        var left = Math.Abs(fresh.GrossCents - fresh.LinkedCents);
        InfoText.Text = $"{fresh.PartnerName} · 价税合计 {fresh.GrossText} · 已关联 {fresh.LinkedText} · 可关联余额 {Money.Format(left)}";
        TipText.Text = left <= 0 ? "该发票金额已全部关联。" : "";

        GridBiz.ItemsSource = new InvoiceService().InvoiceableVouchers(fresh.PartnerId, fresh.Kind,
            fresh.GrossCents < 0 ? fresh.OriginalInvoiceId : null);
        GridLinks.ItemsSource = new InvoiceService().Links(fresh.Id)
            .Select(l => new LinkRow { Id = l.linkId, BizDate = l.bizDate, DocNo = l.docNo, Summary = l.summary, AmountCents = l.amount })
            .ToList();
        TxtAmount.Text = left > 0 ? Money.Format(left) : "0.00";
    }

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (GridBiz.SelectedItem is not Voucher v) throw new AppException("请选择一笔业务单据。");
            var amount = Money.Parse(TxtAmount.Text, "关联金额");
            new InvoiceService().Link(_inv.Id, v.Id, amount);
            LoadData();
        }
        catch (Exception ex) { Ui.Error(ex); }
    }

    private void Unlink_Click(object sender, RoutedEventArgs e)
    {
        if (GridLinks.SelectedItem is not LinkRow row) { Ui.Info("请先选择一条关联记录。"); return; }
        var reason = TextPromptWindow.Ask("解除关联", "解除后该发票金额将回到可关联余额，请填写原因：", this);
        if (reason == null) return;
        try
        {
            new InvoiceService().Unlink(row.Id, reason);
            LoadData();
        }
        catch (Exception ex) { Ui.Error(ex); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

public class LinkRow
{
    public long Id { get; init; }
    public string BizDate { get; init; } = "";
    public string DocNo { get; init; } = "";
    public string Summary { get; init; } = "";
    public long AmountCents { get; init; }
    public string AmountText => Money.Format(AmountCents);
}
