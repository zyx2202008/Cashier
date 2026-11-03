using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Cashier.Core;
using Cashier.Core.Services;
using Microsoft.Win32;

namespace Cashier.App.Views;

public class DataPackageView:UserControl
{
    private readonly TextBlock _info=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)};
    private readonly DataGrid _grid=new(){IsReadOnly=true};
    private readonly Button _import;
    private readonly Button _open;
    private string? _packagePath;
    private DataPackageInfo? _preview;
    private string? _importedDirectory;
    public DataPackageView()
    {
        var(root,bar,body)=ToolLayout.Build();Content=root;
        var export=Ui.Btn("导出整账套…",(_,_)=>Export(),"Btn.Primary");
        var select=Ui.Btn("选择整账套文件…",(_,_)=>Pick());
        _import=Ui.Btn("导入为新账套…",(_,_)=>Import());_import.IsEnabled=false;
        _open=Ui.Btn("打开导入的账套",(_,_)=>OpenImported());_open.IsEnabled=false;
        export.IsEnabled=Perm.Has(AppSession.Role,Perm.Backup);select.IsEnabled=Perm.Has(AppSession.Role,Perm.Restore);
        foreach(var button in new[]{export,select,_import,_open}){button.Margin=new Thickness(0,0,8,6);bar.Children.Add(button);}
        _grid.Columns.Add(Ui.Col("数据内容","Title",230));_grid.Columns.Add(Ui.Col("记录数","Count",130,true));
        var panel=new DockPanel();DockPanel.SetDock(_info,Dock.Top);panel.Children.Add(_info);panel.Children.Add(_grid);body.Content=panel;
        _info.Text="整账套文件保留基础资料、全部单据、发票、核销、期初锁定、余额调整、账号及操作日志。\n导入将在所选位置新建账套目录。打开导入账套后，使用原账套的账号和密码登录。\n普通Excel整理请使用“基础资料整理”页；业务单据与发票请使用“导入预览”页。";
    }
    private void Export()
    {
        try
        {
            var save=new SaveFileDialog{Filter="Cashier整账套文件|*.cashier",FileName="账套-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".cashier"};
            if(save.ShowDialog()!=true)return;
            var result=AppSession.Db.ExportDataPackage(save.FileName);
            Ui.Info($"整账套已导出：\n{save.FileName}\n\n账套：{result.BookName}，包含{result.TableCounts.Count}类数据。文件包含账号及完整账务，请妥善保存。");
        }
        catch(Exception ex){Ui.Error(ex);}
    }
    private void Pick()
    {
        try{var select=new OpenFileDialog{Filter="Cashier整账套文件|*.cashier"};if(select.ShowDialog()==true)LoadPackage(select.FileName);}
        catch(Exception ex){Ui.Error(ex);}
    }
    internal void LoadPackage(string path)
    {
        _import.IsEnabled=false;_preview=null;_packagePath=null;_grid.ItemsSource=null;
        var preview=AppSession.Db.PreviewDataPackage(path);
        _preview=preview;_packagePath=path;
        var created=DateTimeOffset.TryParse(preview.CreatedAt,out var time)?time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"):preview.CreatedAt;
        _info.Text=$"来源账套：{preview.BookName} · 导出时间：{created}\n软件版本：{preview.AppVersion} · 数据大小：{preview.DatabaseBytes/1024d:N1} KB\n请核对下列记录数量。导入会创建新账套，账号及密码沿用来源账套。";
        _grid.ItemsSource=preview.TableCounts.Select(row=>new{Title=TableTitle(row.Key),Count=row.Value}).ToList();
        _import.IsEnabled=Perm.Has(AppSession.Role,Perm.Restore);
    }
    private void Import()
    {
        try
        {
            if(_preview==null||_packagePath==null)throw new AppException("请先选择并核对整账套文件。");
            var folder=new OpenFolderDialog{Title="选择存放新账套的位置（会在其下新建文件夹）"};if(folder.ShowDialog()!=true)return;
            var target=Path.Combine(folder.FolderName,"Cashier-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..6]);
            if(!Ui.Confirm($"将把账套“{_preview.BookName}”导入到：\n{target}\n\n这是一个新账套目录。是否继续？"))return;
            ImportInto(target);
            Ui.Info("导入成功：\n"+target+"\n\n点击“打开导入的账套”，使用来源账套的账号及密码登录。");
        }
        catch(Exception ex){Ui.Error(ex);}
    }
    internal void ImportInto(string directory)
    {
        if(_preview==null||_packagePath==null)throw new AppException("请先选择整账套文件。");
        AppSession.Db.ImportDataPackage(_packagePath,directory,_preview.FileSha256);_importedDirectory=directory;_open.IsEnabled=true;
    }
    private void OpenImported()
    {
        try
        {
            if(_importedDirectory==null)return;
            var executable=Environment.ProcessPath??throw new AppException("无法确定程序位置，请用Cashier.exe重新打开。");
            var start=new ProcessStartInfo(executable){UseShellExecute=false};
            if(Path.GetFileNameWithoutExtension(executable).Equals("dotnet",StringComparison.OrdinalIgnoreCase))start.ArgumentList.Add(typeof(MainWindow).Assembly.Location);
            start.ArgumentList.Add("--data-dir");start.ArgumentList.Add(_importedDirectory);Process.Start(start);
        }
        catch(Exception ex){Ui.Error(ex);}
    }
    private static string TableTitle(string name)=>name switch
    {
        "accounts"=>"资金账户","partners"=>"客户与供应商","subjects"=>"收入支出科目","depts"=>"部门","projects"=>"项目","handlers"=>"经办人",
        "vouchers"=>"业务单据","settlements"=>"核销记录","invoices"=>"发票","invoice_links"=>"发票关联","periods"=>"期间管理",
        "balance_adjustments"=>"余额调整","opening_control"=>"期初确认与锁定","users"=>"账号与角色","audit_log"=>"操作日志",
        "settlement_history"=>"核销历史","invoice_link_history"=>"发票关联历史","import_batches"=>"导入批次","settings"=>"账套设置","meta"=>"账套控制信息",_=>name
    };
}

public class MasterDataExchangeView:UserControl
{
    private readonly ComboBox _kind=new(){Width=150,DisplayMemberPath="Title",SelectedValuePath="Kind",Margin=new Thickness(0,0,8,6)};
    private readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,10)};
    private readonly DataGrid _grid=new(){IsReadOnly=true};
    private readonly Button _commit;
    private readonly Button _validation;
    private MasterExchangePreview? _preview;
    private MasterExchangeKind Kind=>(MasterExchangeKind)(_kind.SelectedValue??MasterExchangeKind.Account);
    public MasterDataExchangeView()
    {
        var(root,bar,body)=ToolLayout.Build();Content=root;
        _kind.ItemsSource=Enum.GetValues<MasterExchangeKind>().Select(kind=>new{Kind=kind,Title=MasterDataExchangeService.Title(kind)}).ToList();_kind.SelectedIndex=0;bar.Children.Add(_kind);
        var template=Ui.Btn("下载空白模板…",(_,_)=>Export(true));var export=Ui.Btn("导出当前资料…",(_,_)=>Export(false),"Btn.Primary");
        var select=Ui.Btn("选择Excel并预览…",(_,_)=>Pick());_commit=Ui.Btn("确认导入",(_,_)=>Commit());_validation=Ui.Btn("导出校验结果…",(_,_)=>Validation());
        var allowed=Perm.Has(AppSession.Role,Perm.MasterData)&&Perm.Has(AppSession.Role,Perm.Import);
        template.IsEnabled=export.IsEnabled=select.IsEnabled=allowed;_commit.IsEnabled=_validation.IsEnabled=false;
        foreach(var button in new[]{template,export,select,_commit,_validation}){button.Margin=new Thickness(0,0,8,6);bar.Children.Add(button);}
        foreach(var(title,path,width) in new[]{("Excel行号","RowNo",90),("状态","StatusText",130),("名称","Name",220),("校验说明","Detail",600)})_grid.Columns.Add(Ui.Col(title,path,width));
        var panel=new DockPanel();DockPanel.SetDock(_status,Dock.Top);panel.Children.Add(_status);panel.Children.Add(_grid);body.Content=panel;
        _status.Text="新增资料请下载空白模板；批量修改请先导出当前资料，在原行编辑后回导。\n锁定期初不能通过Excel修改；错误行需先修正，确认后整批保存。已有相同资料会跳过。";
        _kind.SelectionChanged+=(_,_)=>{_preview=null;_grid.ItemsSource=null;_commit.IsEnabled=_validation.IsEnabled=false;};
    }
    private void Export(bool blank)
    {
        try
        {
            var save=new SaveFileDialog{Filter="Excel工作簿|*.xlsx",FileName=MasterDataExchangeService.Title(Kind)+(blank?"新增模板":"资料整理")+".xlsx"};
            if(save.ShowDialog()!=true)return;MasterDataExchangeService.Export(Kind,save.FileName,blank);Ui.Info("Excel文件已生成。填写规则见工作簿“填写说明”页。");
        }
        catch(Exception ex){Ui.Error(ex);}
    }
    private void Pick()
    {
        try{var select=new OpenFileDialog{Filter="Excel工作簿|*.xlsx"};if(select.ShowDialog()==true)LoadFile(select.FileName);}
        catch(Exception ex){Ui.Error(ex);}
    }
    internal void LoadFile(string path)
    {
        _preview=null;_commit.IsEnabled=_validation.IsEnabled=false;_grid.ItemsSource=null;
        _preview=MasterDataExchangeService.Preview(Kind,path);_grid.ItemsSource=_preview.Rows;
        _status.Text=$"共{_preview.Rows.Count}行，需要新增或修改{_preview.ChangeCount}行，不变{_preview.SkipCount}行，错误{_preview.ErrorCount}行。\n请核对全部校验结果。有错误时整批不导入。";
        _commit.IsEnabled=_preview.ErrorCount==0&&_preview.ChangeCount>0;_validation.IsEnabled=true;
    }
    private void Commit()
    {
        try{var count=CommitPrepared();Ui.Info($"已保存{count}条基础资料。请刷新对应资料页查看。");}
        catch(Exception ex){Ui.Error(ex);}
    }
    internal int CommitPrepared()
    {
        if(_preview==null)throw new AppException("请先选择Excel文件并预览。");
        var count=MasterDataExchangeService.Commit(_preview);_commit.IsEnabled=false;_status.Text=$"已成功保存{count}条基础资料。";return count;
    }
    private void Validation()
    {
        try{if(_preview==null)return;var save=new SaveFileDialog{Filter="Excel工作簿|*.xlsx",FileName="基础资料校验结果.xlsx"};if(save.ShowDialog()==true)MasterDataExchangeService.ExportValidation(_preview,save.FileName);}
        catch(Exception ex){Ui.Error(ex);}
    }
}
