using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Cashier.Core;
using Cashier.Core.Services;
using Microsoft.Win32;

namespace Cashier.App.Views;

internal static class ToolLayout
{
    public static (Grid root, WrapPanel bar, ContentControl body) Build(string margin = "14,12,14,12")
    {
        var root = new Grid { Margin = new Thickness(14, 12, 14, 12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        var bar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var body = new ContentControl();
        Grid.SetRow(bar, 0);
        Grid.SetRow(body, 1);
        root.Children.Add(bar);
        root.Children.Add(body);
        return (root, bar, body);
    }

    public static Border CardBox(UIElement child, double padding = 0) => Ui.Card(child, padding);

    public static DataGrid MakeGrid(params (string header, string path, double width, bool right)[] cols)
    {
        var g = new DataGrid { IsReadOnly = true };
        foreach (var (h, p, w, r) in cols)
            g.Columns.Add(w <= 0 ? Ui.Star(h, p) : Ui.Col(h, p, w, r));
        return g;
    }
}

// ---------------- 导入导出 ----------------
public class ImportView : UserControl, IAppPage
{
    private readonly DataGrid _grid = new();
    private readonly DataGrid _history = new();
    private readonly TextBlock _status = new();
    private ImportPreview? _preview;
    private InvoiceImportPreview? _invoicePreview;
    private readonly ComboBox _importType = new() { Width = 120, Height = 30, Margin = new Thickness(0, 0, 6, 6) };
    private readonly Button _commit;
    private readonly Button _validationExport;
    private bool _importing;

    public string PageTitle => "导入导出";
    public string PageSubtitle => "业务Excel导入、基础资料批量整理、整账套导出迁移；导入前预览校验";
    public string? Chip => null;

    public ImportView(InvoiceKind? invoiceKind = null)
    {
        var (root, bar, body) = ToolLayout.Build();
        Content = root;

        _importType.ItemsSource = new[] { "收入单据", "支出单据", "销项发票", "进项发票" };
        _importType.SelectedIndex = invoiceKind.HasValue ? invoiceKind==InvoiceKind.Out ? 2 : 3 : 0;
        bar.Children.Add(_importType);
        var tplV = Ui.Btn("下载当前类型模板", (_, _) => DownloadTemplate(), "Btn.Ghost");
        var pick = Ui.Btn("选择 Excel 文件…", Pick_Click, "Btn.Primary");
        _commit = Ui.Btn("确认导入", Commit_Click);
        _validationExport=Ui.Btn("导出校验结果",ValidationExport_Click,"Btn.Ghost");
        var hist = Ui.Btn("刷新记录", (_, _) => LoadHistory(), "Btn.Ghost");
        foreach (var b in new[] { tplV, pick, _commit, _validationExport, hist }) { b.Margin = new Thickness(0, 0, 6, 6); bar.Children.Add(b); }
        tplV.IsEnabled=pick.IsEnabled=Perm.Has(AppSession.Role,Perm.Import);
        _commit.IsEnabled=false;_validationExport.IsEnabled=false;_status.TextWrapping=TextWrapping.Wrap;
        _importType.SelectionChanged += (_, _) => { _preview = null; _invoicePreview = null; _grid.ItemsSource = null; _commit.IsEnabled=false;_validationExport.IsEnabled=false;BuildPreviewColumns();_status.Text = "请选择对应类型的 Excel 文件。"; };

        _grid.Columns.Add(Ui.Col("行号", "RowNo", 56, true));
        _grid.Columns.Add(Ui.Col("状态", "StatusText", 90));
        _grid.Columns.Add(Ui.Star("错误原因 / 摘要", "Detail"));
        _grid.Columns.Add(Ui.Col("日期", "Date", 96));
        _grid.Columns.Add(Ui.Col("性质 / 票号", "Nature", 160));
        _grid.Columns.Add(Ui.Col("金额", "Amount", 110, true));
        BuildPreviewColumns();

        _history.Columns.Add(Ui.Col("导入时间", "Ts", 150));
        _history.Columns.Add(Ui.Col("文件", "File", 260));
        _history.Columns.Add(Ui.Col("类型", "Kind", 80));
        _history.Columns.Add(Ui.Col("总行数", "Total", 80, true));
        _history.Columns.Add(Ui.Col("成功", "Ok", 70, true));
        _history.Columns.Add(Ui.Col("失败", "Fail", 70, true));
        _history.Columns.Add(Ui.Col("操作人", "User", 100));

        var tabs = new TabControl();
        var t1 = new TabItem { Header = "导入预览" };
        var p1 = new DockPanel();
        var st = new Border
        {
            Padding = new Thickness(10, 6, 10, 6),
            Background = Ui.Br("B.Header"),
            BorderBrush = Ui.Br("B.Line"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = _status,
        };
        DockPanel.SetDock(st, Dock.Top);
        p1.Children.Add(st);
        p1.Children.Add(_grid);
        t1.Content = p1;

        var t2 = new TabItem { Header = "导入批次记录" };
        t2.Content = _history;
        tabs.Items.Add(t1);
        tabs.Items.Add(t2);
        tabs.Items.Add(new TabItem{Header="基础资料整理",Content=new MasterDataExchangeView()});
        tabs.Items.Add(new TabItem{Header="整账套迁移",Content=new DataPackageView()});
        tabs.SelectionChanged+=(_,e)=>{if(ReferenceEquals(e.Source,tabs))bar.Visibility=tabs.SelectedIndex<2?Visibility.Visible:Visibility.Collapsed;};
        body.Content = Ui.Card(tabs);

        _status.Text = "尚未选择文件。请先下载模板填写，或直接选择已填写的 Excel。";
        Loaded += (_, _) => LoadHistory();
    }

    public IEnumerable<UIElement> BuildActions() => Array.Empty<UIElement>();
    public void Refresh() => LoadHistory();

    private void DownloadTemplate()
    {
        try
        {
            var dlg = new SaveFileDialog
            {
                Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
                FileName = _importType.SelectedItem + "导入模板.xlsx",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            };
            if (dlg.ShowDialog() != true) return;
            if (_importType.SelectedIndex < 2)
                ExcelService.CreateVoucherTemplate(_importType.SelectedIndex == 0 ? VoucherKind.Income : VoucherKind.Expense, dlg.FileName);
            else
                ExcelService.CreateInvoiceTemplate(_importType.SelectedIndex == 2 ? InvoiceKind.Out : InvoiceKind.In, dlg.FileName);
            Ui.Info("模板已生成：\n" + dlg.FileName + "\n\n请查看工作簿内的填写说明和往来单位编码。", "模板下载");
        }
        catch (Exception ex) { Ui.Error(ex); }
    }

    private void Pick_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new OpenFileDialog { Filter = "Excel 工作簿 (*.xlsx)|*.xlsx" };
            if (dlg.ShowDialog() != true) return;
            LoadFile(dlg.FileName);
        }
        catch (Exception ex) { Ui.Error(ex); }
    }

    internal void LoadFile(string path)
    {
            _preview = null; _invoicePreview = null; _grid.ItemsSource = null;_commit.IsEnabled=false;_validationExport.IsEnabled=false;
            BuildPreviewColumns();
            if (_importType.SelectedIndex >= 2)
            {
                var invKind = _importType.SelectedIndex == 2 ? InvoiceKind.Out : InvoiceKind.In;
                _invoicePreview = ExcelService.PreviewInvoiceImport(invKind, path);
                ShowInvoicePreview(_invoicePreview);
                _commit.IsEnabled=_invoicePreview.OkCount>0;_validationExport.IsEnabled=true;
                _status.Text = $"文件：{_invoicePreview.FileName} · 共 {_invoicePreview.Rows.Count} 行，可导入 {_invoicePreview.OkCount} 行，错误 {_invoicePreview.ErrorCount} 行，重复 {_invoicePreview.DuplicateCount} 行。请逐列核对票号、单位和价税金额；可导出校验结果修正。";
                return;
            }
            var kind = _importType.SelectedIndex == 0 ? VoucherKind.Income : VoucherKind.Expense;
            _preview = ExcelService.PreviewVoucherImport(kind, path);
            _grid.ItemsSource = _preview.Rows.Select(r => new
            {
                r.RowNo,
                r.StatusText,
                Detail = r.Error ?? (r.Duplicate ? "与已有单据重复，将跳过" : (r.Voucher?.Summary ?? "")),
                Date = r.Voucher?.BizDate ?? r.Raw.ElementAtOrDefault(0) ?? "",
                Nature = r.Voucher == null ? r.Raw.ElementAtOrDefault(1) ?? "" : NatureInfo.Label(r.Voucher.Nature),
                Amount = r.Voucher == null ? r.Raw.ElementAtOrDefault(2) ?? "" : Money.Format(r.Voucher.AmountCents),
            }).ToList();

            _status.Text = $"文件：{_preview.FileName} · 共 {_preview.Rows.Count} 行，可导入 {_preview.OkCount} 行，" +
                           $"错误 {_preview.ErrorCount} 行，重复 {_preview.DuplicateCount} 行。确认无误后点击“确认导入”。";
            _commit.IsEnabled=_preview.OkCount>0;
    }

    private void BuildPreviewColumns()
    {
        _grid.Columns.Clear();
        _grid.Columns.Add(Ui.Col("行号","RowNo",48,true));_grid.Columns.Add(Ui.Col("状态","StatusText",92));
        if(_importType.SelectedIndex>=2)
        {
            foreach(var (title,path,width,right) in new[]{("发票号码","InvoiceNo",150d,false),("往来单位","Partner",170d,false),
                ("含税金额","Gross",110d,true),("不含税金额","Net",110d,true),("税额","Tax",100d,true),("税率","Rate",64d,true),
                ("原蓝票号码","Original",150d,false),("登记日期","Date",100d,false),("单位编码","PartnerCode",110d,false)})_grid.Columns.Add(Ui.Col(title,path,width,right));
            _grid.Columns.Add(Ui.Col("校验原因 / 内容","Detail",300));
        }
        else
        {
            _grid.Columns.Add(Ui.Star("错误原因 / 摘要","Detail"));_grid.Columns.Add(Ui.Col("日期","Date",96));_grid.Columns.Add(Ui.Col("性质","Nature",160));_grid.Columns.Add(Ui.Col("金额","Amount",110,true));
        }
    }
    private void ShowInvoicePreview(InvoiceImportPreview preview,bool imported=false)
    {
        _grid.ItemsSource=preview.Rows.Select(r=>new{r.RowNo,StatusText=imported&&r.Ok&&!r.Duplicate?"已导入":r.StatusText,
            InvoiceNo=r.Invoice?.InvoiceNo??r.Raw.ElementAtOrDefault(2)??"",Partner=(r.Raw.ElementAtOrDefault(4)??"")+" · "+(r.Invoice?.PartnerName??""),PartnerCode=r.Raw.ElementAtOrDefault(4)??"",Date=r.Invoice?.RegDate??r.Raw.ElementAtOrDefault(0)??"",
            Gross=r.Invoice?.GrossText??r.Raw.ElementAtOrDefault(5)??"",Net=r.Invoice?.NetText??r.Raw.ElementAtOrDefault(7)??"",Tax=r.Invoice?.TaxText??r.Raw.ElementAtOrDefault(8)??"",Rate=r.Invoice?.RateLabel??r.Raw.ElementAtOrDefault(6)??"",Original=r.OriginalInvoiceNo,
            Detail=r.Error??(r.Duplicate?"重复号码跳过，不覆盖已有发票":r.Invoice?.Content??"")}).ToList();
    }
    private void ValidationExport_Click(object sender,RoutedEventArgs e)
    {
        if(_invoicePreview==null)return;
        try{var dlg=new SaveFileDialog{Filter="Excel 工作簿 (*.xlsx)|*.xlsx",FileName="发票导入校验结果.xlsx"};if(dlg.ShowDialog()==true)ExcelService.ExportInvoiceValidation(_invoicePreview,dlg.FileName);}catch(Exception ex){Ui.Error(ex);}
    }
    internal int ImportPreparedInvoices()
    {
        if(_invoicePreview==null||_importing)throw new AppException("请先选择文件并完成预览。");
        _importing=true;_commit.IsEnabled=false;
        try
        {
            var preview=_invoicePreview;var count=ExcelService.CommitInvoiceImport(preview);
            ShowInvoicePreview(preview,true);_status.Text=$"导入完成：已登记 {count} 张发票，跳过错误 {preview.ErrorCount} 行和重复 {preview.DuplicateCount} 行。其中 {preview.UnlinkedInvoiceCount} 张存在未关联金额，请在发票列表核对并关联业务。";
            _invoicePreview=null;_validationExport.IsEnabled=false;LoadHistory();return count;
        }
        catch(Exception ex){_status.Text=ex.Message+" 请重新选择文件预览。";throw;}
        finally{_importing=false;}
    }

    private void Commit_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_invoicePreview != null)
            {
                if (_invoicePreview.ErrorCount > 0 && !Ui.Confirm($"有 {_invoicePreview.ErrorCount} 行存在错误，这些行将跳过。仍要导入其余 {_invoicePreview.OkCount} 行吗？")) return;
                ImportPreparedInvoices();
                return;
            }
            if (_preview == null) { Ui.Info("请先选择 Excel 文件并查看预览。"); return; }
            if (_preview.ErrorCount > 0 && !Ui.Confirm($"有 {_preview.ErrorCount} 行存在错误，这些行将被跳过（不会静默忽略，错误已在预览中列出）。\n\n仍要导入其余 {_preview.OkCount} 行吗？"))
                return;
            var n = ExcelService.CommitVoucherImport(_preview);
            Ui.Info($"已导入 {n} 张单据，状态为待审核。请到“审核中心”或对应列表审核后计入账务。", "导入完成");
            LoadHistory();
            _preview = null;
            _status.Text = $"上次导入成功 {n} 行。";
        }
        catch (Exception ex) { Ui.Error(ex); }
    }

    private void LoadHistory()
    {
        try
        {
            _history.ItemsSource = ExcelService.ImportHistory().Select(h => new
            {
                Ts = h.ts, File = h.file, Kind = h.kind switch { "expense" => "支出", "income" => "收入", "invoice_out" => "销项发票", "invoice_in" => "进项发票", "master_account"=>"资金账户","master_partner"=>"往来单位","master_subject"=>"科目","master_dept"=>"部门","master_project"=>"项目","master_handler"=>"经办人", _ => h.kind },
                Total = h.total, Ok = h.ok, Fail = h.fail, User = h.user,
            }).ToList();
        }
        catch (Exception ex) { Ui.Error(ex); }
    }
}

