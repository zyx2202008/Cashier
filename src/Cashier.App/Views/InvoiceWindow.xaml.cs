using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cashier.Core;
using Cashier.Core.Services;

namespace Cashier.App.Views;

public partial class InvoiceWindow : Window
{
    private readonly InvoiceKind _kind;
    private readonly Invoice? _origin;
    private bool _refreshingPartners;
    private bool _savingQuick;
    private bool _imeComposing;
    private long? _previousPartner, _previousOriginal, _createdQuickId;
    private string? _reviewSnapshot, _reviewName;
    private string? _calculatedGrossText, _calculatedRateText;
    public bool PartnerDirectoryChanged { get; private set; }
    // Set only by the explicit isolated-ledger WPF acceptance driver. Normal windows use real dialogs.
    internal Action? QaSavedNotice { get; set; }
    internal Func<bool>? QaDistinctCustomerConfirmation { get; set; }
    private bool QuickOpen => QuickCustomerPanel.Visibility == Visibility.Visible;

    public InvoiceWindow(InvoiceKind kind, Invoice? invoice)
    {
        InitializeComponent();
        Height = Math.Max(MinHeight, Math.Min(680, SystemParameters.WorkArea.Height - 24));
        _kind = kind;
        _origin = invoice;
        TitleText.Text = invoice == null ? "登记发票" : "修改发票";
        Title = TitleText.Text;
        KindText.Text = kind == InvoiceKind.Out ? "销项" : "进项";
        LblPartner.Text = kind == InvoiceKind.Out ? "客户 *" : "供应商 *";

        var invoiceTypes = new[]
        {
            "电子发票（普通）", "电子发票（专用）", "增值税专用发票", "增值税普通发票",
            "机动车销售统一发票", "二手车销售统一发票", "其他",
        }.ToList();
        if(invoice != null && !invoiceTypes.Contains(invoice.InvoiceType))invoiceTypes.Add(invoice.InvoiceType);
        CmbType.ItemsSource=invoiceTypes.Select(x => new ComboItem { Id = 0, Name = x }).ToList();
        CmbType.SelectedIndex = 0;

        RefreshPartners(null, chooseFirst: true);
        BtnAddCustomer.Visibility = kind == InvoiceKind.Out && invoice == null ? Visibility.Visible : Visibility.Collapsed;
        BtnAddCustomer.IsEnabled = Perm.Has(AppSession.Role, Perm.MasterData);
        BtnAddCustomer.ToolTip = BtnAddCustomer.IsEnabled ? "在当前发票中新增并选用客户" : "仅管理员或会计可新增客户；当前账号可选择已有客户";
        TextCompositionManager.AddPreviewTextInputStartHandler(this, (_, _) => _imeComposing = true);
        TextCompositionManager.AddPreviewTextInputHandler(this, (_, _) => _imeComposing = false);

        CmbRate.ItemsSource = new[] { "13", "9", "6", "5", "3", "1", "0" };
        CmbRate.Text = "6";

        DpReg.SelectedDate = DateTime.Today;

        if (invoice != null)
        {
            CmbType.SelectedItem = CmbType.Items.Cast<ComboItem>().FirstOrDefault(x => x.Name == invoice.InvoiceType);
            TxtNo.Text = invoice.InvoiceNo;
            TxtCode.Text = invoice.InvoiceCode;
            CmbPartner.SelectedValue = invoice.PartnerId;
            LoadOriginals();
            CmbOriginal.SelectedValue = invoice.OriginalInvoiceId ?? 0;
            DpReg.SelectedDate = DateTime.Parse(invoice.RegDate);
            DpDeliver.SelectedDate = string.IsNullOrWhiteSpace(invoice.DeliverDate) ? null : DateTime.Parse(invoice.DeliverDate);
            TxtGross.Text = Money.Format(invoice.GrossCents);
            CmbRate.Text = (invoice.RateBp / 100m).ToString("0.##");
            TxtNet.Text = Money.Format(invoice.NetCents);
            TxtTax.Text = Money.Format(invoice.TaxCents);
            TxtContent.Text = invoice.Content;
            TxtNote.Text = invoice.Note;
            if (invoice.Status == InvoiceStatus.Void)
            {
                HintText.Text = "该发票已作废，不能修改。";
                foreach (var c in AllControls(this)) c.IsEnabled = false;
            }
        }
        if (invoice == null) Recalc();
        _calculatedGrossText = TxtGross.Text;
        _calculatedRateText = CmbRate.Text;
    }

