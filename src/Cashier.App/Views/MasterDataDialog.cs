using System.Windows;
using System.Windows.Controls;
using Cashier.Core;
using Cashier.Core.Services;

namespace Cashier.App.Views;

/// <summary>基础资料通用编辑窗口（客户/供应商、科目、账户、项目、部门、经办人）。</summary>
public class MasterDataDialog : Window
{
    private readonly string _type;
    private readonly object? _entity;
    private readonly Dictionary<string, Control> _fields = new();
    private readonly StackPanel _panel;

    private MasterDataDialog(string type, object? entity)
    {
        _type = type;
        _entity = entity;
        Title = (entity == null ? "新增" : "修改") + TypeName(type);
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Ui.Br("B.Bg");
        FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI");

        _panel = new StackPanel { Margin = new Thickness(16) };
        BuildFields();
        if(type is "account" or "partner" && OpeningBalanceService.State().IsLocked)
        {
            foreach(var key in new[]{"opening","openingAr","openingAp"})
                if(_fields.TryGetValue(key,out var field))field.IsEnabled=false;
            _panel.Children.Add(new TextBlock {Text="期初已锁定。更正请到“系统工具 → 期初与余额调整”登记；新账户或单位以零期初建立。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,4)});
        }

        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var ok = new Button { Content = "保存", Width = 88, Style = (Style)Application.Current.Resources["Btn.Primary"], IsDefault = true };
        ok.Click += (_, _) => Save();
        var cancel = new Button { Content = "取消", Width = 88, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        btns.Children.Add(ok);
        btns.Children.Add(cancel);
        _panel.Children.Add(btns);
        Content = new ScrollViewer { Content = _panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 720 };
    }

    public static bool Edit(string type, object? entity, Window? owner)
    {
        var dlg = new MasterDataDialog(type, entity) { Owner = owner };
        return dlg.ShowDialog() == true;
    }

    private static string TypeName(string t) => t switch
    {
        "partner" => "往来单位",
        "subject" => "科目",
        "account" => "资金账户",
        "project" => "项目",
        "dept" => "部门",
        "handler" => "经办人",
        _ => "资料",
    };

    private void AddRow(string label, Control input, string? label2 = null, Control? input2 = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        left.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.Resources["T.Label"] });
        left.Children.Add(input);
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);
        if (input2 != null)
        {
            var right = new StackPanel();
            right.Children.Add(new TextBlock { Text = label2, Style = (Style)Application.Current.Resources["T.Label"] });
            right.Children.Add(input2);
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
        }
        _panel.Children.Add(grid);
    }

    private TextBox Tb(string key, string value = "")
    {
        var t = new TextBox { Text = value, Height = 30 };
        _fields[key] = t;
        return t;
    }