// ---------------- 用户与权限 ----------------
public class UserView : UserControl, IAppPage
{
    private readonly DataGrid _grid = new();
    public string PageTitle => "用户与权限";
    public string PageSubtitle => "本地账号与角色：管理员、会计、出纳、只读；权限在业务层校验";
    public string? Chip => null;

    public UserView()
    {
        var (root, bar, body) = ToolLayout.Build();
        Content = root;
        var add = Ui.Btn("新增用户", (_, _) => Edit(null), "Btn.Primary");
        var edit = Ui.Btn("修改", (_, _) => Edit(Selected()));
        var pwd = Ui.Btn("重置密码", (_, _) => ResetPwd());
        // 1.1.2：无用户管理权限时禁用操作按钮
        var canManage = Perm.Has(AppSession.Role, Perm.Users);
        foreach (var b in new[] { add, edit, pwd }) { b.Margin = new Thickness(0, 0, 6, 0); b.IsEnabled = canManage; bar.Children.Add(b); }

        _grid.Columns.Add(Ui.Col("登录名", "Username", 140));
        _grid.Columns.Add(Ui.Col("姓名", "DisplayName", 160));
        _grid.Columns.Add(Ui.Col("角色", "RoleLabel", 100));
        _grid.Columns.Add(Ui.Col("状态", "ActiveText", 80));
        _grid.Columns.Add(Ui.Col("创建时间", "CreatedAt", 160));
        body.Content = Ui.Card(_grid);
        Loaded += (_, _) => Load();
    }

