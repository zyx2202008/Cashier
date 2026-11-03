using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cashier.App.Printing;
using Cashier.Core;
using Cashier.Core.Services;

namespace Cashier.App.Views;

public partial class MasterDataView : UserControl, IAppPage
{
    private readonly string _type;

    public string PageTitle => _type switch
    {
        "partner" => "客户与供应商",
        "subject" => "收入支出科目",
        "account" => "资金账户",
        "project" => "项目",
        "dept" => "部门",
        "handler" => "经办人",
        _ => "基础资料",
    };

    public string PageSubtitle => _type switch
    {
        "partner" => "维护往来单位、账期与期初余额；已被业务引用的资料不能删除，只能停用",
        "subject" => "收入/支出科目，用于报表归集",
        "account" => "资金账户与期初余额；余额由期初与已审核流水自动计算，不可直接修改",
        "project" => "项目用于收支归集与利润统计",
        "dept" => "部门用于费用归属",
        "handler" => "单据经办人",
        _ => "",
    };

    public string? Chip => null;

    public MasterDataView(string type)
    {
        InitializeComponent();
        _type = type;
        Loaded += (_, _) =>
        {
            BuildColumns();
            Reload();
        };
    }

    public IEnumerable<UIElement> BuildActions()
    {
        // 1.1.2：按权限隐藏维护类按钮，避免只读账号点击后才收到报错弹窗
        var canMaintain = Perm.Has(AppSession.Role, Perm.MasterData);
        var list = new List<UIElement>();
        if (canMaintain)
        {
            var add = Ui.Btn("新增", Add_Click, "Btn.Primary");
            add.Margin = new Thickness(0, 0, 6, 0);
            var edit = Ui.Btn("修改", Edit_Click);
            edit.Margin = new Thickness(0, 0, 6, 0);
            var toggle = Ui.Btn("停用/启用", Toggle_Click);
            toggle.Margin = new Thickness(0, 0, 6, 0);
            var del = Ui.Btn("删除", Delete_Click, "Btn.Danger");
            del.Margin = new Thickness(0, 0, 6, 0);
            list.Add(add); list.Add(edit); list.Add(toggle); list.Add(del);
        }
        list.Add(Ui.Btn("导出", Export_Click, "Btn.Ghost"));
        list.Add(Ui.Btn("打印", Print_Click, "Btn.Ghost"));
        return list;
    }

    private void BuildColumns()
    {
        Grid.Columns.Clear();
        switch (_type)
        {
            case "partner":
                Grid.Columns.Add(Ui.Col("编码", "Code", 96));
                Grid.Columns.Add(Ui.Col("名称", "Name", 176));
                Grid.Columns.Add(Ui.Col("类型", "KindLabel", 80));
                Grid.Columns.Add(Ui.Col("联系人", "Contact", 76));
                Grid.Columns.Add(Ui.Col("电话", "Phone", 112));
                Grid.Columns.Add(Ui.Col("账期", "Terms", 78));
                Grid.Columns.Add(Ui.Col("期初应收", "OpeningReceivableText", 96, true));
                Grid.Columns.Add(Ui.Col("净应收", "NetReceivableText", 104, true));
                Grid.Columns.Add(Ui.Col("期初应付", "OpeningPayableText", 96, true));
                Grid.Columns.Add(Ui.Col("净应付", "NetPayableText", 104, true));
                Grid.Columns.Add(Ui.Col("状态", "ActiveText", 60));
                break;
            case "subject":
                Grid.Columns.Add(Ui.Col("科目编码", "Code", 110));
                Grid.Columns.Add(Ui.Star("科目名称", "Name"));
                Grid.Columns.Add(Ui.Col("方向", "DirectionLabel", 80));
                Grid.Columns.Add(Ui.Col("状态", "ActiveText", 70));
                break;
            case "account":
                Grid.Columns.Add(Ui.Col("账户编码", "Code", 110));
                Grid.Columns.Add(Ui.Col("账户名称", "Name", 200));
                Grid.Columns.Add(Ui.Col("类型", "KindLabel", 80));
                Grid.Columns.Add(Ui.Col("期初余额", "OpeningText", 130, true));
                Grid.Columns.Add(Ui.Col("当前余额", "BalanceText", 130, true));
                Grid.Columns.Add(Ui.Col("状态", "ActiveText", 70));
                break;
            case "project":
                Grid.Columns.Add(Ui.Col("项目编码", "Code", 110));
                Grid.Columns.Add(Ui.Col("项目名称", "Name", 240));
                Grid.Columns.Add(Ui.Col("归属部门", "DeptName", 120));
                Grid.Columns.Add(Ui.Col("负责人", "Owner", 100));
                Grid.Columns.Add(Ui.Col("状态", "ActiveText", 70));
                break;
            case "dept":
                Grid.Columns.Add(Ui.Col("部门编码", "Code", 110));
                Grid.Columns.Add(Ui.Star("部门名称", "Name"));
                Grid.Columns.Add(Ui.Col("状态", "ActiveText", 70));
                break;
            case "handler":
                Grid.Columns.Add(Ui.Col("姓名", "Name", 160));
                Grid.Columns.Add(Ui.Star("", "ActiveText"));
                Grid.Columns.Add(Ui.Col("状态", "ActiveText", 70));
                break;
        }
    }

    public void Refresh() => Reload();

