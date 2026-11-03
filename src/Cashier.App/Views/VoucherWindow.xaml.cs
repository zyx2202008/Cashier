using System.Windows;
using System.Windows.Controls;
using Cashier.Core;
using Cashier.Core.Services;

namespace Cashier.App.Views;

public partial class VoucherWindow : Window
{
    private readonly VoucherKind _kind;
    private readonly Voucher? _origin;
    private readonly bool _readOnly;
    private List<Partner> _partners = new();

    public VoucherWindow(VoucherKind kind, Voucher? voucher)
    {
        InitializeComponent();
        _kind = kind;
        _origin = voucher;
        _readOnly = voucher?.Status is VoucherStatus.Audited or VoucherStatus.Void;

        TitleText.Text = (voucher == null ? "新增" : _readOnly ? "查看" : "编辑") + KindInfo.Label(kind);
        Title = TitleText.Text;
        DocNoText.Text = voucher == null ? "单据编号保存后自动生成" : voucher.DocNo;
        DpDate.SelectedDate = voucher != null ? DateTime.Parse(voucher.BizDate) : DateTime.Today;

        LoadStaticCombos();
        LoadNatures();

        if (voucher != null)
        {
            TxtAmount.Text = Money.Format(voucher.AmountCents);
            CmbAccount.SelectedValue = voucher.AccountId;
            CmbToAccount.SelectedValue = voucher.ToAccountId;
            CmbPartner.SelectedValue = voucher.PartnerId;
            CmbProject.SelectedValue = voucher.ProjectId;
            CmbDept.SelectedValue = voucher.DeptId;
            CmbHandler.SelectedValue = voucher.Handler;
            CmbSubject.SelectedValue = voucher.SubjectId;
            TxtSummary.Text = voucher.Summary;
            TxtNote.Text = voucher.Note;
            TxtFee.Text = voucher.FeeCents > 0 ? Money.Format(voucher.FeeCents) : "";
            ChkRevenue.IsChecked = voucher.CountAsRevenue;
            ChkCost.IsChecked = voucher.CountAsCost;
            ChkInvoiceable.IsChecked = voucher.Invoiceable;
            if (voucher.Kind is VoucherKind.Income or VoucherKind.Expense)
                SelectNature(voucher.Nature);
        }
        else
        {
            ChkRevenue.IsChecked = kind == VoucherKind.Receivable;
            ChkCost.IsChecked = kind == VoucherKind.Payable;
        }

        ApplyKindVisibility();
        if (voucher != null) CmbPartner.SelectedValue = voucher.PartnerId ?? 0;
        ApplyReadOnly();
        // 1.1.2：草稿/待审核单据提供删除入口（此前按钮永远隐藏，草稿只能作废）
        if (!_readOnly && voucher != null && voucher.Status is VoucherStatus.Draft or VoucherStatus.Pending
            && Perm.Has(AppSession.Role, Perm.CreateVoucher))
        {
            BtnDelete.Visibility = Visibility.Visible;
        }
        UpdateUpper();
        TxtAmount.Focus();
    }