    public IEnumerable<UIElement> BuildActions() => Array.Empty<UIElement>();
    public void Refresh() => Load();

    private void Load()
    {
        try
        {
            _grid.ItemsSource = new AuthService().List().Select(u => new
            {
                u.Username, u.DisplayName, u.RoleLabel, ActiveText = u.IsActive ? "启用" : "停用", u.CreatedAt, u.Id, u.Role,
            }).ToList();
        }
        catch (Exception ex) { Ui.Error(ex); }
    }

    private dynamic? Selected() => _grid.SelectedItem;

    private void Edit(dynamic? row)
    {
        try
        {
            var win = new UserDialog(row == null ? null : (User)new User
            {
                Id = (long)row.Id,
                Username = (string)row.Username,
                DisplayName = (string)row.DisplayName,
                Role = (UserRole)row.Role,
            });
            win.Owner = Window.GetWindow(this);
            if (win.ShowDialog() == true) Load();
        }
        catch (Exception ex) { Ui.Error(ex); }
    }

    private void ResetPwd()
    {
        var row = Selected();
        if (row == null) { Ui.Info("请先选择一个用户。"); return; }
        var pwd = TextPromptWindow.Ask("重置密码", $"为用户 {row.Username} 设置新密码（至少 6 位）：", Window.GetWindow(this));
        if (pwd == null) return;
        try
        {
            new AuthService().ResetPassword((long)row.Id, pwd);
            Ui.Info("密码已重置。");
        }
        catch (Exception ex) { Ui.Error(ex); }
    }
}

