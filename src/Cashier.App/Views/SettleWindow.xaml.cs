using System.Windows;
using System.Windows.Controls;
using Cashier.Core;
using Cashier.Core.Services;

namespace Cashier.App.Views;

/// <summary>
/// 核销窗口：以选中的应收/应付单为主体，从该往来单位可用的收付款单据（回款/预收或付款/预付）
/// 中分配金额核销。1.1.2 修复：此前把业务单本身当作收付款来源，Core 必然拒绝，UI 核销完全不可用。
/// </summary>
public partial class SettleWindow : Window
{
    private readonly Voucher _biz;
    private readonly bool _isCustomer;
    private List<Voucher> _sources = new();

    /// <summary>实时查询业务单未结金额（_biz 上的 SettledCents 是打开窗口时的快照）。</summary>
    private long BizLeft() => new SettlementService().UnsettledOf(_biz.Id);

    public SettleWindow(Voucher biz)
    {
        InitializeComponent();
        _biz = biz;
        _isCustomer = biz.Kind == VoucherKind.Receivable;

        TitleText.Text = "核销 · " + biz.DocNo;
        KindText.Text = _isCustomer ? "应收单" : "应付单";
        SummaryText.Text = $"{biz.PartnerName} · 单据金额 {biz.AmountText} · 业务日期 {biz.BizDate} · 摘要：{biz.Summary}";

        BuildColumns();
        LoadData();
        TxtAmount.Text = BizLeft() > 0 ? Money.Format(BizLeft()) : "0.00";
    }

    private void BuildColumns()
    {
        GridBiz.Columns.Clear();
        GridBiz.Columns.Add(Ui.Col("日期", "BizDate", 92));
        GridBiz.Columns.Add(Ui.Col("单据编号", "DocNo", 118));
        GridBiz.Columns.Add(Ui.Col("业务性质", "NatureText", 92));
        GridBiz.Columns.Add(Ui.Star("摘要", "Summary"));
        GridBiz.Columns.Add(Ui.Col("单据金额", "AmountText", 100, true));
        GridBiz.Columns.Add(Ui.Col("可分配余额", "UnsettledText", 100, true));

        GridAlloc.Columns.Clear();
        GridAlloc.Columns.Add(Ui.Col("核销时间", "CreatedAt", 140));
        GridAlloc.Columns.Add(Ui.Col("收付款单据", "BizDocNo", 120));
        GridAlloc.Columns.Add(Ui.Star("业务摘要", "BizSummary"));
        GridAlloc.Columns.Add(Ui.Col("核销金额", "AmountText", 110, true));
        GridAlloc.Columns.Add(Ui.Col("操作人", "CreatedBy", 80));
    }

    private void LoadData()
    {
        _sources = new SettlementService().ListPaySources(_biz.PartnerId ?? 0, _isCustomer);
        GridBiz.ItemsSource = _sources;

        var left = BizLeft();
        LeftText.Text = (_isCustomer ? "未收金额" : "未付金额") + $" {Money.Format(left)}";
        TipText.Text = left <= 0 ? "该单据已全额结清。" : _sources.Count == 0 ? "该往来单位暂无可用的收付款单据。" : "";

        var allocs = new SettlementService().ListByBiz(_biz.Id).Select(a => new AllocationRow
        {
            Id = a.Id, CreatedAt = a.CreatedAt, BizDocNo = a.PayDocNo, BizSummary = a.BizSummary,
            AmountCents = a.AmountCents, CreatedBy = a.CreatedBy,
        }).ToList();
        GridAlloc.ItemsSource = allocs;
    }

    private void Allocate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var selected = GridBiz.SelectedItems.Cast<Voucher>().ToList();
            if (selected.Count == 0) throw new AppException("请先在列表中选择收付款单据（可多选）。");
            var amount = Money.Parse(TxtAmount.Text, "分配金额");
            var bizLeft = BizLeft();
            if (amount > bizLeft) throw new AppException($"分配金额超过该单据未结金额（{Money.Format(bizLeft)}）。");

