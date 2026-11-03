using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Cashier.App.Views;
using Cashier.Core;
using Cashier.Core.Services;

namespace Cashier.App;

public class NavItem
{
    public string Group { get; init; } = "";
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public override string ToString() => Title;
}

public interface IAppPage
{
    string PageTitle { get; }
    string PageSubtitle { get; }
    string? Chip { get; }
    IEnumerable<UIElement> BuildActions();
    void Refresh();
}

public partial class MainWindow : Window
{
    public static MainWindow? Instance { get; private set; }
    private readonly ObservableCollection<NavItem> _nav = new();
    private string _currentId = "";

    public MainWindow()
    {
        InitializeComponent();
        Width=Math.Max(MinWidth,Math.Min(Width,SystemParameters.WorkArea.Width-16));
        Height=Math.Max(MinHeight,Math.Min(Height,SystemParameters.WorkArea.Height-16));
        Instance = this;

        NavBook.Text = SettingsService.BookName;
        var version = typeof(MainWindow).Assembly.GetName().Version;
        NavMeta.Text = $"v{version?.ToString(3)} · 本地数据库";
        UserName.Text = AppSession.Current?.DisplayName ?? "";
        UserRole.Text = AppSession.Current?.RoleLabel ?? "";
        UserInitial.Text = string.IsNullOrEmpty(UserName.Text) ? "U" : UserName.Text[..1];
        StatusDb.Text = "数据：" + App.Db.FilePath;

        BuildNav();
        Loaded += (_, _) =>
        {
            Navigate(App.StartupPage ?? "ledger");
            var issues = IntegrityCheckService.FindIssues();
            if (issues.Count > 0)
                MessageBox.Show("发现需要人工核对的旧账异常（程序未自动更改账务）：\n\n" +
                    string.Join("\n", issues.Take(10)) + (issues.Count > 10 ? $"\n另有 {issues.Count - 10} 项。" : "") +
                    "\n\n请先备份并核对相关单据，再用于正式记账。", "账套核对提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        };
    }

    private void BuildNav()
    {
        void Add(string group, string id, string title) =>
            _nav.Add(new NavItem { Group = group, Id = id, Title = title });

        Add("业务记账", "ledger", "收支明细");
        Add("业务记账", "income", "收入记账");
        Add("业务记账", "expense", "支出记账");
        Add("业务记账", "receivable", "应收管理");
        Add("业务记账", "payable", "应付管理");
        Add("业务记账", "transfer", "内部转账");
        Add("业务记账", "audit", "审核中心");

        Add("发票管理", "invoiceOut", "销项发票");
        Add("发票管理", "invoiceIn", "进项发票");

        foreach (var (id, title, group) in ReportService.Catalog)
            Add("报表 · " + group, "rpt_" + id, title);

        Add("基础资料", "md_partner", "客户与供应商");
        Add("基础资料", "md_subject", "收入支出科目");
        Add("基础资料", "md_account", "资金账户");
        Add("基础资料", "md_project", "项目");
        Add("基础资料", "md_dept", "部门");
        Add("基础资料", "md_handler", "经办人");

        Add("系统工具", "tool_import", "导入导出");
        Add("系统工具", "tool_user", "用户与权限");
        Add("系统工具", "tool_log", "操作日志");
        Add("系统工具", "tool_period", "期间管理");
        Add("系统工具", "tool_opening", "期初与余额调整");
        Add("系统工具", "tool_settings", "账套与打印设置");
        Add("系统工具", "tool_backup", "备份与恢复");

        var view = new ListCollectionView(_nav);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(NavItem.Group)));
        Nav.ItemsSource = view;
    }