public class UserDialog : Window
{
    private readonly User? _user;
    private readonly TextBox _name = new() { Height = 30 };
    private readonly TextBox _display = new() { Height = 30 };
    private readonly ComboBox _role = new() { Height = 30 };
    private readonly PasswordBox _pwd = new() { Height = 30 };

    public UserDialog(User? user)
    {
        _user = user;
        Title = user == null ? "新增用户" : "修改用户";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Ui.Br("B.Bg");
        FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI");

        var sp = new StackPanel { Margin = new Thickness(16) };
        void Add(string label, UIElement input)
        {
            sp.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.Resources["T.Label"] });
            sp.Children.Add(input);
            sp.Children.Add(new Border { Height = 8 });
        }

        foreach (var (v, n) in new[] { (UserRole.Admin, "管理员"), (UserRole.Accountant, "会计"), (UserRole.Cashier, "出纳"), (UserRole.Viewer, "只读") })
            _role.Items.Add(new ComboBoxItem { Content = n, Tag = v });
        _role.SelectedIndex = 2;

        _name.Text = user?.Username ?? "";
        _display.Text = user?.DisplayName ?? "";
        if (user != null)
        {
            _name.IsEnabled = false;
            foreach (ComboBoxItem it in _role.Items)
                if ((UserRole)it.Tag! == user.Role) it.IsSelected = true;
        }

        Add("登录名 *", _name);
        Add("姓名 *", _display);
        Add("角色 *", _role);
        if (user == null) Add("初始密码 *（至少 6 位）", _pwd);

        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "保存", Width = 84, Style = (Style)Application.Current.Resources["Btn.Primary"], IsDefault = true };
        ok.Click += (_, _) => Save();
        var cancel = new Button { Content = "取消", Width = 84, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        btns.Children.Add(ok);
        btns.Children.Add(cancel);
        sp.Children.Add(btns);
        Content = sp;
    }

    private void Save()
    {
        try
        {
            var role = (UserRole)((ComboBoxItem)_role.SelectedItem).Tag!;
            var svc = new AuthService();
            if (_user == null)
            {
                svc.Create(_name.Text.Trim(), _display.Text.Trim(), _pwd.Password, role);
            }
            else
            {
                svc.Update(_user.Id, _display.Text.Trim(), role, true);
            }
            DialogResult = true;
        }
        catch (Exception ex) { Ui.Error(ex); }
    }
}