            // 多选时按业务日期先后依次从各收付款单分配
            var remain = amount;
            foreach (var pay in selected.OrderBy(x => x.BizDate).ThenBy(x => x.DocNo))
            {
                if (remain <= 0) break;
                var take = Math.Min(remain, pay.UnsettledCents);
                if (take <= 0) continue;
                new SettlementService().Allocate(pay.Id, _biz.Id, take);
                remain -= take;
            }
            if (remain > 0) Ui.Info($"还有 {Money.Format(remain)} 未能分配（所选收付款单可分配余额不足），已作为未结金额保留。");
            LoadData();
            TxtAmount.Text = Money.Format(BizLeft());
        }
        catch (Exception ex) { Ui.Error(ex); LoadData(); }
    }

    private void Auto_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 以业务单为主体自动核销：按业务日期先后遍历该往来单位全部可用收付款单，直到本单结清。
            var done = new List<(string docNo, long amount)>();
            foreach (var pay in _sources.OrderBy(x => x.BizDate).ThenBy(x => x.DocNo))
            {
                var payLeft = pay.UnsettledCents;
                var bizLeft = BizLeft();
                if (payLeft <= 0 || bizLeft <= 0) continue;
                var take = Math.Min(payLeft, bizLeft);
                new SettlementService().Allocate(pay.Id, _biz.Id, take);
                done.Add((pay.DocNo, take));
            }
            if (done.Count == 0) Ui.Info("没有可用的收付款单据，或该单据已结清。");
            else Ui.Info($"已核销 {done.Count} 笔，合计 {Money.Format(done.Sum(x => x.amount))}。");
            LoadData();
            TxtAmount.Text = Money.Format(BizLeft());
        }
        catch (Exception ex) { Ui.Error(ex); LoadData(); }
    }

    private void Split_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var selected = GridBiz.SelectedItems.Cast<Voucher>().OrderBy(x => x.BizDate).ToList();
            if (selected.Count < 2) throw new AppException("平均分配需要选择至少 2 笔收付款单据。");
            var amount = Money.Parse(TxtAmount.Text, "分配金额");
            var each = amount / selected.Count;
            if (each <= 0) throw new AppException("分配金额过小，无法在所选单据间平均分配（每笔不足 0.01）。");

            // 第一轮：每笔给基准份额，整除的余数并入第一笔；份额受该单可分配余额封顶。
            // 第二轮：把因封顶未分出的余额顺次补给还有余量的单据，避免静默丢失。
            var remain = amount;
            var remainder = amount - each * selected.Count;
            var given = new Dictionary<long, long>();
            for (var pass = 0; pass < 2; pass++)
            {
                foreach (var pay in selected)
                {
                    if (remain <= 0) break;
                    var already = given.TryGetValue(pay.Id, out var g) ? g : 0;
                    var want = pass == 0
                        ? each + (already == 0 && remainder > 0 ? remainder : 0)
                        : remain; // 第二轮补齐
                    var take = Math.Min(want, Math.Min(pay.UnsettledCents - already, remain));
                    if (take <= 0) continue;
                    new SettlementService().Allocate(pay.Id, _biz.Id, take);
                    given[pay.Id] = already + take;
                    remain -= take;
                }
                if (pass == 0 && remain == 0) break;
            }
            if (remain > 0) Ui.Info($"还有 {Money.Format(remain)} 未能分配（所选收付款单可分配余额不足）。");
            LoadData();
            TxtAmount.Text = Money.Format(BizLeft());
        }
        catch (Exception ex) { Ui.Error(ex); LoadData(); }
    }

    private void Unallocate_Click(object sender, RoutedEventArgs e)
    {
        if (GridAlloc.SelectedItem is not AllocationRow row) { Ui.Info("请先选择一条核销明细。"); return; }
        var reason = TextPromptWindow.Ask("取消核销", "取消核销后该笔分配金额将回到未分配状态，请填写原因：", this);
        if (reason == null) return;
        try
        {
            new SettlementService().Unallocate(row.Id, reason);
            LoadData();
            TxtAmount.Text = Money.Format(BizLeft());
        }
        catch (Exception ex) { Ui.Error(ex); LoadData(); }
    }

    /// <summary>
    /// 1.1.3：关闭时必须回传 DialogResult，否则 LedgerView 的 <c>if (win.ShowDialog() == true) Reload()</c>
    /// 永远不成立，核销已落库但应收/应付列表仍显示"已核销 0.00 / 未结清"，容易引发重复核销。
    /// </summary>
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            DialogResult = true;   // 由 WPF 自动关闭窗口
        }
        catch (InvalidOperationException)
        {
            // 非 ShowDialog 打开（理论上不存在）时退化为普通关闭，不影响使用
            Close();
        }
    }
}

public class AllocationRow
{
    public long Id { get; init; }
    public string CreatedAt { get; init; } = "";
    public string BizDocNo { get; init; } = "";
    public string BizSummary { get; init; } = "";
    public long AmountCents { get; init; }
    public string AmountText => Money.Format(AmountCents);
    public string CreatedBy { get; init; } = "";
}