    private void LoadStaticCombos()
    {
        var accounts = MasterDataService.ListAccounts(true);
        CmbAccount.ItemsSource = accounts.Where(a => a.IsActive || a.Id == _origin?.AccountId).Select(a => new ComboItem { Id = a.Id, Name = a.Name }).ToList();
        CmbToAccount.ItemsSource = accounts.Where(a => a.IsActive || a.Id == _origin?.ToAccountId).Select(a => new ComboItem { Id = a.Id, Name = a.Name }).ToList();
        CmbProject.ItemsSource = new[] { new ComboItem { Id = 0, Name = "（不指定）" } }
            .Concat(MasterDataService.ListProjects(true).Where(p => p.IsActive || p.Id == _origin?.ProjectId).Select(p => new ComboItem { Id = p.Id, Name = p.Name })).ToList();
        CmbDept.ItemsSource = new[] { new ComboItem { Id = 0, Name = "（不指定）" } }
            .Concat(MasterDataService.ListDepts(true).Where(d => d.IsActive || d.Id == _origin?.DeptId).Select(d => new ComboItem { Id = d.Id, Name = d.Name })).ToList();
        CmbHandler.ItemsSource = new[] { new ComboItem { Id = 0, Name = "（不指定）" } }
            .Concat(MasterDataService.ListHandlers(true).Where(h => h.IsActive || h.Name == _origin?.Handler).Select(h => new ComboItem { Id = h.Id, Name = h.Name })).ToList();
        CmbSubject.ItemsSource = new[] { new ComboItem { Id = 0, Name = "（不指定）" } }
            .Concat(MasterDataService.ListSubjects(null, true).Where(s => s.IsActive || s.Id == _origin?.SubjectId)
                .Where(s => _kind is VoucherKind.Income or VoucherKind.Receivable ? s.Direction == "in" : s.Direction == "out")
                .Select(s => new ComboItem { Id = s.Id, Name = $"{s.Code} {s.Name}" })).ToList();
        CmbAccount.SelectedIndex = 0;
        CmbToAccount.SelectedIndex = CmbToAccount.Items.Count > 1 ? 1 : 0;
        CmbProject.SelectedIndex = 0;
        CmbDept.SelectedIndex = 0;
        CmbHandler.SelectedIndex = 0;
        CmbSubject.SelectedIndex = 0;
    }

    private void LoadNatures()
    {
        var list = new List<(VoucherNature nature, string name)>();
        if (_kind == VoucherKind.Income)
            list.AddRange(new[]
            {
                (VoucherNature.SaleCash, "现收收入（计入收入）"),
                (VoucherNature.CustomerReceipt, "客户回款（冲减应收）"),
                (VoucherNature.CustomerPrepay, "客户预收（形成预收）"),
                (VoucherNature.SupplierRefund, "供应商退回（冲减应付）"),
                (VoucherNature.OtherIn, "其他收入（计入收入）"),
            });
        else if (_kind == VoucherKind.Expense)
            list.AddRange(new[]
            {
                (VoucherNature.PurchaseCash, "现付费用（计入成本费用）"),
                (VoucherNature.SupplierPayment, "供应商付款（冲减应付）"),
                (VoucherNature.SupplierPrepay, "供应商预付（形成预付）"),
                (VoucherNature.CustomerRefund, "退还客户（增加应收）"),
                (VoucherNature.OtherOut, "其他支出（计入成本费用）"),
            });
        CmbNature.ItemsSource = list.Select(x => new NatureItem { Nature = x.nature, Name = x.name }).ToList();
        if (CmbNature.Items.Count > 0) CmbNature.SelectedIndex = 0;
    }

    private void SelectNature(VoucherNature n)
    {
        var items = CmbNature.ItemsSource as List<NatureItem>;
        var hit = items?.FirstOrDefault(x => x.Nature == n);
        if (hit != null) CmbNature.SelectedItem = hit;
    }

    private void ApplyKindVisibility()
    {
        var isTransfer = _kind == VoucherKind.Transfer;
        var isAr = _kind is VoucherKind.Receivable or VoucherKind.Payable;
        var isCash = _kind is VoucherKind.Income or VoucherKind.Expense;

        BoxNature.Visibility = isCash ? Visibility.Visible : Visibility.Collapsed;
        BoxAccount.Visibility = isCash || isTransfer ? Visibility.Visible : Visibility.Collapsed;
        BoxToAccount.Visibility = isTransfer ? Visibility.Visible : Visibility.Collapsed;
        BoxFee.Visibility = isTransfer ? Visibility.Visible : Visibility.Collapsed;
        BoxPartner.Visibility = isTransfer ? Visibility.Collapsed : Visibility.Visible;
        LblAccount.Text = _kind == VoucherKind.Expense ? "付款账户 *" : "入账账户 *";
        LblPartner.Text = _kind == VoucherKind.Receivable ? "客户 *" : _kind == VoucherKind.Payable ? "供应商 *" : "往来单位";

        ChkRevenue.Visibility = _kind == VoucherKind.Receivable ? Visibility.Visible : Visibility.Collapsed;
        ChkCost.Visibility = _kind == VoucherKind.Payable ? Visibility.Visible : Visibility.Collapsed;
        ChkInvoiceable.Visibility = isAr || isCash ? Visibility.Visible : Visibility.Collapsed;

        if (isTransfer)
        {
            BoxFee.Visibility = Visibility.Visible;
            Grid.SetColumn(BoxFee, 1);
        }

        ReloadPartners();
    }