// ---------------- 操作日志 ----------------
public class LogView : UserControl, IAppPage
{
    private readonly DataGrid _grid = new();
    private readonly DatePicker _from = new() { Width = 124 };
    private readonly DatePicker _to = new() { Width = 124 };
    private readonly TextBox _kw = new() { Width = 180, Height = 30 };

    public string PageTitle => "操作日志";
    public string PageSubtitle => "记录操作者、时间、动作、单据号与变更内容；仅追加不提供删除入口";
    public string? Chip => null;

    public LogView()
    {
        var (root, bar, body) = ToolLayout.Build();
        Content = root;
        var q = Ui.Btn("查询", (_, _) => Load(), "Btn.Primary");
        var exp = Ui.Btn("导出", (_, _) =>
        {
            try { Printing.PrintHelper.ExportGrid(_grid, "操作日志", $"{_from.SelectedDate:yyyy-MM-dd} 至 {_to.SelectedDate:yyyy-MM-dd}"); }
            catch (Exception ex) { Ui.Error(ex); }
        }, "Btn.Ghost");
        bar.Children.Add(new TextBlock { Text = "从", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        bar.Children.Add(_from);
        bar.Children.Add(new TextBlock { Text = "至", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0) });
        bar.Children.Add(_to);
        bar.Children.Add(new TextBlock { Text = "关键字", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 6, 0) });
        bar.Children.Add(_kw);
        q.Margin = new Thickness(10, 0, 6, 0);
        bar.Children.Add(q);
        bar.Children.Add(exp);

        _grid.Columns.Add(Ui.Col("时间", "Ts", 150));
        _grid.Columns.Add(Ui.Col("操作人", "Username", 90));
        _grid.Columns.Add(Ui.Col("动作", "Action", 120));
        _grid.Columns.Add(Ui.Col("对象", "Entity", 90));
        _grid.Columns.Add(Ui.Col("单据号", "DocNo", 130));
        _grid.Columns.Add(Ui.Star("变更前", "BeforeJson"));
        _grid.Columns.Add(Ui.Star("变更后", "AfterJson"));
        _grid.Columns.Add(Ui.Col("原因", "Reason", 140));
        body.Content = Ui.Card(_grid);

        var today = DateTime.Today;
        _from.SelectedDate = new DateTime(today.Year, today.Month, 1);
        _to.SelectedDate = today;
        Loaded += (_, _) => Load();
    }

