using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Cashier.App;
using Cashier.App.Views;
using Cashier.Core;
using Cashier.Core.Services;
using ClosedXML.Excel;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1 || Directory.Exists(args[0])) throw new Exception("Fresh isolated data directory required");
        var app = new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/Cashier;component/Theme/Styles.xaml",UriKind.Absolute)});
        AppSession.Db = new Database(args[0]); AppSession.Db.EnsureCreated();
        AppSession.Current = new AuthService().Login("admin", "admin888");
        var sup = MasterDataService.SavePartner(new Partner { Name="测试供应商甲", Kind="supplier" });
        var other = MasterDataService.SavePartner(new Partner { Name="测试供应商乙", Kind="supplier" });
        var cust = MasterDataService.SavePartner(new Partner { Name="测试客户甲", Kind="customer" });
        using (var c = AppSession.Db.Open())
            new SqlCmd(c, @"INSERT INTO vouchers(doc_no,kind,nature,biz_date,period,amount_cents,partner_id,summary,status,created_at,created_by)
              VALUES('P1','payable','None','2024-02-10','202402',100000,$s,'采购','audited','2024-02-10','test'),
              ('P2','payable','None','2024-02-11','202402',250000,$o,'采购','audited','2024-02-11','test'),
              ('P3','payable','None','2024-03-10','202403',70000,$s,'采购','audited','2024-03-10','test'),
              ('R1','receivable','None','2024-02-10','202402',80000,$c,'销售','audited','2024-02-10','test')")
              .P("$s",sup).P("$o",other).P("$c",cust).Exec();
        int count=0, failed=0;
        void Check(string n,bool b){count++;if(!b)failed++;Console.WriteLine($"{(b?"PASS":"FAIL")} | {n}");}
        T Field<T>(ReportView view,string name) where T:FrameworkElement => (T)view.FindName(name);
        void Query(ReportView view) => typeof(ReportView).GetMethod("Run",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(view,null);
        ReportResult Result(ReportView view) => (ReportResult)typeof(ReportView).GetField("_result",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(view)!;
        foreach (var (id,partner,expectedTitle,expectedAmount) in new[] {
            ("supplier_monthly_payable",sup,"供应商应付汇总统计月报表",100000L),
            ("customer_monthly_receivable",cust,"客户应收汇总统计月报表",80000L) })
        {
            var view=new ReportView(id);
            typeof(ReportView).GetMethod("LoadCombos",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(view,null);
            var year=Field<ComboBox>(view,"CmbYear"); var month=Field<ComboBox>(view,"CmbMonth");
            var partners=Field<ComboBox>(view,"CmbPartner");
            Check(id+" title and year month controls", view.PageTitle==expectedTitle && Field<StackPanel>(view,"BoxYear").Visibility==Visibility.Visible && Field<StackPanel>(view,"BoxMonth").Visibility==Visibility.Visible);
            Check(id+" partner supports text search",partners.IsEditable && partners.IsTextSearchEnabled && partners.Items.Cast<ComboItem>().Any(x=>x.Id==partner));
            Check(id+" filter labels", Field<TextBlock>(view,"PartnerFilterLabel").Text == (id.StartsWith("supplier") ? "供应商名称" : "客户名称") && Field<Button>(view,"QueryButton").Content?.ToString() == "刷新数据");
            year.SelectedItem=2024; month.SelectedItem=2; partners.SelectedValue=partner;
            Query(view);
            var r=Result(view); var grid=Field<DataGrid>(view,"Grid");
            Check(id+" filtered February grid",r.Period=="2024-02"&&r.Rows.Count==1&&(long)r.Rows[0].Cells[3]! == expectedAmount&&grid.Items.Count==1&&grid.Columns.Count==9&&grid.Columns[8].Header?.ToString()=="本月余额调整");
            Check(id+" footer total",Field<TextBlock>(view,"FootTotal").Text.Contains(Money.Format(expectedAmount)));
            Check(id+" export action present",view.BuildActions().OfType<Button>().Any(x=>x.Content?.ToString()=="导出 Excel"));
            var path=Path.Combine(args[0],id+".xlsx"); ExcelService.ExportReport(r,path);
            using(var wb=new XLWorkbook(path)){var ws=wb.Worksheet(1);Check(id+" Excel matches displayed query and total",ws.Cell(6,4).GetDouble()==expectedAmount/100.0&&ws.Cell(7,4).GetDouble()==expectedAmount/100.0&&ws.Cell(7,1).GetString()=="合计");}
            month.SelectedItem=3; Query(view); var march=Result(view);
            Check(id+" refresh applies month",march.Period=="2024-03"&&(long)march.Rows[0].Cells[3]! == (id.StartsWith("supplier")?70000L:0L));
            if(id.StartsWith("supplier")){month.SelectedItem=2;partners.SelectedValue=other;Query(view);Check("supplier filter changes displayed data",(long)Result(view).Rows[0].Cells[3]! == 250000L);}
        }
        var preset = new ReportView("supplier_monthly_payable",new ReportFilter{From="2024-02-01",To="2024-02-29",PartnerId=sup});
        typeof(ReportView).GetMethod("LoadCombos",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(preset,null);
        Query(preset);
        Check("preset month and supplier retained",Result(preset).Period=="2024-02"&&Result(preset).Rows.Count==1&&Field<ComboBox>(preset,"CmbYear").SelectedItem is 2024);
        Console.WriteLine($"RESULT checks={count} failures={failed}"); return failed==0?0:1;
    }
}