    private void ReloadPartners()
    {
        var kind = "customer";
        if (_kind == VoucherKind.Payable) kind = "supplier";
        else if (_kind == VoucherKind.Expense)
        {
            var nat = (CmbNature.SelectedItem as NatureItem)?.Nature ?? VoucherNature.PurchaseCash;
            kind = nat is VoucherNature.SupplierPayment or VoucherNature.SupplierPrepay ? "supplier" : "customer";
        }
        else if (_kind == VoucherKind.Income)
        {
            var nat = (CmbNature.SelectedItem as NatureItem)?.Nature ?? VoucherNature.SaleCash;
            kind = nat == VoucherNature.SupplierRefund ? "supplier" : "customer";
        }
        else if (_kind == VoucherKind.Receivable) kind = "customer";

        var prev = CmbPartner.SelectedValue;
        _partners = MasterDataService.ListPartners(null, true)
            .Where(p => (p.IsActive && (p.Kind == kind || p.Kind == "both")) || p.Id == _origin?.PartnerId).ToList();
        var items = new List<ComboItem> { new() { Id = 0, Name = "（不指定）" } };
        items.AddRange(_partners.Select(p => new ComboItem { Id = p.Id, Name = p.Name }));
        CmbPartner.ItemsSource = items;
        CmbPartner.SelectedValue = prev;
        if (CmbPartner.SelectedIndex < 0) CmbPartner.SelectedIndex = 0;
    }

