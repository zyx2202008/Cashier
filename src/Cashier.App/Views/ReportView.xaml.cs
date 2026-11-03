using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Cashier.App.Printing;
using Cashier.Core;
using Cashier.Core.Services;

namespace Cashier.App.Views;

public class ReportGridRow
{
    public object?[] Cells { get; init; } = Array.Empty<object?>();
    public long? DrillId { get; init; }
    public string? DrillKind { get; init; }
}

public class CellConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var fmt = parameter?.ToString() ?? "text";
        if (value == null) return "";
        return fmt switch
        {
            "money" when value is long c => Money.Format(c),
            "percent" when value is decimal d => d.ToString("0.0%"),
            "int" when value is long l => l.ToString(),
            _ => value.ToString() ?? "",
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public partial class ReportView : UserControl, IAppPage
{
    private readonly string _reportId;
    private readonly ReportFilter _filter;
    private ReportResult? _result;
    private bool _loading = true;
    private bool _applyingQuick;
    private bool IsPeriodInvoiceCash => _reportId == "period_invoice_cash";
    private bool IsMonthlyDebt => _reportId is "supplier_monthly_payable" or "customer_monthly_receivable";

    public string PageTitle { get; }
    public string PageSubtitle => _result?.Basis ?? "报表口径加载中…";
    public string? Chip => null;

    public ReportView(string reportId, ReportFilter? preset = null)
    {
        InitializeComponent();
        _reportId = reportId;
        _filter = preset ?? new ReportFilter();
        PageTitle = reportId == "account_detail" ? "账户资金明细" : ReportService.Catalog.FirstOrDefault(c => c.id == reportId).title ?? "报表";

        var today = DateTime.Today;
        DateFrom.SelectedDate = DateTime.Parse(_filter.From);
        DateTo.SelectedDate = DateTime.Parse(_filter.To);
        if (DateFrom.SelectedDate == null) DateFrom.SelectedDate = new DateTime(today.Year, today.Month, 1);

        if (IsMonthlyDebt)
        {
            CmbYear.ItemsSource = Enumerable.Range(1990, 111).ToArray();
            CmbMonth.ItemsSource = Enumerable.Range(1, 12).ToArray();
            var selectedMonth = preset == null ? today : DateTime.Parse(_filter.From);
            CmbYear.SelectedItem = selectedMonth.Year;
            CmbMonth.SelectedItem = selectedMonth.Month;
            CmbPartner.IsEditable = true;
            CmbPartner.IsTextSearchEnabled = true;
        }

        if (IsPeriodInvoiceCash)
        {
            CmbDirection.ItemsSource = new[]
            {
                new ComboItem { Id = 0, Name = "销项" },
                new ComboItem { Id = 1, Name = "进项" },
            };
            CmbDirection.SelectedValue = _filter.InvoiceDirection == InvoiceKind.Out ? 0 : 1;
            CmbQuick.ItemsSource = new[] { "本月", "上月", "本季度", "本年度", "自定义" };
            CmbQuick.SelectedItem = preset == null ? "本月" : "自定义";
        }
        _loading = false;

        Loaded += (_, _) =>
        {
            LoadCombos();
            Run();
        };
    }

    public IEnumerable<UIElement> BuildActions()
    {
        var exp = Ui.Btn("导出 Excel", Export_Click, "Btn.Ghost");
        exp.Margin = new Thickness(0, 0, 6, 0);
        var prt = Ui.Btn("打印", Print_Click, "Btn.Ghost");
        prt.Margin = new Thickness(0, 0, 6, 0);
        var refresh = Ui.Btn("刷新", (_, _) => Run(), "Btn.Primary");
        return new UIElement[] { exp, prt, refresh };
    }

    private InvoiceKind SelectedDirection() =>
        (CmbDirection.SelectedItem as ComboItem)?.Id == 1 ? InvoiceKind.In : InvoiceKind.Out;

    private void LoadPartners()
    {
        string? kind = null;
        if (IsPeriodInvoiceCash) kind = SelectedDirection() == InvoiceKind.Out ? "customer" : "supplier";
        if (IsMonthlyDebt) kind = _reportId == "supplier_monthly_payable" ? "supplier" : "customer";
        var partners = new List<ComboItem> { new() { Id = 0, Name = "全部往来单位" } };
        partners.AddRange(MasterDataService.ListPartners(kind, true).Select(p => new ComboItem { Id = p.Id, Name = p.Name }));
        CmbPartner.ItemsSource = partners;
        CmbPartner.SelectedValue = _filter.PartnerId ?? 0;
        if (CmbPartner.SelectedIndex < 0) CmbPartner.SelectedIndex = 0;
    }

    private void LoadCombos()
    {
        LoadPartners();

        var projects = new List<ComboItem> { new() { Id = 0, Name = "全部项目" } };
        projects.AddRange(MasterDataService.ListProjects(true).Select(p => new ComboItem { Id = p.Id, Name = p.Name }));
        CmbProject.ItemsSource = projects;
        CmbProject.SelectedValue = _filter.ProjectId ?? 0;
        if (CmbProject.SelectedIndex < 0) CmbProject.SelectedIndex = 0;

        var accounts = new List<ComboItem> { new() { Id = 0, Name = "全部账户" } };
        accounts.AddRange(MasterDataService.ListAccounts(true).Select(a => new ComboItem { Id = a.Id, Name = a.Name }));
        CmbAccount.ItemsSource = accounts;
        CmbAccount.SelectedValue = _filter.AccountId ?? 0;
        if (CmbAccount.SelectedIndex < 0) CmbAccount.SelectedIndex = 0;

        // 只显示该报表适用的筛选
        var id = _reportId;
        BoxPartner.Visibility = id is "income_detail" or "expense_detail" or "cash_detail" or "receivable_detail"
            or "payable_detail" or "customer_summary" or "supplier_summary" or "invoice_out_detail" or "invoice_in_detail"
            or "period_invoice_cash" or "supplier_monthly_payable" or "customer_monthly_receivable" ? Visibility.Visible : Visibility.Collapsed;
        BoxProject.Visibility = id is "income_detail" or "expense_detail" or "project_month" or "project_year"
            or "receivable_detail" or "payable_detail" ? Visibility.Visible : Visibility.Collapsed;
        BoxAccount.Visibility = id is "cash_detail" or "account_detail" or "transfer_detail" or "account_report" or "account_month" or "account_year"
            ? Visibility.Visible : Visibility.Collapsed;
        BoxDirection.Visibility = IsPeriodInvoiceCash ? Visibility.Visible : Visibility.Collapsed;
        BoxQuick.Visibility = IsPeriodInvoiceCash ? Visibility.Visible : Visibility.Collapsed;
        BoxQuickButtons.Visibility = IsPeriodInvoiceCash || IsMonthlyDebt ? Visibility.Collapsed : Visibility.Visible;
        BoxDateRange.Visibility = IsMonthlyDebt ? Visibility.Collapsed : Visibility.Visible;
        BoxYear.Visibility = BoxMonth.Visibility = IsMonthlyDebt ? Visibility.Visible : Visibility.Collapsed;
        if (IsMonthlyDebt)
        {
            PartnerFilterLabel.Text = _reportId == "supplier_monthly_payable" ? "供应商名称" : "客户名称";
            QueryButton.Content = "刷新数据";
        }
    }

    public void Refresh() => Run();

    private void Run()
    {
        try
        {
            if (IsPeriodInvoiceCash && (DateFrom.SelectedDate == null || DateTo.SelectedDate == null))
                throw new AppException("统计开始日期和结束日期不能为空。");
            if (IsMonthlyDebt)
            {
                if (CmbYear.SelectedItem is not int year || CmbMonth.SelectedItem is not int month)
                    throw new AppException("请选择年度和月份。");
                if (CmbPartner.SelectedItem is not ComboItem) throw new AppException("请选择列表中的往来单位，或选择全部往来单位。");
                var monthStart = new DateTime(year, month, 1);
                _filter.From = monthStart.ToString("yyyy-MM-dd");
                _filter.To = monthStart.AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd");
            }
            else
            {
                _filter.From = (DateFrom.SelectedDate ?? DateTime.Today).ToString("yyyy-MM-dd");
                _filter.To = (DateTo.SelectedDate ?? DateTime.Today).ToString("yyyy-MM-dd");
            }
            _filter.PartnerId = (CmbPartner.SelectedItem as ComboItem)?.Id is > 0 and var pid ? pid : null;
            _filter.ProjectId = (CmbProject.SelectedItem as ComboItem)?.Id is > 0 and var prj ? prj : null;
            _filter.AccountId = (CmbAccount.SelectedItem as ComboItem)?.Id is > 0 and var acc ? acc : null;
            if (IsPeriodInvoiceCash) _filter.InvoiceDirection = SelectedDirection();

            _result = new ReportService().Build(_reportId, _filter);
            BuildColumns(_result);
            Grid.ItemsSource = _result.Rows.Select(r => new ReportGridRow
            {
                Cells = r.Cells, DrillId = r.DrillId, DrillKind = r.DrillKind,
            }).ToList();

            BasisText.Text = "口径：" + _result.Basis;
            NoteText.Text = _result.Note;
            FootCount.Text = $"共 {_result.Rows.Count} 行 · 统计期间 {_result.Period}";

            var totals = new List<string>();
            if (_result.TotalRow != null)
            {
                for (var i = 0; i < _result.Columns.Count; i++)
                {
                    var col = _result.Columns[i];
                    var v = _result.TotalRow.ElementAtOrDefault(i);
                    if (col.Format == "money" && v is long c) totals.Add($"{col.Header} {Money.Format(c)}");
                    else if (col.Format == "percent" && v is decimal d) totals.Add($"{col.Header} {d:0.0%}");
                    else if (col.Format == "int" && v is long n) totals.Add($"{col.Header} {n}");
                }
            }
            FootTotal.Text = totals.Count > 0 ? "合计：" + string.Join("   ", totals) : "";

            if (MainWindow.Instance?.IsShowing(this) == true) MainWindow.Instance.RefreshHeader();
        }
        catch (Exception ex)
        {
            if (IsMonthlyDebt) { _result = null; Grid.ItemsSource = null; FootCount.Text = ""; FootTotal.Text = ""; }
            Ui.Error(ex);
        }
    }

    private void BuildColumns(ReportResult r)
    {
        Grid.Columns.Clear();
        for (var i = 0; i < r.Columns.Count; i++)
        {
            var c = r.Columns[i];
            var col = c.Numeric ? new NumericColumn { RightAligned = true } : new DataGridTextColumn();
            col.Header = c.Header;
            col.Width = new DataGridLength(c.Width);
            col.Binding = new Binding($"Cells[{i}]")
            {
                Converter = new CellConverter(),
                ConverterParameter = c.Format,
            };
                var st = new Style(typeof(TextBlock));
                st.Setters.Add(new Setter(TextBlock.TextAlignmentProperty,c.Numeric?TextAlignment.Right:TextAlignment.Left));
                st.Setters.Add(new Setter(TextBlock.FontFamilyProperty, new FontFamily("Segoe UI, Microsoft YaHei UI")));
                st.Setters.Add(new Setter(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis));
                st.Setters.Add(new Setter(TextBlock.ToolTipProperty,new Binding("Text"){RelativeSource=new RelativeSource(RelativeSourceMode.Self)}));
                col.ElementStyle = st;
            Grid.Columns.Add(col);
        }
    }

    private void Query_Click(object sender, RoutedEventArgs e) => Run();

    private void Quick_Click(object sender, RoutedEventArgs e)
    {
        var today = DateTime.Today;
        var tag = (sender as Button)?.Tag?.ToString();
        if (tag == "month")
        {
            DateFrom.SelectedDate = new DateTime(today.Year, today.Month, 1);
            DateTo.SelectedDate = new DateTime(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));
        }
        else
        {
            DateFrom.SelectedDate = new DateTime(today.Year, 1, 1);
            DateTo.SelectedDate = new DateTime(today.Year, 12, 31);
        }
        Run();
    }

    private void Direction_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsPeriodInvoiceCash) return;
        LoadPartners();
        Run();
    }

    private void Date_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || _applyingQuick || !IsPeriodInvoiceCash) return;
        if (CmbQuick.SelectedItem as string != "自定义") CmbQuick.SelectedItem = "自定义";
    }

    private void QuickPeriod_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _applyingQuick || !IsPeriodInvoiceCash) return;
        var today = DateTime.Today;
        _applyingQuick = true;
        try
        {
            switch (CmbQuick.SelectedItem as string)
            {
                case "本月":
                    DateFrom.SelectedDate = new DateTime(today.Year, today.Month, 1);
                    DateTo.SelectedDate = today;
                    break;
                case "上月":
                    var first = new DateTime(today.Year, today.Month, 1).AddMonths(-1);
                    DateFrom.SelectedDate = first;
                    DateTo.SelectedDate = first.AddMonths(1).AddDays(-1);
                    break;
                case "本季度":
                    var quarterFirst = new DateTime(today.Year, (today.Month - 1) / 3 * 3 + 1, 1);
                    DateFrom.SelectedDate = quarterFirst;
                    DateTo.SelectedDate = today;
                    break;
                case "本年度":
                    DateFrom.SelectedDate = new DateTime(today.Year, 1, 1);
                    DateTo.SelectedDate = today;
                    break;
                default:
                    return;
            }
        }
        finally
        {
            _applyingQuick = false;
        }
        Run();
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_result == null) return;
            PrintHelper.ExportReport(_result);
        }
        catch (Exception ex) { Ui.Error(ex); }
    }

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_result == null) return;
            PrintHelper.PrintReport(_result);
        }
        catch (Exception ex) { Ui.Error(ex); }
    }

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsPeriodInvoiceCash)
        {
            if (Grid.SelectedItem is not ReportGridRow selected || selected.DrillId == null) return;
            try
            {
                var detail = new PeriodInvoiceCashDetailWindow(selected.DrillId.Value, _filter.From, _filter.To, _filter.InvoiceDirection)
                {
                    Owner = Window.GetWindow(this),
                };
                detail.ShowDialog();
            }
            catch (Exception ex) { Ui.Error(ex); }
            return;
        }
        if (_result?.DrillReportId == null) return;
        if (Grid.SelectedItem is not ReportGridRow row || row.DrillId == null) return;

        var filter = new ReportFilter { From = _filter.From, To = _filter.To };
        if (row.DrillKind == "partner") filter.PartnerId = row.DrillId;
        if (row.DrillKind == "account") filter.AccountId = row.DrillId;

        var win = new Window
        {
            Title = "明细 · " + (_result.DrillReportId == "account_detail" ? "账户资金明细" : ReportService.Catalog.FirstOrDefault(c => c.id == _result.DrillReportId).title ?? ""),
            Width = 1180,
            Height = 760,
            Owner = Window.GetWindow(this),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Ui.Br("B.Bg"),
            Content = new ReportView(_result.DrillReportId, filter),
        };
        win.ShowDialog();
    }
}
