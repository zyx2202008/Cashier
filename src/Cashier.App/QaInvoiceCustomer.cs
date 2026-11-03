using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Cashier.App.Views;
using Cashier.Core;
using Cashier.Core.Services;

namespace Cashier.App;

/// <summary>Explicit opt-in UI acceptance driver. Only operates in an explicitly selected, synthetic ledger.</summary>
public static class QaInvoiceCustomer
{
    public static bool Run(string output)
    {
        Directory.CreateDirectory(output);
        var log=new List<string>();
        void Check(string name,bool ok) { var line=$"{(ok?"PASS":"FAIL")} | {name}";log.Add(line);Console.WriteLine(line);File.AppendAllText(Path.Combine(output,"ui-progress.txt"),line+Environment.NewLine);if(!ok)throw new Exception(name); }
        T C<T>(InvoiceWindow w,string name) where T:FrameworkElement => (T)w.FindName(name);
        void Click(InvoiceWindow w,string name) => C<Button>(w,name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        long Count(string table){using var conn=AppSession.Db.Open();return new SqlCmd(conn,"SELECT COUNT(*) FROM "+table).Long();}
        void SendKey(InvoiceWindow w,Key key) => w.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(w),0,key){RoutedEvent=Keyboard.PreviewKeyDownEvent});
        void Fill(InvoiceWindow w,string no)
        {
            C<TextBox>(w,"TxtNo").Text=no;C<TextBox>(w,"TxtCode").Text="001234";
            C<DatePicker>(w,"DpReg").SelectedDate=new DateTime(2024,3,10);C<DatePicker>(w,"DpDeliver").SelectedDate=new DateTime(2024,3,12);
            C<TextBox>(w,"TxtGross").Text="113.00";C<ComboBox>(w,"CmbRate").Text="13";
            C<TextBox>(w,"TxtNet").Text="100.01";C<TextBox>(w,"TxtTax").Text="12.99";
            C<TextBox>(w,"TxtContent").Text="票面内容";C<TextBox>(w,"TxtNote").Text="票面备注";
        }
        string Fields(InvoiceWindow w)=>JsonSerializer.Serialize(new{Text=new[]{"TxtNo","TxtCode","TxtGross","TxtNet","TxtTax","TxtContent","TxtNote"}.Select(n=>C<TextBox>(w,n).Text).ToArray(),Rate=C<ComboBox>(w,"CmbRate").Text,Date=C<DatePicker>(w,"DpReg").SelectedDate,Deliver=C<DatePicker>(w,"DpDeliver").SelectedDate});
        void Dialog(InvoiceKind kind,Invoice? invoice,Action<InvoiceWindow> action)
        {
            var w=new InvoiceWindow(kind,invoice) { QaSavedNotice=()=>Check("invoice saved notification reached",true) };Exception? error=null;
            w.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>{try{action(w);}catch(Exception ex){error=ex;}finally{if(w.IsVisible)w.Close();}}));
            w.ShowDialog();if(error!=null)throw error;
        }
        AppSession.Current=new AuthService().Login("admin","admin888");
        var marker=Path.Combine(output,"saved-customer-id.txt");
        try
        {
            if(File.Exists(marker))
            {
                var id=long.Parse(File.ReadAllText(marker));
                Dialog(InvoiceKind.Out,null,w=>{Check("restart customer candidate from SQLite",C<ComboBox>(w,"CmbPartner").Items.Cast<ComboItem>().Any(p=>p.Id==id));C<ComboBox>(w,"CmbPartner").SelectedValue=id;Check("restart selected same real ID",((ComboItem)C<ComboBox>(w,"CmbPartner").SelectedItem).Id==id);});
                var invoice=new InvoiceService().List(InvoiceKind.Out,"UI-QUICK",id,null,null).Single();
                Check("restart persisted invoice PartnerId and manual tax",invoice.PartnerId==id&&invoice.NetCents==10001&&invoice.TaxCents==1299);
                Check("restart database version is current",AppSession.Db.GetUserVersion()==Database.CurrentVersion);
                long previousSequence, previousSupplierSequence;
                using (var conn = AppSession.Db.Open())
                {
                    previousSequence = new SqlCmd(conn,"SELECT v FROM meta WHERE k='partner_code_sequence_v3.KH'").Long();
                    previousSupplierSequence = new SqlCmd(conn,"SELECT v FROM meta WHERE k='partner_code_sequence_v3.GYS'").Long();
                }
                var afterRestart = MasterDataService.CreateCustomerForInvoice(new("重启后编码验证-" + Guid.NewGuid().ToString("N")));
                Check("restart preserves customer code sequence", afterRestart.Outcome == InvoiceCustomerOutcome.Created &&
                    afterRestart.Code == "KH-" + (previousSequence + 1).ToString("D4"));
                var supplierId = MasterDataService.SavePartner(new Partner { Name = "重启后供应商", Kind = "supplier" });
                Check("restart preserves supplier code sequence", MasterDataService.GetPartner(supplierId)!.Code ==
                    "GYS-" + (previousSupplierSequence + 1).ToString("D4"));
                File.WriteAllLines(Path.Combine(output,"ui-restart.txt"),log);return true;
            }
            if(Count("partners")!=0 || Count("invoices")!=0)throw new Exception("First UI run requires an empty synthetic ledger");
            long created=0;
            Dialog(InvoiceKind.Out,null,w=>
            {
                Fill(w,"UI-QUICK");var before=Fields(w);Click(w,"BtnAddCustomer");
                Check("empty directory quick entry opens and invoice default blocked",C<Border>(w,"QuickCustomerPanel").IsVisible&&!C<Button>(w,"BtnSaveInvoice").IsEnabled&&!C<Button>(w,"BtnSaveInvoice").IsDefault);
                SendKey(w,Key.Enter);Check("Enter never submits invoice while adding",Count("invoices")==0);
                Click(w,"BtnSaveQuickCustomer");Check("empty name validation keeps fields and panel",Count("partners")==0&&Fields(w)==before&&C<Border>(w,"QuickCustomerPanel").IsVisible);
                SendKey(w,Key.Escape);Check("Esc cancels quick area only",w.IsVisible&&!C<Border>(w,"QuickCustomerPanel").IsVisible&&Fields(w)==before&&Count("partners")==0);
                Click(w,"BtnAddCustomer");
                var input=C<TextBox>(w,"TxtQuickName");input.Focus();
                var composition=new TextComposition(InputManager.Current,input,"中文客户",TextCompositionAutoComplete.Off);TextCompositionManager.StartComposition(composition);
                SendKey(w,Key.ImeProcessed);Check("IME composition cannot submit invoice",Count("invoices")==0&&!C<Button>(w,"BtnSaveInvoice").IsDefault);
                TextCompositionManager.CompleteComposition(composition);input.Text="新建中文客户";
                for(int i=0;i<8;i++)((UIElement)Keyboard.FocusedElement).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                Check("Tab remains in quick area",IsWithin(Keyboard.FocusedElement as DependencyObject,C<Border>(w,"QuickCustomerPanel")));
                foreach(double scale in new[]{1.0,1.25,1.5})
                {
                    w.Width=600;w.Height=420;((FrameworkElement)w.Content).LayoutTransform=new ScaleTransform(scale,scale);w.UpdateLayout();
                    C<Button>(w,"BtnSaveQuickCustomer").BringIntoView();Pump();
                    var button=C<Button>(w,"BtnSaveQuickCustomer");var scroll=C<ScrollViewer>(w,"InvoiceScroll");
                    var middle=button.TransformToAncestor(scroll).Transform(new Point(button.ActualWidth/2,button.ActualHeight/2));
                    Check("minimum window quick save reachable at scale "+scale,button.IsVisible&&button.ActualWidth>100&&middle.Y>=0&&middle.Y<=scroll.ActualHeight);
                }
                ((FrameworkElement)w.Content).LayoutTransform=Transform.Identity;w.Width=680;w.Height=680;
                C<TextBox>(w,"TxtQuickPhone").Text="021-12345678 分机 9";
                QaTools.CaptureWindow(w,Path.Combine(output,"quick-panel.png"));
                Click(w,"BtnSaveQuickCustomer");created=((ComboItem)C<ComboBox>(w,"CmbPartner").SelectedItem).Id;
                Check("new customer selected by returned ID",created>0&&w.PartnerDirectoryChanged&&!C<Border>(w,"QuickCustomerPanel").IsVisible&&Count("invoices")==0);
                Check("all invoice fields including adjusted tax unchanged",Fields(w)==before);
                Check("success makes unsaved invoice explicit",C<TextBlock>(w,"CustomerSavedMessage").Text=="客户已保存并选用，发票尚未保存");
                Click(w,"BtnSaveInvoice");
                Check("actual invoice save closes dialog",w.DialogResult==true);
            });
            File.WriteAllText(marker,created.ToString());
            var saved=new InvoiceService().List(InvoiceKind.Out,"UI-QUICK",created,null,null).Single();Check("saved invoice binds selected new customer",saved.PartnerId==created&&saved.TaxCents==1299);
            long canceled=0;
            Dialog(InvoiceKind.Out,null,w=>
            {
                Fill(w,"CANCEL");var cb=C<ComboBox>(w,"CmbPartner");cb.SelectedValue=created;
                C<ComboBox>(w,"CmbOriginal").SelectedValue=saved.Id;var before=Fields(w);
                Click(w,"BtnAddCustomer");C<TextBox>(w,"TxtQuickName").Text="不保存客户";Click(w,"BtnCancelQuickCustomer");
                Check("cancel quick preserves customer and original ID",Convert.ToInt64(cb.SelectedValue)==created&&Convert.ToInt64(C<ComboBox>(w,"CmbOriginal").SelectedValue)==saved.Id&&Fields(w)==before);
                cb.SelectedIndex=-1;Check("no customer clears stale original candidates",C<ComboBox>(w,"CmbOriginal").Items.Count==1&&Convert.ToInt64(C<ComboBox>(w,"CmbOriginal").SelectedValue)==0);
                Click(w,"BtnAddCustomer");C<TextBox>(w,"TxtQuickName").Text="保存客户后取消发票";Click(w,"BtnSaveQuickCustomer");canceled=((ComboItem)cb.SelectedItem).Id;
                Check("new customer clears previous red original",Convert.ToInt64(C<ComboBox>(w,"CmbOriginal").SelectedValue)==0);
            });
            Check("cancel whole invoice retains created customer",MasterDataService.GetPartner(canceled)!=null&&Count("invoices")==1);
            Dialog(InvoiceKind.Out,null,w=>
            {
                Fill(w,"UI-FAILURE");var fields=Fields(w);var before=Count("partners");Click(w,"BtnAddCustomer");C<TextBox>(w,"TxtQuickName").Text="界面数据库失败";
                using(var conn=AppSession.Db.Open())new SqlCmd(conn,"CREATE TRIGGER qa_fail_customer BEFORE INSERT ON partners BEGIN SELECT RAISE(ABORT,'qa failure'); END;").Exec();
                try{Click(w,"BtnSaveQuickCustomer");Check("UI save failure keeps inputs and restores buttons",Fields(w)==fields&&C<TextBox>(w,"TxtQuickName").Text=="界面数据库失败"&&C<Button>(w,"BtnSaveQuickCustomer").IsEnabled&&C<Border>(w,"QuickCustomerPanel").IsVisible&&Count("partners")==before);}
                finally{using var conn=AppSession.Db.Open();new SqlCmd(conn,"DROP TRIGGER qa_fail_customer;").Exec();}
            });
            Dialog(InvoiceKind.Out,null,w=>{Click(w,"BtnAddCustomer");C<TextBox>(w,"TxtQuickName").Text="新建中文客户";Click(w,"BtnSaveQuickCustomer");Check("duplicates displayed without automatic selection",C<ListBox>(w,"DuplicateCandidates").Items.Count==1&&C<ListBox>(w,"DuplicateCandidates").SelectedIndex==-1);C<ListBox>(w,"DuplicateCandidates").SelectedIndex=0;C<TextBox>(w,"TxtQuickPhone").Text="不应覆盖";Click(w,"BtnUseExisting");Check("explicit existing selection preserves original details",Convert.ToInt64(C<ComboBox>(w,"CmbPartner").SelectedValue)==created&&MasterDataService.GetPartner(created)!.Phone=="021-12345678 分机 9"&&!w.PartnerDirectoryChanged);});
            Dialog(InvoiceKind.Out,null,w=>{Click(w,"BtnAddCustomer");C<TextBox>(w,"TxtQuickName").Text="新建中文客户";Click(w,"BtnSaveQuickCustomer");w.QaDistinctCustomerConfirmation=()=>false;Click(w,"BtnConfirmDuplicate");Check("declining duplicate confirmation creates nothing",!w.PartnerDirectoryChanged);bool asked=false;w.QaDistinctCustomerConfirmation=()=>{asked=true;return true;};Click(w,"BtnConfirmDuplicate");Check("explicit second confirmation creates separate customer",asked&&w.PartnerDirectoryChanged&&Convert.ToInt64(C<ComboBox>(w,"CmbPartner").SelectedValue)!=created);});
            Dialog(InvoiceKind.In,null,w=>Check("input invoice flow has no quick customer entry",C<Button>(w,"BtnAddCustomer").Visibility==Visibility.Collapsed));
            var old=MasterDataService.GetPartner(created)!;old.IsActive=false;MasterDataService.SavePartner(old);
            Dialog(InvoiceKind.Out,saved,w=>Check("edit preserves inactive customer without quick entry",C<Button>(w,"BtnAddCustomer").Visibility==Visibility.Collapsed&&Convert.ToInt64(C<ComboBox>(w,"CmbPartner").SelectedValue)==created));
            old.IsActive=true;MasterDataService.SavePartner(old);
            var admin=AppSession.Current!;
            var auth=new AuthService();auth.Create("qa_cashier","出纳","qa-test-4821",UserRole.Cashier);
            auth.Login("qa_cashier","qa-test-4821");
            Dialog(InvoiceKind.Out,null,w=>Check("cashier quick entry disabled, existing IDs selectable",!C<Button>(w,"BtnAddCustomer").IsEnabled&&C<ComboBox>(w,"CmbPartner").Items.Count>0));AppSession.Current=admin;
            var parent=new InvoiceView(InvoiceKind.Out);var owner=new Window{Content=parent,Width=1100,Height=700};owner.Show();Pump();
            var filter=(ComboBox)parent.FindName("CmbPartner");filter.SelectedItem=filter.Items.Cast<ComboItem>().Single(x=>x.Id==created);
            long fromParent=0;Exception? parentError=null;
            owner.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>{var child=Application.Current.Windows.OfType<InvoiceWindow>().Single();try{Click(child,"BtnAddCustomer");C<TextBox>(child,"TxtQuickName").Text="父页面取消刷新客户";Click(child,"BtnSaveQuickCustomer");fromParent=Convert.ToInt64(C<ComboBox>(child,"CmbPartner").SelectedValue);child.Close();}catch(Exception ex){parentError=ex;child.Close();}}));
            ((Button)parent.BuildActions().First()).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if(parentError!=null)throw parentError;
            Check("parent candidates refresh even after invoice cancel",filter.Items.Cast<ComboItem>().Any(x=>x.Id==fromParent)&&((ComboItem)filter.SelectedItem).Id==created);
            owner.Close();
            Dialog(InvoiceKind.Out,null,w=>Check("next registration reads saved customer",C<ComboBox>(w,"CmbPartner").Items.Cast<ComboItem>().Any(p=>p.Id==fromParent)));
            Check("user version is current",AppSession.Db.GetUserVersion()==Database.CurrentVersion);
            File.WriteAllLines(Path.Combine(output,"ui-first.txt"),log);return true;
        }
        catch(Exception ex){log.Add("FAIL | "+ex);File.WriteAllLines(Path.Combine(output,"ui-failure.txt"),log);return false;}
    }
    private static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}
    private static bool IsWithin(DependencyObject? element,DependencyObject parent){while(element!=null){if(ReferenceEquals(element,parent))return true;element=VisualTreeHelper.GetParent(element);}return false;}
}