    private void Reload()
    {
        try
        {
            var kw = TxtKeyword.Text.Trim();
            object list = _type switch
            {
                "partner" => MasterDataService.ListPartners(null, true),
                "subject" => MasterDataService.ListSubjects(null, true),
                "account" => MasterDataService.ListAccounts(true),
                "project" => MasterDataService.ListProjects(true),
                "dept" => MasterDataService.ListDepts(true),
                "handler" => MasterDataService.ListHandlers(true),
                _ => new List<object>(),
            };

            IEnumerable<object> rows;
            switch (_type)
            {
                case "partner":
                    rows = ((IEnumerable<Partner>)list)
                        .Where(p => kw.Length == 0 || p.Name.Contains(kw) || p.Code.Contains(kw) || (p.Contact ?? "").Contains(kw))
                        .Cast<object>();
                    break;
                case "subject":
                    rows = ((IEnumerable<Subject>)list)
                        .Where(x => kw.Length == 0 || x.Name.Contains(kw) || x.Code.Contains(kw)).Cast<object>();
                    break;
                case "account":
                    rows = ((IEnumerable<Account>)list)
                        .Where(x => kw.Length == 0 || x.Name.Contains(kw) || x.Code.Contains(kw)).Cast<object>();
                    break;
                case "project":
                    rows = ((IEnumerable<Project>)list)
                        .Where(x => kw.Length == 0 || x.Name.Contains(kw) || x.Code.Contains(kw)).Cast<object>();
                    break;
                case "dept":
                    rows = ((IEnumerable<Dept>)list)
                        .Where(x => kw.Length == 0 || x.Name.Contains(kw) || x.Code.Contains(kw)).Cast<object>();
                    break;
                case "handler":
                    rows = ((IEnumerable<Handler>)list)
                        .Where(x => kw.Length == 0 || x.Name.Contains(kw)).Cast<object>();
                    break;
                default:
                    rows = Enumerable.Empty<object>();
                    break;
            }

            if (CmbActive.SelectedIndex == 1) rows = rows.Where(r => IsActive(r));
            if (CmbActive.SelectedIndex == 2) rows = rows.Where(r => !IsActive(r));

            var materialized = rows.ToList();
            Grid.ItemsSource = materialized;
            FootText.Text = $"共 {materialized.Count} 条 · 停用后历史单据仍可正常查看";
            HintText.Text = _type == "account" ? "账户余额 = 期初 + 已审核流入 − 流出 + 生效的余额调整" : "";
        }
        catch (Exception ex) { Ui.Error(ex); }
    }

    private static bool IsActive(object o) => o switch
    {
        Partner p => p.IsActive,
        Subject s => s.IsActive,
        Account a => a.IsActive,
        Project p => p.IsActive,
        Dept d => d.IsActive,
        Handler h => h.IsActive,
        _ => true,
    };

    private void SetActive(object o, bool active)
    {
        switch (o)
        {
            case Partner p: p.IsActive = active; MasterDataService.SavePartner(p); break;
            case Subject s: s.IsActive = active; MasterDataService.SaveSubject(s); break;
            case Account a: a.IsActive = active; MasterDataService.SaveAccount(a); break;
            case Project p: p.IsActive = active; MasterDataService.SaveProject(p); break;
            case Dept d: d.IsActive = active; MasterDataService.SaveDept(d); break;
            case Handler h: h.IsActive = active; MasterDataService.SaveHandler(h); break;
        }
    }

    private void Query_Click(object sender, RoutedEventArgs e) => Reload();
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        TxtKeyword.Text = "";
        CmbActive.SelectedIndex = 0;
        Reload();
    }
    private void TxtKeyword_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) Reload(); }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (MasterDataDialog.Edit(_type, null, Window.GetWindow(this))) Reload();
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        var sel = Grid.SelectedItem;
        if (sel == null) { Ui.Info("请先选择一条记录。"); return; }
        if (MasterDataDialog.Edit(_type, sel, Window.GetWindow(this))) Reload();
    }

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Edit_Click(sender, e);

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        var sel = Grid.SelectedItem;
        if (sel == null) { Ui.Info("请先选择一条记录。"); return; }
        var active = IsActive(sel);
        if (!Ui.Confirm(active ? "确定停用该记录？停用后不能再被新单据引用，历史单据不受影响。" : "确定启用该记录？")) return;
        try
        {
            SetActive(sel, !active);
            Reload();
        }
        catch (Exception ex) { Ui.Error(ex); }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var sel = Grid.SelectedItem;
        if (sel == null) { Ui.Info("请先选择一条记录。"); return; }
        if (!Ui.Confirm("确定删除？只有未被任何业务引用的记录才能删除，否则请改用停用。")) return;
        try
        {
            switch (sel)
            {
                case Partner p: MasterDataService.DeletePartner(p.Id); break;
                case Subject s: MasterDataService.DeleteSubject(s.Id); break;
                case Account a: MasterDataService.DeleteAccount(a.Id); break;
                case Project p: MasterDataService.DeleteProject(p.Id); break;
                case Dept d: MasterDataService.DeleteDept(d.Id); break;
                case Handler h: MasterDataService.DeleteHandler(h.Id); break;
            }
            Reload();
        }
        catch (Exception ex) { Ui.Error(ex); }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        try { PrintHelper.ExportGrid(Grid, PageTitle, DateTime.Now.ToString("yyyy-MM-dd")); }
        catch (Exception ex) { Ui.Error(ex); }
    }

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        try { PrintHelper.PrintGrid(Grid, PageTitle, DateTime.Now.ToString("yyyy-MM-dd")); }
        catch (Exception ex) { Ui.Error(ex); }
    }
}
