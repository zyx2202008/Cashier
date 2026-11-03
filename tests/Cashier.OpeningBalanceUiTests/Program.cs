using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cashier.App;
using Cashier.App.Views;
using Cashier.App.Printing;
using Cashier.Core;
using Cashier.Core.Services;

internal static class Program
{
    [STAThread] private static int Main(string[] args)
    {
        if(args.Length!=1||Directory.Exists(args[0]))throw new Exception("Fresh isolated data directory required");
        Directory.CreateDirectory(args[0]);
        // Load production styles without the production App startup handler or default-ledger access.
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/Cashier;component/Theme/Styles.xaml",UriKind.Absolute)});
        AppSession.Db=new Database(Path.Combine(args[0],"data"));AppSession.Db.EnsureCreated();
        var auth=new AuthService();auth.Login("admin","admin888");
        var account=MasterDataService.SaveAccount(new Account{Code="UI-1",Name="界面测试账户",OpeningCents=10000});
        var customer=MasterDataService.SavePartner(new Partner{Name="界面测试客户",Kind="customer",OpeningReceivableCents=20000});
        var supplier=MasterDataService.SavePartner(new Partner{Name="界面测试供应商",Kind="supplier",OpeningPayableCents=30000});
        int checks=0,failed=0;
        void Check(string label,bool ok){checks++;if(!ok)failed++;Console.WriteLine($"{(ok?"PASS":"FAIL")} | {label}");}
        T Field<T>(object value,string name)=>(T)value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(value)!;
        void Invoke(object value,string method)=>value.GetType().GetMethod(method,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(value,null);
        bool Save(Window window,Action prepare)
        {
            bool timeout=false;
            var watchdog=new DispatcherTimer{Interval=TimeSpan.FromSeconds(12)};
            watchdog.Tick+=(_,_)=>{timeout=true;foreach(var w in Application.Current.Windows.OfType<Window>().ToArray())w.Close();watchdog.Stop();};
            window.Loaded+=(_,_)=>window.Dispatcher.BeginInvoke(new Action(()=>{prepare();Invoke(window,"Save");}),DispatcherPriority.ApplicationIdle);
            watchdog.Start();var result=window.ShowDialog()==true;watchdog.Stop();Check("dialog save completes without timeout",!timeout);return result;
        }
        var view=new OpeningBalanceView();view.Refresh();
        Check("admin can confirm setup",Field<Button>(view,"_confirm").IsEnabled);
        Check("adjustment disabled before lock",!Field<Button>(view,"_add").IsEnabled);
        var confirm=new OpeningConfirmDialog();
        Check("empty ledger confirmation defaults to today",Field<DatePicker>(confirm,"_date").SelectedDate==DateTime.Today);
        Check("actual confirmation dialog saves",Save(confirm,()=>Field<TextBox>(confirm,"_reason").Text="界面核对后确认"));
        view.Refresh();Check("confirmed page disables lock and enables adjustment",!Field<Button>(view,"_confirm").IsEnabled&&Field<Button>(view,"_add").IsEnabled);
        Check("locked info visible",Field<TextBlock>(view,"_state").Text.Contains("期初已锁定"));
        foreach(var (kind,target,amount) in new[]{("account",account,"200.00"),("receivable",customer,"-150.00"),("payable",supplier,"80.00")})
        {
            var dialog=new BalanceAdjustmentDialog(null);
            Check(kind+" dialog save through actual UI method",Save(dialog,()=>{
                Field<ComboBox>(dialog,"_kind").SelectedValue=kind;Field<ComboBox>(dialog,"_target").SelectedValue=target;
                Field<TextBox>(dialog,"_amount").Text=amount;Field<TextBox>(dialog,"_reason").Text="界面录入核对";
            }));
        }
        view.Refresh();var grid=Field<DataGrid>(view,"_grid");Check("three persisted records visible",grid.Items.Count==3&&grid.Columns.Count==11);
        var original=OpeningBalanceService.Query(kind:"account").Single();
        var reverse=new BalanceAdjustmentDialog(original);Check("reversal amount readonly",Field<TextBox>(reverse,"_amount").IsReadOnly);
        Check("reversal target disabled",!Field<ComboBox>(reverse,"_target").IsEnabled);
        Check("actual reversal dialog saves",Save(reverse,()=>Field<TextBox>(reverse,"_reason").Text="界面反向核对"));
        Check("reversal preserves original and appends record",OpeningBalanceService.Query().Count==4);
        Check("account returns to original balance",MasterDataService.AccountBalance(account)==10000);
        var ctor=typeof(MasterDataDialog).GetConstructor(BindingFlags.Instance|BindingFlags.NonPublic,null,new[]{typeof(string),typeof(object)},null)!;
        using(var conn=AppSession.Db.Open())
        {
            var md=(MasterDataDialog)ctor.Invoke(new object?[]{"account",MasterDataService.GetAccount(conn,account)});
            Check("locked account opening control disabled",!Field<Dictionary<string,Control>>(md,"_fields")["opening"].IsEnabled);md.Close();
        }
        var pd=(MasterDataDialog)ctor.Invoke(new object?[]{"partner",MasterDataService.GetPartner(customer)});
        Check("both partner opening controls disabled",!Field<Dictionary<string,Control>>(pd,"_fields")["openingAr"].IsEnabled&&!Field<Dictionary<string,Control>>(pd,"_fields")["openingAp"].IsEnabled);pd.Close();
        foreach(var role in new[]{UserRole.Accountant,UserRole.Cashier,UserRole.Viewer})
        {
            auth.Login("admin","admin888");auth.Create("ui_"+role,"ui_"+role,"testing123",role);auth.Login("ui_"+role,"testing123");
            var page=new OpeningBalanceView();page.Refresh();
            Check(role+" cannot confirm",!Field<Button>(page,"_confirm").IsEnabled);
            Check(role+" expected adjustment permissions",Field<Button>(page,"_add").IsEnabled==(role==UserRole.Accountant));
            Check(role+" expected reverse permissions",Field<Button>(page,"_reverse").IsEnabled==(role==UserRole.Accountant));
            Check(role+" reads history",Field<DataGrid>(page,"_grid").Items.Count==4);
        }
        auth.Login("admin","admin888");
        foreach(var (width,height) in new[]{(1024,768),(1280,800),(1600,900)})
        {
            var page=new OpeningBalanceView();page.Refresh();var window=new Window{Width=width,Height=height,Content=page,ShowInTaskbar=false,WindowStyle=WindowStyle.None};
            window.Show();window.UpdateLayout();
            Check($"{width} grid has working viewport",Field<DataGrid>(page,"_grid").ActualHeight>200&&Field<DataGrid>(page,"_grid").ActualWidth>800);
            Check($"{width} action bar visible",Field<Button>(page,"_add").ActualHeight>15&&Field<Button>(page,"_confirm").ActualHeight>15);
            if(width==1280)
            {
                var image=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(window);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var output=File.Create(Path.Combine(args[0],"期初与余额调整界面.png"));encoder.Save(output);
            }
            window.Close();
        }
        var rs=new ReportService();
        foreach(var id in new[]{"account_report","account_detail","account_month","account_year","customer_summary","supplier_summary","customer_monthly_receivable","supplier_monthly_payable"})
        {
            var start=new DateTime(DateTime.Today.Year,DateTime.Today.Month,1);
            var report=rs.Build(id,new(){From=start.ToString("yyyy-MM-dd"),To=start.AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd")});
            Check(id+" row widths match columns",report.Rows.All(row=>row.Cells.Length==report.Columns.Count));
            Check(id+" print document builds",PrintHelper.Build(PrintHelper.BuildReportTable(report)).Pages.Count>0);
        }
        Console.WriteLine($"RESULT checks={checks} failures={failed}");app.Shutdown();return failed==0?0:1;
    }
}