    public void Navigate(string id)
    {
        var item = _nav.FirstOrDefault(n => n.Id == id) ?? _nav.First();
        _currentId = item.Id;
        Nav.SelectedItem = item;
        ExpandNavGroup(item.Group);

        UserControl page = item.Id switch
        {
            "ledger" => new LedgerView(null, null, "收支明细", "全部收支单据，含待审核与已作废"),
            "income" => new LedgerView(VoucherKind.Income, null, "收入记账", "现收收入、客户回款、预收、其他收入"),
            "expense" => new LedgerView(VoucherKind.Expense, null, "支出记账", "现付费用、供应商付款、预付、其他支出"),
            "receivable" => new LedgerView(VoucherKind.Receivable, null, "应收管理", "应收单据与未收金额，支持核销与开票关联"),
            "payable" => new LedgerView(VoucherKind.Payable, null, "应付管理", "应付单据与未付金额，支持核销"),
            "transfer" => new LedgerView(VoucherKind.Transfer, null, "内部转账", "账户间资金调拨，不计入收支统计"),
            "audit" => new AuditView(),
            "invoiceOut" => new InvoiceView(InvoiceKind.Out),
            "invoiceIn" => new InvoiceView(InvoiceKind.In),
            "md_partner" => new MasterDataView("partner"),
            "md_subject" => new MasterDataView("subject"),
            "md_account" => new MasterDataView("account"),
            "md_project" => new MasterDataView("project"),
            "md_dept" => new MasterDataView("dept"),
            "md_handler" => new MasterDataView("handler"),
            "tool_import" => new ImportView(),
            "tool_user" => new UserView(),
            "tool_log" => new LogView(),
            "tool_period" => new PeriodView(),
            "tool_opening" => new OpeningBalanceView(),
            "tool_settings" => new SettingsView(),
            "tool_backup" => new BackupView(),
            _ => item.Id.StartsWith("rpt_") ? new ReportView(item.Id[4..]) : new PlaceholderView(item.Title),
        };

        PageHost.Content = page;
        PageTitle.Text = page is IAppPage p ? p.PageTitle : item.Title;
        PageSubtitle.Text = page is IAppPage p2 ? p2.PageSubtitle : "";
        PageActions.Children.Clear();
        if (page is IAppPage p3)
            foreach (var el in p3.BuildActions())
            {
                if(el is FrameworkElement element)element.Margin=new Thickness(element.Margin.Left,3,element.Margin.Right,3);
                PageActions.Children.Add(el);
            }

        if (page is IAppPage p4 && p4.Chip is { Length: > 0 } chip)
        {
            PageChip.Visibility = Visibility.Visible;
            PageChipText.Text = chip;
        }
        else PageChip.Visibility = Visibility.Collapsed;

        StatusText.Text = $"当前用户：{AppSession.UserName}（{AppSession.Current?.RoleLabel}） · 账套：{SettingsService.BookName}";
        StatusPeriod.Text = "会计期间：" + DateTime.Today.ToString("yyyy-MM");
    }

    public bool IsShowing(object page) => ReferenceEquals(PageHost.Content, page);

    private void ExpandNavGroup(string group)
    {
        Nav.UpdateLayout();
        Expand(Nav);
        Nav.ScrollIntoView(Nav.SelectedItem);

        void Expand(DependencyObject parent)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is Expander expander && Equals(expander.Header, group))
                {
                    expander.IsExpanded = true;
                    return;
                }
                Expand(child);
            }
        }
    }

    public void RefreshHeader()
    {
        if (PageHost.Content is not IAppPage p) return;
        PageTitle.Text = p.PageTitle;
        PageSubtitle.Text = p.PageSubtitle;
    }

    public void RefreshCurrent()
    {
        if (PageHost.Content is IAppPage p) p.Refresh();
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is NavItem item && item.Id != _currentId) Navigate(item.Id);
    }

    private void OpenDataDir_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", App.Db.DataDir) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show("无法打开目录：" + ex.Message); }
    }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("确定退出当前账号？", "出纳管理", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        var login = new LoginWindow();
        if (login.ShowDialog() == true)
        {
            UserName.Text = AppSession.Current?.DisplayName ?? "";
            UserRole.Text = AppSession.Current?.RoleLabel ?? "";
            UserInitial.Text = string.IsNullOrEmpty(UserName.Text) ? "U" : UserName.Text[..1];
            Navigate("ledger");
        }
    }
}

public class PlaceholderView : UserControl, IAppPage
{
    public string PageTitle { get; }
    public string PageSubtitle => "";
    public string? Chip => null;
    public PlaceholderView(string title)
    {
        PageTitle = title;
        Content = new TextBlock { Text = title + "（建设中）", Margin = new Thickness(24) };
    }
    public IEnumerable<UIElement> BuildActions() => Array.Empty<UIElement>();
    public void Refresh() { }
}