    public IEnumerable<UIElement> BuildActions() => Array.Empty<UIElement>();
    public void Refresh() => Load();

    private void Load()
    {
        try
        {
            _grid.ItemsSource = AuditLogService.Query(_kw.Text.Trim(), null,
                _from.SelectedDate?.ToString("yyyy-MM-dd"), _to.SelectedDate?.ToString("yyyy-MM-dd"), 1000)
                .Select(l => new
                {
                    l.Ts, l.Username, l.Action, l.Entity, l.DocNo,
                    BeforeJson = l.BeforeJson ?? "", AfterJson = l.AfterJson ?? "", Reason = l.Reason ?? "",
                }).ToList();
        }
        catch (Exception ex) { Ui.Error(ex); }
    }
}

// ---------------- 期间管理 ----------------
public class PeriodView : UserControl, IAppPage
{
    private readonly DataGrid _grid = new();
    private readonly TextBox _period = new() { Width = 100, Height = 30 };

    public string PageTitle => "期间管理";
    public string PageSubtitle => "关闭期间后该月不能新增、修改或反审核单据；重新开启需填写原因并写入日志";
    public string? Chip => null;

    public PeriodView()
    {
        var (root, bar, body) = ToolLayout.Build();
        Content = root;
        bar.Children.Add(new TextBlock { Text = "期间（yyyyMM）", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        bar.Children.Add(_period);
        var close = Ui.Btn("关闭期间", (_, _) => ClosePeriod(), "Btn.Primary");
        var reopen = Ui.Btn("重新开启", (_, _) => Reopen());
        var refresh = Ui.Btn("刷新", (_, _) => Load(), "Btn.Ghost");
        // 1.1.2：无关账权限时禁用操作按钮（只读展示列表）
        var canClose = Perm.Has(AppSession.Role, Perm.PeriodClose);
        close.IsEnabled = canClose;
        reopen.IsEnabled = canClose;
        foreach (var b in new[] { close, reopen, refresh }) { b.Margin = new Thickness(8, 0, 0, 0); bar.Children.Add(b); }

        _grid.Columns.Add(Ui.Col("期间", "Period", 120));
        _grid.Columns.Add(Ui.Col("状态", "StatusLabel", 100));
        _grid.Columns.Add(Ui.Col("关闭时间", "ClosedAt", 170));
        _grid.Columns.Add(Ui.Col("关闭人", "ClosedBy", 120));
        body.Content = Ui.Card(_grid);
        _period.Text = DateTime.Today.ToString("yyyyMM");
        Loaded += (_, _) => Load();
    }

    public IEnumerable<UIElement> BuildActions() => Array.Empty<UIElement>();
    public void Refresh() => Load();

    private void Load()
    {
        try
        {
            _grid.ItemsSource = PeriodService.List().Select(p => new
            {
                p.Period, StatusLabel = p.IsClosed ? "已关闭" : "开放", ClosedAt = p.ClosedAt ?? "—", ClosedBy = p.ClosedBy ?? "—",
            }).ToList();
        }
        catch (Exception ex) { Ui.Error(ex); }
    }

    private void ClosePeriod()
    {
        var p = _period.Text.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(p, @"^\d{6}$")) { Ui.Info("期间格式应为 yyyyMM，例如 202610。"); return; }
        if (!Ui.Confirm($"确定关闭期间 {p}？关闭后该期间单据不能新增、修改或反审核。")) return;
        try
        {
            PeriodService.Close(p);
            Load();
        }
        catch (Exception ex) { Ui.Error(ex); }
    }

    private void Reopen()
    {
        var p = _period.Text.Trim();
        var reason = TextPromptWindow.Ask("重新开启期间", $"重新开启期间 {p} 需要授权并记录原因：", Window.GetWindow(this));
        if (reason == null) return;
        try
        {
            PeriodService.Reopen(p, reason);
            Load();
        }
        catch (Exception ex) { Ui.Error(ex); }
    }
}

// ---------------- 账套设置 ----------------
public class SettingsView : UserControl, IAppPage
{
    public string PageTitle => "账套与打印设置";
    public string PageSubtitle => "账套名称、列表分页与本地数据目录；打印通过系统打印对话框完成";
    public string? Chip => null;