    private void CmbNature_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        ReloadPartners();
    }

    private void ApplyReadOnly()
    {
        if (!_readOnly) return;
        StatusChip.Background = Ui.Br("B.Header");
        StatusChip.BorderBrush = Ui.Br("B.Line");
        StatusText.Text = _origin!.StatusText;
        StatusText.Foreground = Ui.Br("B.Ink2");
        foreach (var c in FindAll<Control>(this)) c.IsEnabled = false;
        BtnDelete.Visibility = Visibility.Collapsed;
        HintText.Text = "该单据已" + _origin.StatusText + "，内容为只读。如需修改请先反审核。";
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var c in FindAll<T>(child)) yield return c;
        }
    }

    private void TxtAmount_TextChanged(object sender, TextChangedEventArgs e) => UpdateUpper();

    private void UpdateUpper()
    {
        AmountErr.Visibility = Visibility.Collapsed;
        AmountBox.BorderBrush = Ui.Br("B.Line");
        var txt = TxtAmount.Text.Replace(",", "").Trim();
        if (decimal.TryParse(txt, out var d) && d > 0)
        {
            UpperText.Text = RmbUpper.Convert(d);
        }
        else UpperText.Text = "";
    }

    private void TxtSummary_TextChanged(object sender, TextChangedEventArgs e)
        => SummaryCount.Text = $"{TxtSummary.Text.Length} / 200";

    private Voucher BuildVoucher()
    {
        var amountText = TxtAmount.Text.Replace(",", "").Trim();
        if (!decimal.TryParse(amountText, out var amount) || amount <= 0 || decimal.Round(amount, 2) != amount)
        {
            AmountErr.Visibility = Visibility.Visible;
            AmountBox.BorderBrush = Ui.Br("B.Danger");
            throw new AppException("请填写正确的金额（大于 0，最多两位小数）。");
        }
        if (TxtSummary.Text.Trim().Length == 0) throw new AppException("请填写摘要。");

        var v = new Voucher
        {
            Id = _origin?.Id ?? 0,
            Revision = _origin?.Revision ?? 0,
            Kind = _kind,
            BizDate = (DpDate.SelectedDate ?? DateTime.Today).ToString("yyyy-MM-dd"),
            AmountCents = Money.ToCents(amount),
            Summary = TxtSummary.Text.Trim(),
            Note = string.IsNullOrWhiteSpace(TxtNote.Text) ? null : TxtNote.Text.Trim(),
            CountAsRevenue = ChkRevenue.IsChecked == true,
            CountAsCost = ChkCost.IsChecked == true,
            Invoiceable = ChkInvoiceable.IsChecked != false,
            Nature = (CmbNature.SelectedItem as NatureItem)?.Nature ?? VoucherNature.None,
        };

        if (_kind is VoucherKind.Income or VoucherKind.Expense && v.Nature == VoucherNature.None)
            throw new AppException("请选择业务性质。");

        if (_kind is VoucherKind.Income or VoucherKind.Expense or VoucherKind.Transfer && CmbAccount.SelectedItem is ComboItem acc && acc.Id > 0) v.AccountId = acc.Id;
        if (_kind == VoucherKind.Transfer && CmbToAccount.SelectedItem is ComboItem acc2 && acc2.Id > 0) v.ToAccountId = acc2.Id;
        if (CmbPartner.SelectedItem is ComboItem p && p.Id > 0) v.PartnerId = p.Id;
        if (CmbProject.SelectedItem is ComboItem pr && pr.Id > 0) v.ProjectId = pr.Id;
        if (CmbDept.SelectedItem is ComboItem d && d.Id > 0) v.DeptId = d.Id;
        if (CmbHandler.SelectedItem is ComboItem h && h.Id > 0) v.Handler = h.Name;
        if (CmbSubject.SelectedItem is ComboItem s && s.Id > 0) v.SubjectId = s.Id;

        if (_kind == VoucherKind.Transfer && !string.IsNullOrWhiteSpace(TxtFee.Text))
            v.FeeCents = Money.ParseOrZero(TxtFee.Text);

        if (_kind is VoucherKind.Income or VoucherKind.Expense && v.AccountId == null)
            throw new AppException(_kind == VoucherKind.Expense ? "请选择付款账户。" : "请选择入账账户。");
        if (_kind == VoucherKind.Transfer)
        {
            if (v.AccountId == null || v.ToAccountId == null) throw new AppException("请选择转出与转入账户。");
            if (v.AccountId == v.ToAccountId) throw new AppException("转出账户与转入账户不能相同。");
        }
        if (_kind is VoucherKind.Receivable or VoucherKind.Payable && v.PartnerId == null)
            throw new AppException(_kind == VoucherKind.Receivable ? "请选择客户。" : "请选择供应商。");

        return v;
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Save(submit: true);
    private void SaveDraft_Click(object sender, RoutedEventArgs e) => Save(submit: false);

    private void Save(bool submit)
    {
        try
        {
            var v = BuildVoucher();
            var svc = new VoucherService();
            if (v.Id == 0)
            {
                svc.Create(v, submit);
                Ui.Info($"单据 {v.DocNo} 已保存，状态：{(submit ? "待审核" : "草稿")}。\n\n审核后才会计入正式账务与报表。", "保存成功");
            }
            else
            {
                svc.Update(v);
                if (submit && _origin!.Status == VoucherStatus.Draft) svc.Submit(v.Id);
                Ui.Info("单据已更新。", "保存成功");
            }
            DialogResult = true;
        }
        catch (Exception ex) { Ui.Error(ex); }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_origin == null) return;
        if (!Ui.Confirm($"确定删除单据 {_origin.DocNo}？此操作不可恢复。")) return;
        try
        {
            new VoucherService().DeleteDraft(_origin.Id);
            DialogResult = true;
        }
        catch (Exception ex) { Ui.Error(ex); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

public class NatureItem
{
    public VoucherNature Nature { get; init; }
    public string Name { get; init; } = "";
    public override string ToString() => Name;
}