    private static IEnumerable<Control> AllControls(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is Control c) yield return c;
            foreach (var x in AllControls(child)) yield return x;
        }
    }

    private void Amount_Changed(object sender, TextChangedEventArgs e) => Recalc();
    private void Rate_Changed(object sender, SelectionChangedEventArgs e) => Recalc();
    private void Rate_LostFocus(object sender, RoutedEventArgs e) => Recalc();

    private void Partner_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshingPartners && CmbOriginal != null) LoadOriginals();
    }

    private void LoadOriginals()
    {
        var previous = CmbOriginal.SelectedValue as long? ?? 0;
        var choices = new List<ComboItem> { new() { Id = 0, Name = "（无）" } };
        if (CmbPartner.SelectedItem is ComboItem partner)
            choices.AddRange(new InvoiceService().List(_kind, null, partner.Id, null, null, false)
            .Where(i => i.GrossCents > 0 && i.Id != _origin?.Id)
            .Select(i => new ComboItem { Id = i.Id, Name = $"{i.RegDate}  {i.InvoiceNo}  {i.GrossText}" }));
        CmbOriginal.ItemsSource = choices;
        CmbOriginal.SelectedValue = previous;
        if (CmbOriginal.SelectedIndex < 0) CmbOriginal.SelectedIndex = 0;
    }

    private int RateBp()
    {
        return InvoiceService.ParseRate(CmbRate.Text);
    }

    private void Recalc()
    {
        if (!IsLoaded) return;
        if (_calculatedGrossText == TxtGross.Text && _calculatedRateText == CmbRate.Text) return;
        _calculatedGrossText = TxtGross.Text;
        _calculatedRateText = CmbRate.Text;
        var txt = (TxtGross.Text ?? "").Replace(",", "").Trim();
        if (!decimal.TryParse(txt, out var gross) || gross == 0)
        {
            TxtNet.Text = "";
            TxtTax.Text = "";
            return;
        }
        try
        {
            var grossCents = Money.ToCents(gross);
            var (net, tax) = Money.SplitTax(grossCents, RateBp());
            TxtNet.Text = Money.Format(net);
            TxtTax.Text = Money.Format(tax);
        }
        catch (AppException) { TxtNet.Text = ""; TxtTax.Text = ""; }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (QuickOpen || _savingQuick || _imeComposing) return;
        try
        {
            if (CmbType.SelectedItem is not ComboItem type) throw new AppException("请选择发票类型。");
            if (CmbPartner.SelectedItem is not ComboItem partner) throw new AppException(_kind == InvoiceKind.Out ? "请选择客户。" : "请选择供应商。");
            if (string.IsNullOrWhiteSpace(TxtNo.Text)) throw new AppException("请填写发票号码。");
            var grossText = (TxtGross.Text ?? "").Replace(",", "").Trim();
            if (!decimal.TryParse(grossText, out var gross) || gross == 0) throw new AppException("请填写正确的含税金额，红字使用负数。");
            if (!decimal.TryParse((TxtNet.Text ?? "").Replace(",", "").Trim(), out var netValue) ||
                !decimal.TryParse((TxtTax.Text ?? "").Replace(",", "").Trim(), out var taxValue))
                throw new AppException("请填写票面的不含税金额和税额。");
            if (decimal.Round(gross, 2) != gross || decimal.Round(netValue, 2) != netValue || decimal.Round(taxValue, 2) != taxValue)
                throw new AppException("票面金额最多保留两位小数。");

            var grossCents = Money.ToCents(gross);
            var net = Money.ToCents(netValue);
            var tax = Money.ToCents(taxValue);

            var inv = new Invoice
            {
                AutoLink = true,
                Id = _origin?.Id ?? 0,
                Revision = _origin?.Revision ?? 0,
                Kind = _kind,
                InvoiceType = type.Name,
                InvoiceNo = TxtNo.Text.Trim(),
                InvoiceCode = string.IsNullOrWhiteSpace(TxtCode.Text) ? null : TxtCode.Text.Trim(),
                PartnerId = partner.Id,
                OriginalInvoiceId = CmbOriginal.SelectedItem is ComboItem original && original.Id > 0 ? original.Id : null,
                RegDate = (DpReg.SelectedDate ?? DateTime.Today).ToString("yyyy-MM-dd"),
                DeliverDate = DpDeliver.SelectedDate?.ToString("yyyy-MM-dd"),
                GrossCents = grossCents,
                NetCents = net,
                TaxCents = tax,
                RateBp = RateBp(),
                Content = string.IsNullOrWhiteSpace(TxtContent.Text) ? null : TxtContent.Text.Trim(),
                Note = string.IsNullOrWhiteSpace(TxtNote.Text) ? null : TxtNote.Text.Trim(),
            };

            var invoiceService = new InvoiceService();
            var invoiceId = invoiceService.Save(inv);
            var saved = invoiceService.Get(invoiceId)!;
            var unlinked = Math.Abs(saved.GrossCents) - Math.Abs(saved.LinkedCents);
            if(QaSavedNotice != null) QaSavedNotice();
            else Ui.Info(unlinked > 0
                ? $"发票已保存，已自动关联 {Money.Format(Math.Abs(saved.LinkedCents))} 元；尚有 {Money.Format(unlinked)} 元未关联业务。请核对并在发票列表中关联。"
                : "发票已保存，发票金额已全部关联业务。", "保存成功");
            DialogResult = true;
        }
        catch (Exception ex) { Ui.Error(ex); }
    }

    private void RefreshPartners(long? selectedId, bool chooseFirst = false)
    {
        var previous = selectedId ?? (CmbPartner.SelectedItem as ComboItem)?.Id;
        var partners = MasterDataService.ListPartners(null, true)
            .Where(p => (p.IsActive && (p.Kind == (_kind == InvoiceKind.Out ? "customer" : "supplier") || p.Kind == "both")) || p.Id == _origin?.PartnerId).ToList();
        _refreshingPartners = true;
        try
        {
            CmbPartner.ItemsSource = partners.Select(p => new ComboItem { Id=p.Id,Name=p.DisplayName }).ToList();
            CmbPartner.SelectedValue = previous;
            if (chooseFirst && CmbPartner.SelectedIndex < 0 && CmbPartner.Items.Count > 0) CmbPartner.SelectedIndex=0;
        }
        finally { _refreshingPartners=false; }
        LoadOriginals();
    }

    private void AddCustomer_Click(object sender, RoutedEventArgs e)
    {
        if (_kind != InvoiceKind.Out || _origin != null || !Perm.Has(AppSession.Role,Perm.MasterData) || QuickOpen) return;
        _previousPartner=(CmbPartner.SelectedItem as ComboItem)?.Id;
        _previousOriginal=CmbOriginal.SelectedValue as long?;
        _createdQuickId=null;
        QuickCustomerFields.IsEnabled=true;
        TxtQuickName.Text=""; TxtQuickContact.Text=""; TxtQuickPhone.Text=""; TxtQuickAddress.Text="";
        QuickMessage.Text="请点击“保存客户并选用”完成新增；回车不会提交发票。";
        CustomerSavedMessage.Visibility=Visibility.Collapsed;
        QuickCustomerPanel.Visibility=Visibility.Visible;
        BtnSaveInvoice.IsEnabled=false; BtnSaveInvoice.IsDefault=false;
        CmbPartner.IsEnabled=false; CmbOriginal.IsEnabled=false;
        BtnAddCustomer.IsEnabled=false;
        TxtQuickName.Focus(); TxtQuickName.BringIntoView();
    }

    private void QuickName_Changed(object sender, TextChangedEventArgs e)
    {
        _reviewSnapshot=null; _reviewName=null;
        if (DuplicatePanel != null) DuplicatePanel.Visibility=Visibility.Collapsed;
    }

    private void ShowReview(InvoiceCustomerResult result)
    {
        _reviewSnapshot=result.Snapshot; _reviewName=TxtQuickName.Text;
        DuplicateCandidates.ItemsSource=result.Candidates;
        DuplicateCandidates.SelectedIndex=-1;
        DuplicatePanel.Visibility=result.Candidates.Count>0 ? Visibility.Visible : Visibility.Collapsed;
        QuickMessage.Text=result.Candidates.Count>0 ? "请核对同名单位，不会自动修改、启用或合并原资料。" : "同名候选已变化，请重新点击保存检查。";
        DuplicatePanel.BringIntoView();
    }

    private void CompleteQuick(long id, bool created)
    {
        RefreshPartners(id);
        if ((CmbPartner.SelectedItem as ComboItem)?.Id != id) throw new AppException("客户已保存，但当前不可选，请刷新名录并核对启用状态。");
        CloseQuick();
        CustomerSavedMessage.Text=created ? "客户已保存并选用，发票尚未保存" : "已选用已有客户，发票尚未保存";
        CustomerSavedMessage.Visibility=Visibility.Visible;
        CmbPartner.Focus();
    }

    private void SaveQuick(string? confirmedSnapshot = null)
    {
        if (!QuickOpen || _savingQuick || _imeComposing) return;
        _savingQuick=true; QuickCustomerPanel.IsEnabled=false;
        try
        {
            if (_createdQuickId.HasValue) { CompleteQuick(_createdQuickId.Value,true); return; }
            var result=MasterDataService.CreateCustomerForInvoice(new(TxtQuickName.Text,TxtQuickContact.Text,TxtQuickPhone.Text,TxtQuickAddress.Text),confirmedSnapshot);
            if (result.Outcome==InvoiceCustomerOutcome.ReviewRequired) { ShowReview(result); return; }
            PartnerDirectoryChanged=true; _createdQuickId=result.Id!.Value; QuickCustomerFields.IsEnabled=false;
            CompleteQuick(result.Id.Value,true);
        }
        catch (Exception ex)
        {
            QuickMessage.Text=(_createdQuickId.HasValue ? "客户已保存，刷新未完成。请重试选用或取消新增；客户不会被删除。\n" : "") +
                (ex is AppException ? ex.Message : "客户操作失败，请稍后重试；如持续失败请检查账套是否可写。" );
        }
        finally { _savingQuick=false; QuickCustomerPanel.IsEnabled=true; }
    }

    private void SaveQuickCustomer_Click(object sender, RoutedEventArgs e) => SaveQuick();
    private void UseExisting_Click(object sender, RoutedEventArgs e)
    {
        if (_savingQuick || !QuickOpen || _reviewSnapshot==null || _reviewName==null) return;
        if (DuplicateCandidates.SelectedItem is not CustomerCandidate candidate) { QuickMessage.Text="请先选择一条具体记录。"; return; }
        try
        {
            var result=MasterDataService.SelectExistingInvoiceCustomer(_reviewName,candidate.Id,_reviewSnapshot);
            if (result.Outcome==InvoiceCustomerOutcome.ReviewRequired) { ShowReview(result); return; }
            CompleteQuick(result.Id!.Value,false);
        }
        catch(Exception ex) { QuickMessage.Text=ex is AppException ? ex.Message : "无法选用客户，请刷新后重试。"; }
    }
    private void ConfirmDuplicate_Click(object sender, RoutedEventArgs e)
    {
        if (_savingQuick || _reviewSnapshot==null || _reviewName==null || !QuickOpen) return;
        var confirmed=QaDistinctCustomerConfirmation?.Invoke() ??
            MessageBox.Show(this,$"已存在同名单位。确认“{_reviewName.Trim()}”是另一家不同的客户，并新建独立名录记录？\n旧单位资料不会被修改。",
                "确认同名不同单位",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)==MessageBoxResult.Yes;
        if (confirmed)
            SaveQuick(_reviewSnapshot);
    }
    private void CloseQuick()
    {
        _imeComposing=false;
        QuickCustomerPanel.Visibility=Visibility.Collapsed;
        BtnSaveInvoice.IsEnabled=true; BtnSaveInvoice.IsDefault=true;
        CmbPartner.IsEnabled=true; CmbOriginal.IsEnabled=true;
        BtnAddCustomer.IsEnabled=Perm.Has(AppSession.Role,Perm.MasterData);
        _reviewSnapshot=null; _reviewName=null; DuplicatePanel.Visibility=Visibility.Collapsed;
    }
    private void CancelQuickCustomer_Click(object sender, RoutedEventArgs e)
    {
        if (_savingQuick) return;
        if ((CmbPartner.SelectedItem as ComboItem)?.Id != _previousPartner)
        {
            if (!_previousPartner.HasValue) CmbPartner.SelectedIndex=-1;
            RefreshPartners(_previousPartner);
        }
        if (_previousOriginal.HasValue) { CmbOriginal.SelectedValue=_previousOriginal.Value; if(CmbOriginal.SelectedIndex<0) CmbOriginal.SelectedIndex=0; }
        CloseQuick(); CmbPartner.Focus();
    }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!QuickOpen || e.Key==Key.ImeProcessed || _imeComposing) return;
        if(e.Key==Key.Enter || e.Key==Key.Return) e.Handled=true;
        if(e.Key==Key.Escape) { e.Handled=true; CancelQuickCustomer_Click(sender,e); }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if(QuickOpen) CancelQuickCustomer_Click(sender,e);
        else DialogResult=false;
    }
}
