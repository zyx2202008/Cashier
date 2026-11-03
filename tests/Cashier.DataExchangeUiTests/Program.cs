using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cashier.App.Views;
using Cashier.Core;
using Cashier.Core.Services;
using ClosedXML.Excel;

internal static class Program
{
    [STAThread]private static int Main(string[] args)
    {
        if(args.Length!=1||Directory.Exists(args[0]))throw new Exception("Fresh isolated test root required");Directory.CreateDirectory(args[0]);
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/Cashier;component/Theme/Styles.xaml",UriKind.Absolute)});
        AppSession.Db=new Database(Path.Combine(args[0],"ledger"));AppSession.Db.EnsureCreated();var auth=new AuthService();auth.Login("admin","admin888");
        int checks=0,failed=0;void Check(string name,bool ok){checks++;if(!ok)failed++;Console.WriteLine($"{(ok?"PASS":"FAIL")} | {name}");}
        T Field<T>(object value,string name)=>(T)value.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(value)!;
        object? Invoke(object value,string name,params object[] values)=>value.GetType().GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(value,values);
        IEnumerable<T> Descend<T>(DependencyObject root) where T:DependencyObject
        {
            if(root is T match)yield return match;
            foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())foreach(var found in Descend<T>(child))yield return found;
        }
        var import=new ImportView();var tabs=Descend<TabControl>(import).Single();
        Check("four import export workflow tabs",tabs.Items.Count==4&&((TabItem)tabs.Items[2]).Header?.ToString()=="基础资料整理"&&((TabItem)tabs.Items[3]).Header?.ToString()=="整账套迁移");
        var master=(MasterDataExchangeView)((TabItem)tabs.Items[2]).Content;
        Field<ComboBox>(master,"_kind").SelectedValue=MasterExchangeKind.Dept;
        var excel=Path.Combine(args[0],"dept.xlsx");MasterDataExchangeService.Export(MasterExchangeKind.Dept,excel,true);
        using(var w=new XLWorkbook(excel)){var s=w.Worksheet("资料");s.Cell(2,2).Value="UI-DEPT";s.Cell(2,3).Value="界面导入部门";s.Cell(2,4).Value="1";s.Cell(3,2).Value="BAD";s.Cell(3,4).Value="1";w.Save();}
        Invoke(master,"LoadFile",excel);Check("invalid preview disables commit",!Field<Button>(master,"_commit").IsEnabled&&Field<Button>(master,"_validation").IsEnabled);
        Check("preview displays exact error row",Field<DataGrid>(master,"_grid").Items.Cast<MasterExchangeRow>().Any(r=>r.RowNo==3&&r.Error!=null));
        using(var w=new XLWorkbook(excel)){w.Worksheet("资料").Row(3).Delete();w.Save();}
        Invoke(master,"LoadFile",excel);Check("corrected preview enables commit",Field<Button>(master,"_commit").IsEnabled);
        Check("actual view commit saves one record",(int)Invoke(master,"CommitPrepared")! ==1&&MasterDataService.ListDepts().Any(x=>x.Code=="UI-DEPT"));
        Check("successful commit disabled",!Field<Button>(master,"_commit").IsEnabled);
        var package=Path.Combine(args[0],"ledger.cashier");AppSession.Db.ExportDataPackage(package);
        var native=(DataPackageView)((TabItem)tabs.Items[3]).Content;
        Invoke(native,"LoadPackage",package);Check("native preview lists all categories",Field<DataGrid>(native,"_grid").Items.Count==20&&Field<Button>(native,"_import").IsEnabled);
        var target=Path.Combine(args[0],"imported-ledger");Invoke(native,"ImportInto",target);
        Check("actual view import creates new ledger",File.Exists(Path.Combine(target,"cashier.db"))&&Field<Button>(native,"_open").IsEnabled);
        Check("native view preserves active source",AppSession.Db.DataDir==Path.GetFullPath(Path.Combine(args[0],"ledger")));
        foreach(var role in new[]{UserRole.Accountant,UserRole.Cashier,UserRole.Viewer})
        {
            auth.Login("admin","admin888");auth.Create("ui_"+role,"ui_"+role,"testing123",role);auth.Login("ui_"+role,"testing123");
            var roleMaster=new MasterDataExchangeView();
            Check(role+" expected master export permission",Descend<Button>(roleMaster).Single(b=>b.Content?.ToString()=="导出当前资料…").IsEnabled==(role==UserRole.Accountant));
            var roleNative=new DataPackageView();
            Check(role+" expected native export permission",Descend<Button>(roleNative).Single(b=>b.Content?.ToString()=="导出整账套…").IsEnabled==(role==UserRole.Accountant));
            Check(role+" native import selection disabled",!Descend<Button>(roleNative).Single(b=>b.Content?.ToString()=="选择整账套文件…").IsEnabled);
        }
        auth.Login("admin","admin888");
        foreach(var(width,height) in new[]{(1024,768),(1280,800),(1600,900)})
        {
            var page=new ImportView();var control=Descend<TabControl>(page).Single();var window=new Window{Width=width,Height=height,Content=page,WindowStyle=WindowStyle.None,ShowInTaskbar=false};window.Show();
            foreach(var selected in new[]{2,3})
            {
                control.SelectedIndex=selected;window.UpdateLayout();
                var content=(UserControl)((TabItem)control.Items[selected]).Content;
                Check($"{width} tab{selected} usable viewport",content.ActualWidth>800&&content.ActualHeight>500);
                Check($"{width} tab{selected} primary business buttons hidden",((Grid)page.Content).Children.OfType<WrapPanel>().First().Visibility==Visibility.Collapsed);
                if(width==1280)
                {
                    if(content is DataPackageView p)Invoke(p,"LoadPackage",package);
                    window.UpdateLayout();var image=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(window);
                    var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(Path.Combine(args[0],selected==2?"基础资料整理.png":"整账套迁移.png"));encoder.Save(file);
                }
            }
            window.Close();
        }
        Console.WriteLine($"RESULT checks={checks} failures={failed}");app.Shutdown();return failed==0?0:1;
    }
}