    private void BuildFields()
    {
        switch (_type)
        {
            case "partner":
            {
                var p = _entity as Partner;
                // Keep the displayed original stable even if a failed save mutates the form object.
                var original = p == null ? null : new Partner { Code = p.Code, Kind = p.Kind };
                var code = Tb("code", original?.Code ?? "保存时自动生成");
                code.IsReadOnly = true;
                AddRow("编码", code, "名称 *", Tb("name", p?.Name ?? ""));
                var kind = new ComboBox { Height = 30 };
                foreach (var (v, n) in new[] { ("customer", "客户"), ("supplier", "供应商"), ("both", "客户与供应商") })
                    kind.Items.Add(new ComboBoxItem { Content = n, Tag = v, IsSelected = p?.Kind == v || (p == null && v == "customer") });
                _fields["kind"] = kind;
                kind.SelectionChanged += (_, _) =>
                {
                    var selected = (kind.SelectedItem as ComboBoxItem)?.Tag?.ToString();
                    var changesPrefix = original != null && selected != null && MasterDataService.PartnerCodeChangesOnKindChange(original, selected);
                    code.Text = original == null ? "保存时自动生成" : changesPrefix ? "保存时自动生成新编码" : original.Code;
                };
                var level = new ComboBox { Height = 30, IsEditable = true, Text = p?.Level ?? "A" };
                level.Items.Add("A"); level.Items.Add("B"); level.Items.Add("C");
                _fields["level"] = level;
                AddRow("单位类型 *", kind, "信用等级", level);
                AddRow("联系人", Tb("contact", p?.Contact ?? ""), "电话", Tb("phone", p?.Phone ?? ""));
                AddRow("地址", Tb("address", p?.Address ?? ""), "账期", Tb("terms", p?.Terms ?? ""));
                AddRow("期初应收（元）", Tb("openingAr", p == null ? "0.00" : Money.Format(p.OpeningReceivableCents)),
                    "期初应付（元）", Tb("openingAp", p == null ? "0.00" : Money.Format(p.OpeningPayableCents)));
                var active = new CheckBox { Content = "启用", IsChecked = p?.IsActive ?? true, Margin = new Thickness(0, 4, 0, 0) };
                _fields["active"] = active;
                _panel.Children.Add(active);
                _panel.Children.Add(new TextBlock
                {
                    Text = "期初应收为正表示客户欠款，为负表示预收；期初应付为正表示欠供应商，为负表示预付。",
                    Style = (Style)Application.Current.Resources["T.Hint"],
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 0),
                });
                break;
            }
            case "subject":
            {
                var s = _entity as Subject;
                AddRow("科目编码 *", Tb("code", s?.Code ?? NextCode("subjects", "5001")), "科目名称 *", Tb("name", s?.Name ?? ""));
                var dir = new ComboBox { Height = 30 };
                dir.Items.Add(new ComboBoxItem { Content = "收入", Tag = "in", IsSelected = s?.Direction != "out" });
                dir.Items.Add(new ComboBoxItem { Content = "支出", Tag = "out", IsSelected = s?.Direction == "out" });
                _fields["dir"] = dir;
                AddRow("方向 *", dir);
                var active = new CheckBox { Content = "启用", IsChecked = s?.IsActive ?? true };
                _fields["active"] = active;
                _panel.Children.Add(active);
                break;
            }
            case "account":
            {
                var a = _entity as Account;
                AddRow("账户编码 *", Tb("code", a?.Code ?? NextCode("accounts", "1002")), "账户名称 *", Tb("name", a?.Name ?? ""));
                var kind = new ComboBox { Height = 30 };
                kind.Items.Add(new ComboBoxItem { Content = "银行账户", Tag = "bank", IsSelected = a?.Kind != "cash" });
                kind.Items.Add(new ComboBoxItem { Content = "现金", Tag = "cash", IsSelected = a?.Kind == "cash" });
                _fields["kind"] = kind;
                AddRow("账户类型 *", kind, "账号后四位", Tb("tail", a?.BankTail ?? ""));
                AddRow("对应科目编码", Tb("subject", a?.SubjectCode ?? "1002"),
                    "期初余额（元）", Tb("opening", a == null ? "0.00" : Money.Format(a.OpeningCents)));
                var active = new CheckBox { Content = "启用", IsChecked = a?.IsActive ?? true };
                _fields["active"] = active;
                _panel.Children.Add(active);
                _panel.Children.Add(new TextBlock
                {
                    Text = "账户余额由期初、已审核资金流水和生效的余额调整自动计算；期初确认后请通过余额调整更正。",
                    Style = (Style)Application.Current.Resources["T.Hint"],
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 0),
                });
                break;
            }
            case "project":
            {
                var p = _entity as Project;
                AddRow("项目编码 *", Tb("code", p?.Code ?? NextCode("projects", "P-")), "项目名称 *", Tb("name", p?.Name ?? ""));
                var dept = new ComboBox { Height = 30, DisplayMemberPath = "Name", SelectedValuePath = "Id" };
                var depts = new List<ComboItem> { new() { Id = 0, Name = "（不指定）" } };
                depts.AddRange(MasterDataService.ListDepts(false).Select(d => new ComboItem { Id = d.Id, Name = d.Name }));
                dept.ItemsSource = depts;
                dept.SelectedValue = p?.DeptId ?? 0;
                if (dept.SelectedIndex < 0) dept.SelectedIndex = 0;
                _fields["dept"] = dept;
                AddRow("归属部门", dept, "负责人", Tb("owner", p?.Owner ?? ""));
                var active = new CheckBox { Content = "启用", IsChecked = p?.IsActive ?? true };
                _fields["active"] = active;
                _panel.Children.Add(active);
                break;
            }
            case "dept":
            {
                var d = _entity as Dept;
                AddRow("部门编码 *", Tb("code", d?.Code ?? NextCode("depts", "D")), "部门名称 *", Tb("name", d?.Name ?? ""));
                var active = new CheckBox { Content = "启用", IsChecked = d?.IsActive ?? true };
                _fields["active"] = active;
                _panel.Children.Add(active);
                break;
            }
            case "handler":
            {
                var h = _entity as Handler;
                AddRow("姓名 *", Tb("name", h?.Name ?? ""));
                var active = new CheckBox { Content = "启用", IsChecked = h?.IsActive ?? true };
                _fields["active"] = active;
                _panel.Children.Add(active);
                break;
            }
        }
    }

    private static string NextCode(string table, string prefix)
    {
        try
        {
            using var conn = AppSession.Db.Open();
            var last = new SqlCmd(conn, $"SELECT code FROM {table} ORDER BY id DESC LIMIT 1").Str();
            if (last == null) return prefix + "0001";
            var digits = new string(last.Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, out var n)) return prefix + (n + 1).ToString(new string('0', Math.Max(4, digits.Length)));
            return prefix + "0001";
        }
        catch { return prefix + "0001"; }
    }

    private string Text(string key) => _fields.TryGetValue(key, out var c)
        ? c switch { TextBox t => t.Text.Trim(), ComboBox cb => cb.Text.Trim(), _ => "" }
        : "";

    private string TagOf(string key)
    {
        if (_fields.TryGetValue(key, out var c) && c is ComboBox cb && cb.SelectedItem is ComboBoxItem it)
            return it.Tag?.ToString() ?? "";
        return "";
    }

    private bool Active() => !_fields.TryGetValue("active", out var c) || c is CheckBox cb && cb.IsChecked == true;

    private void Save()
    {
        try
        {
            // 1.1.2 修复“幽灵修改”：把表单值写入副本再保存，保存失败时列表行对象保持原样；
            // 之前直接就地修改行对象，失败后取消对话框，随后的“停用/启用”会把未确认的值写库。
            // 保存成功后由列表页 Reload() 从数据库刷新显示。
            switch (_type)
            {
                case "partner":
                {
                    var src = _entity as Partner;
                    var p = new Partner
                    {
                        Id = src?.Id ?? 0, Code = src?.Code ?? "", Kind = src?.Kind ?? "customer",
                        Name = src?.Name ?? "", Contact = src?.Contact, Phone = src?.Phone, Address = src?.Address,
                        Terms = src?.Terms, Level = src?.Level,
                        OpeningReceivableCents = src?.OpeningReceivableCents ?? 0,
                        OpeningPayableCents = src?.OpeningPayableCents ?? 0,
                        IsActive = src?.IsActive ?? true,
                    };
                    p.Name = Text("name");
                    p.Kind = TagOf("kind");
                    p.Contact = Text("contact");
                    p.Phone = Text("phone");
                    p.Address = Text("address");
                    p.Terms = Text("terms");
                    p.Level = Text("level");
                    p.OpeningReceivableCents = Money.ParseSignedOrZero(Text("openingAr"), "期初应收");
                    p.OpeningPayableCents = Money.ParseSignedOrZero(Text("openingAp"), "期初应付");
                    p.IsActive = Active();
                    MasterDataService.SavePartner(p);
                    break;
                }
                case "subject":
                {
                    var src = _entity as Subject;
                    var s = new Subject
                    {
                        Id = src?.Id ?? 0, Code = src?.Code ?? "", Name = src?.Name ?? "",
                        Direction = src?.Direction ?? "in", IsActive = src?.IsActive ?? true,
                    };
                    s.Code = Text("code");
                    s.Name = Text("name");
                    s.Direction = TagOf("dir");
                    s.IsActive = Active();
                    MasterDataService.SaveSubject(s);
                    break;
                }
                case "account":
                {
                    var src = _entity as Account;
                    var a = new Account
                    {
                        Id = src?.Id ?? 0, Code = src?.Code ?? "", Name = src?.Name ?? "", Kind = src?.Kind ?? "bank",
                        BankTail = src?.BankTail, SubjectCode = src?.SubjectCode,
                        OpeningCents = src?.OpeningCents ?? 0, IsActive = src?.IsActive ?? true,
                    };
                    a.Code = Text("code");
                    a.Name = Text("name");
                    a.Kind = TagOf("kind");
                    a.BankTail = Text("tail");
                    a.SubjectCode = Text("subject");
                    a.OpeningCents = Money.ParseSignedOrZero(Text("opening"), "期初余额");
                    a.IsActive = Active();
                    MasterDataService.SaveAccount(a);
                    break;
                }
                case "project":
                {
                    var src = _entity as Project;
                    var p = new Project
                    {
                        Id = src?.Id ?? 0, Code = src?.Code ?? "", Name = src?.Name ?? "",
                        DeptId = src?.DeptId, Owner = src?.Owner, IsActive = src?.IsActive ?? true,
                    };
                    p.Code = Text("code");
                    p.Name = Text("name");
                    p.DeptId = _fields["dept"] is ComboBox cb && cb.SelectedItem is ComboItem ci && ci.Id > 0 ? ci.Id : null;
                    p.Owner = Text("owner");
                    p.IsActive = Active();
                    MasterDataService.SaveProject(p);
                    break;
                }
                case "dept":
                {
                    var src = _entity as Dept;
                    var d = new Dept { Id = src?.Id ?? 0, Code = src?.Code ?? "", Name = src?.Name ?? "", IsActive = src?.IsActive ?? true };
                    d.Code = Text("code");
                    d.Name = Text("name");
                    d.IsActive = Active();
                    MasterDataService.SaveDept(d);
                    break;
                }
                case "handler":
                {
                    var src = _entity as Handler;
                    var h = new Handler { Id = src?.Id ?? 0, Name = src?.Name ?? "", IsActive = src?.IsActive ?? true };
                    h.Name = Text("name");
                    h.IsActive = Active();
                    MasterDataService.SaveHandler(h);
                    break;
                }
            }
            DialogResult = true;
        }
        catch (Exception ex) { Ui.Error(ex); }
    }
}