    private readonly TextBox _book = new() { Height = 30 };
    private readonly TextBox _company = new() { Height = 30 };
    private readonly TextBox _pageSize = new() { Height = 30 };

    public SettingsView()
    {
        var sp = new StackPanel { Margin = new Thickness(18) };
        void Row(string label, UIElement input, string? hint = null)
        {
            sp.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.Resources["T.Label"] });
            sp.Children.Add(input);
            if (hint != null)
                sp.Children.Add(new TextBlock
                {
                    Text = hint, Style = (Style)Application.Current.Resources["T.Hint"],
                    Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap,
                });
            sp.Children.Add(new Border { Height = 12 });
        }

        Row("账套名称", _book, "显示在登录窗口与打印页眉");
        Row("单位名称", _company, "打印报表时显示在页眉右侧");
        Row("列表默认分页条数", _pageSize, "10 ~ 500，仅影响列表查询上限提示");
        sp.Children.Add(new TextBlock { Text = "数据目录：" + App.Db.DataDir, Style = (Style)Application.Current.Resources["T.Hint"], TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(new TextBlock { Text = "备份目录：" + App.Db.BackupDir, Style = (Style)Application.Current.Resources["T.Hint"], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 12) });

        var save = Ui.Btn("保存设置", Save_Click, "Btn.Primary", 110);
        var pwd = Ui.Btn("修改我的密码", ChangePwd_Click, null, 130);
        pwd.Margin = new Thickness(8, 0, 0, 0);
        var open = Ui.Btn("打开数据目录", (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", App.Db.DataDir) { UseShellExecute = true }); }
            catch (Exception ex) { Ui.Error(ex); }
        });
        open.Margin = new Thickness(8, 0, 0, 0);
        var btns = new StackPanel { Orientation = Orientation.Horizontal };
        btns.Children.Add(save);
        btns.Children.Add(pwd);
        btns.Children.Add(open);
        sp.Children.Add(btns);

        Content = new ScrollViewer { Content = Ui.Card(sp), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(14, 12, 14, 12) };
        Loaded += (_, _) =>
        {
            _book.Text = SettingsService.Get("book.name", "默认账套");
            _company.Text = SettingsService.Get("book.company", "");
            _pageSize.Text = SettingsService.Get("ui.pageSize", "50");
        };
    }

    public IEnumerable<UIElement> BuildActions() => Array.Empty<UIElement>();
    public void Refresh() { }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_book.Text)) throw new AppException("账套名称不能为空。");
            SettingsService.Set("book.name", _book.Text.Trim());
            SettingsService.Set("book.company", _company.Text.Trim());
            SettingsService.Set("ui.pageSize", _pageSize.Text.Trim());
            Ui.Info("设置已保存。部分设置（如账套名称）在下次启动或重新登录后显示。", "已保存");
        }
        catch (Exception ex) { Ui.Error(ex); }
    }

    private void ChangePwd_Click(object sender, RoutedEventArgs e)
    {
        var oldPwd = TextPromptWindow.Ask("修改密码", "请输入当前密码：", Window.GetWindow(this));
        if (oldPwd == null) return;
        var newPwd = TextPromptWindow.Ask("修改密码", "请输入新密码（至少 6 位）：", Window.GetWindow(this));
        if (newPwd == null) return;
        try
        {
            new AuthService().ChangeOwnPassword(oldPwd, newPwd);
            Ui.Info("密码已修改。");
        }
        catch (Exception ex) { Ui.Error(ex); }
    }
}

// ---------------- 备份恢复 ----------------
public class BackupView : UserControl, IAppPage
{
    private readonly DataGrid _grid = new();
    private readonly TextBlock _info = new();

    public string PageTitle => "备份与恢复";
    public string PageSubtitle => "使用 SQLite 在线备份（WAL 下同样一致）；恢复前会自动备份当前账套";
    public string? Chip => null;

    public BackupView()
    {
        var (root, bar, body) = ToolLayout.Build();
        Content = root;
        var make = Ui.Btn("立即备份", (_, _) => DoBackup(), "Btn.Primary");
        var restore = Ui.Btn("恢复选中备份", (_, _) => DoRestore());
        make.IsEnabled = Perm.Has(AppSession.Role, Perm.Backup);
        restore.IsEnabled = Perm.Has(AppSession.Role, Perm.Restore);
        var open = Ui.Btn("打开备份目录", (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", App.Db.BackupDir) { UseShellExecute = true }); }
            catch (Exception ex) { Ui.Error(ex); }
        }, "Btn.Ghost");
        var refresh = Ui.Btn("刷新", (_, _) => Load(), "Btn.Ghost");
        foreach (var b in new[] { make, restore, open, refresh }) { b.Margin = new Thickness(0, 0, 6, 0); bar.Children.Add(b); }

        _grid.Columns.Add(Ui.Col("备份文件", "Name", 320));
        _grid.Columns.Add(Ui.Col("大小(KB)", "Size", 100, true));
        _grid.Columns.Add(Ui.Col("备份时间", "Time", 160));
        _grid.Columns.Add(Ui.Star("完整路径", "FullPath"));

        var dp = new DockPanel();
        _info.Style = (Style)Application.Current.Resources["T.Hint"];
        _info.Margin = new Thickness(2, 0, 0, 8);
        _info.TextWrapping = TextWrapping.Wrap;
        DockPanel.SetDock(_info, Dock.Top);
        dp.Children.Add(_info);
        dp.Children.Add(Ui.Card(_grid));
        body.Content = dp;
        Loaded += (_, _) => Load();
    }

    public IEnumerable<UIElement> BuildActions() => Array.Empty<UIElement>();
    public void Refresh() => Load();

    private void Load()
    {
        try
        {
            var files = Directory.Exists(App.Db.BackupDir)
                ? new DirectoryInfo(App.Db.BackupDir).GetFiles("*.db").OrderByDescending(f => f.LastWriteTime).ToList()
                : new List<FileInfo>();
            _grid.ItemsSource = files.Select(f => new
            {
                Name = f.Name,
                Size = (f.Length / 1024).ToString(),
                Time = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                FullPath = f.FullName,
            }).ToList();
            _info.Text = $"当前账套：{App.Db.FilePath}\n备份目录：{App.Db.BackupDir}（共 {files.Count} 个备份）";
        }
        catch (Exception ex) { Ui.Error(ex); }
    }

    private void DoBackup()
    {
        try
        {
            AppSession.Require(Perm.Backup);
            var path = App.Db.BackupTo(null, "manual");
            if (!File.Exists(path)) throw new AppException("备份文件未生成。");
            Load();
            Ui.Info("备份成功：\n" + path, "备份");
        }
        catch (Exception ex) { Ui.Error(ex); }
    }

    private void DoRestore()
    {
        try { AppSession.Require(Perm.Restore); }
        catch (Exception ex) { Ui.Error(ex); return; }
        dynamic? row = _grid.SelectedItem;
        if (row == null) { Ui.Info("请先选择一个备份文件。"); return; }
        string path = (string)row.FullPath;
        if (!Ui.Confirm($"确定用以下备份覆盖当前账套？\n{path}\n\n恢复前会自动备份当前数据。恢复后请重新登录。")) return;
        try
        {
            var safety = App.Db.RestoreFrom(path);
            Ui.Info("恢复完成。\n恢复前数据已备份到：\n" + safety + "\n\n请重新启动软件以加载恢复后的数据。", "恢复成功");
            Application.Current.Shutdown();
        }
        catch (Exception ex) { Ui.Error(ex); }
    }
}
