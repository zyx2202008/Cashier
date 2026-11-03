using System.Windows;
using System.Windows.Controls;
using Cashier.Core;
using Cashier.Core.Services;
using Microsoft.Win32;

namespace Cashier.App.Views;

public class OpeningBalanceView : UserControl,IAppPage
{
    private readonly TextBlock _state=new() {TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,10)};
    private readonly DataGrid _grid=new() {IsReadOnly=true};
    private readonly DatePicker _from=new() {Width=125};
    private readonly DatePicker _to=new() {Width=125};
    private readonly ComboBox _kind=new() {Width=130,SelectedValuePath="Tag"};
    private readonly Button _confirm;
    private readonly Button _add;
    private readonly Button _reverse;
    public string PageTitle=>"期初与余额调整";
    public string PageSubtitle=>"确认后锁定期初；更正单独登记，生效日不得早于登记当天，历史更正记录完整保留";
    public string? Chip=>null;

    public OpeningBalanceView()
    {
        var (root,bar,body)=ToolLayout.Build();Content=root;
        _confirm=Ui.Btn("确认并锁定期初",(_,_)=>Confirm(),"Btn.Primary");
        _add=Ui.Btn("登记余额调整",(_,_)=>Add());
        _reverse=Ui.Btn("追加反向调整",(_,_)=>Reverse());
        foreach(var button in new[]{_confirm,_add,_reverse,Ui.Btn("导出调整记录",(_,_)=>Export()),Ui.Btn("刷新",(_,_)=>Refresh())})
        {button.Margin=new Thickness(0,0,8,4);bar.Children.Add(button);}
        var panel=new Grid();panel.RowDefinitions.Add(new(){Height=GridLength.Auto});panel.RowDefinitions.Add(new(){Height=GridLength.Auto});panel.RowDefinitions.Add(new());
        panel.Children.Add(_state);
        var filters=new WrapPanel {Margin=new Thickness(0,0,0,8)};
        foreach(var (label,control) in new (string,Control)[]{("生效日从",_from),("至",_to),("类型",_kind)})
        {filters.Children.Add(new TextBlock {Text=label,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,6,4)});control.Margin=new Thickness(0,0,8,4);filters.Children.Add(control);}
        foreach(var (value,label) in new[]{("","全部"),("account","账户余额"),("receivable","客户应收"),("payable","供应商应付")})
            _kind.Items.Add(new ComboBoxItem {Tag=value,Content=label});
        _kind.SelectedIndex=0;
        filters.Children.Add(Ui.Btn("查询",(_,_)=>Refresh()));Grid.SetRow(filters,1);panel.Children.Add(filters);
        foreach(var (label,path,width) in new[]{("调整编号","Number",115),("类型","KindText",110),("对象","TargetName",180),("生效日期","EffectiveDate",115),("调整金额","DeltaText",120),
            ("登记时调整前余额","BeforeText",160),("登记时调整后余额","AfterText",160),("原因","Reason",260),("状态","StatusText",200),("登记人","CreatedBy",110),("登记时间","CreatedAt",175)})
            _grid.Columns.Add(Ui.Col(label,path,width,path.EndsWith("Text")&&path is "DeltaText" or "BeforeText" or "AfterText"));
        Grid.SetRow(_grid,2);panel.Children.Add(_grid);body.Content=Ui.Card(panel,12);
        Loaded+=(_,_)=>Refresh();
    }
    public IEnumerable<UIElement> BuildActions()=>Array.Empty<UIElement>();
    public void Refresh()
    {
        try
        {
            var state=OpeningBalanceService.State();
            _state.Text=state.IsLocked?$"期初已锁定 · 启用日期 {state.StartDate} · {state.LockedBy} 于 {state.LockedAt} 确认\n{state.Reason}\n余额调整独立列示，不计入正常收支、项目利润或开票金额。登记时余额仅为当次操作的核对快照。"
                :"期初尚未确认：请先在基础资料中核对账户、客户和供应商期初，再由管理员确认锁定。正式使用前请完成确认。";
            _confirm.IsEnabled=!state.IsLocked&&Perm.Has(AppSession.Role,Perm.OpeningLock);
            _add.IsEnabled=state.IsLocked&&Perm.Has(AppSession.Role,Perm.BalanceAdjust);
            _reverse.IsEnabled=_add.IsEnabled;
            var from=_from.SelectedDate?.ToString("yyyy-MM-dd");var to=_to.SelectedDate?.ToString("yyyy-MM-dd");
            if(from!=null&&to!=null&&string.CompareOrdinal(from,to)>0) throw new AppException("开始日期不能晚于结束日期。");
            var kind=(_kind.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            _grid.ItemsSource=OpeningBalanceService.Query(from,to,string.IsNullOrEmpty(kind)?null:kind);
        }
        catch(Exception ex){Ui.Error(ex);}
    }
    private void Confirm()
    {
        var dialog=new OpeningConfirmDialog {Owner=Window.GetWindow(this)};
        if(dialog.ShowDialog()==true) Refresh();
    }
    private void Add()
    {
        var dialog=new BalanceAdjustmentDialog(null) {Owner=Window.GetWindow(this)};
        if(dialog.ShowDialog()==true) Refresh();
    }
    private void Reverse()
    {
        if(_grid.SelectedItem is not BalanceAdjustment row){Ui.Info("请先选择一条原调整记录。");return;}
        if(row.HasReversal||row.ReversalOf.HasValue){Ui.Info("请选择尚未反向调整的原记录。");return;}
        var dialog=new BalanceAdjustmentDialog(row) {Owner=Window.GetWindow(this)};
        if(dialog.ShowDialog()==true) Refresh();
    }
    private void Export()
    {
        try
        {
            var save=new SaveFileDialog {Filter="Excel 文件|*.xlsx",FileName="余额调整记录.xlsx"};
            if(save.ShowDialog()!=true)return;
            var rows=(_grid.ItemsSource as IEnumerable<BalanceAdjustment> ?? Array.Empty<BalanceAdjustment>()).ToList();
            var report=new ReportResult {Id="balance_adjustments",Title="余额调整记录",Period="按当前查询条件",Basis="调整独立于业务收支；余额为登记时生效日的核对快照。"};
            foreach(var name in new[]{"编号","类型","对象","生效日","调整金额","调整前余额","调整后余额","原因","状态","登记人","登记时间"})
                report.Columns.Add(new ReportColumn {Header=name,Width=140,Numeric=name.Contains("金额")||name.Contains("余额"),Format=name.Contains("金额")||name.Contains("余额")?"money":"text"});
            foreach(var row in rows) report.Rows.Add(new ReportRow {Cells=new object?[]{row.Number,row.KindText,row.TargetName,row.EffectiveDate,row.DeltaCents,row.BalanceBefore,row.BalanceAfter,row.Reason,row.StatusText,row.CreatedBy,row.CreatedAt}});
            ExcelService.ExportReport(report,save.FileName);Ui.Info("调整记录已导出。");
        }
        catch(Exception ex){Ui.Error(ex);}
    }
}

public class OpeningConfirmDialog : Window
{
    private readonly DatePicker _date=new(){SelectedDate=DateTime.Today};
    private readonly TextBox _reason=new(){Height=72,TextWrapping=TextWrapping.Wrap,AcceptsReturn=true,MaxLength=500};
    public OpeningConfirmDialog()
    {
        Title="确认并锁定期初";Width=520;SizeToContent=SizeToContent.Height;WindowStartupLocation=WindowStartupLocation.CenterOwner;ResizeMode=ResizeMode.NoResize;
        var panel=new StackPanel {Margin=new Thickness(18)};
        panel.Children.Add(new TextBlock {Text="请先核对全部账户及往来单位期初。确认后期初不能直接改写，后续通过余额调整更正。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)});
        _date.SelectedDate=DateTime.Parse(OpeningBalanceService.SuggestedStartDate());
        panel.Children.Add(new TextBlock {Text="账套启用日期（不得晚于最早业务日期）"});panel.Children.Add(_date);
        panel.Children.Add(new TextBlock {Text="确认原因 *",Margin=new Thickness(0,10,0,4)});panel.Children.Add(_reason);
        var buttons=new WrapPanel {HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0)};
        buttons.Children.Add(Ui.Btn("确认锁定",(_,_)=>Save(),"Btn.Primary"));var cancel=Ui.Btn("取消",(_,_)=>Close());cancel.IsCancel=true;buttons.Children.Add(cancel);panel.Children.Add(buttons);Content=panel;
    }
    private void Save()
    {
        try {OpeningBalanceService.Confirm(_date.SelectedDate?.ToString("yyyy-MM-dd")??"",_reason.Text);DialogResult=true;}
        catch(Exception ex){Ui.Error(ex);}
    }
}

public class BalanceAdjustmentDialog : Window
{
    private readonly BalanceAdjustment? _original;
    private readonly ComboBox _kind=new(){SelectedValuePath="Tag",Height=32};
    private readonly ComboBox _target=new(){DisplayMemberPath="Name",SelectedValuePath="Id",Height=32};
    private readonly DatePicker _date=new(){SelectedDate=DateTime.Today};
    private readonly TextBox _amount=new(){Height=32};
    private readonly TextBox _reason=new(){Height=80,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MaxLength=500};
    private readonly string _request=Guid.NewGuid().ToString("N");
    private readonly Button _save;
    public BalanceAdjustmentDialog(BalanceAdjustment? original)
    {
        _original=original;Title=original==null?"登记余额调整":"追加反向调整";Width=560;SizeToContent=SizeToContent.Height;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;ResizeMode=ResizeMode.NoResize;
        var panel=new StackPanel {Margin=new Thickness(18)};
        void Add(string label,Control control){panel.Children.Add(new TextBlock {Text=label,Margin=new Thickness(0,8,0,4)});panel.Children.Add(control);}
        panel.Children.Add(new TextBlock {Text="正数增加余额，负数减少余额。调整单独列示；生效日不得早于今天，不能选已关闭期间。",TextWrapping=TextWrapping.Wrap});
        foreach(var (value,label) in new[]{("account","账户余额"),("receivable","客户应收（负余额为预收）"),("payable","供应商应付（负余额为预付）")})
            _kind.Items.Add(new ComboBoxItem {Tag=value,Content=label});
        _kind.SelectionChanged+=(_,_)=>LoadTargets();_kind.SelectedValue=original?.Kind??"account";
        Add("调整类型",_kind);Add("调整对象",_target);Add("生效日期 *",_date);Add("调整金额（元）*",_amount);Add("原因 *",_reason);
        if(original!=null)
        {
            _kind.IsEnabled=false;_target.IsEnabled=false;
            _target.ItemsSource=new[]{new ComboItem {Id=original.TargetId,Name=original.TargetName}};_target.SelectedValue=original.TargetId;
            _amount.Text=Money.Format(-original.DeltaCents);_amount.IsReadOnly=true;
            _date.SelectedDate=DateTime.Parse(string.CompareOrdinal(original.EffectiveDate,SqlUtil.Today())>0?original.EffectiveDate:SqlUtil.Today());
            panel.Children.Add(new TextBlock {Text=$"原记录：{original.Number}。保存后保留原记录，并追加等额反向记录。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,0)});
        }
        var buttons=new WrapPanel {HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0)};
        _save=Ui.Btn("保存调整",(_,_)=>Save(),"Btn.Primary");buttons.Children.Add(_save);
        var cancel=Ui.Btn("取消",(_,_)=>Close());cancel.IsCancel=true;buttons.Children.Add(cancel);panel.Children.Add(buttons);
        Content=new ScrollViewer {Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,MaxHeight=650};
    }
    private void LoadTargets()
    {
        var kind=(_kind.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        _target.ItemsSource=kind=="account"?MasterDataService.ListAccounts(false).Select(a=>new ComboItem {Id=a.Id,Name=a.DisplayName}).ToList()
            :MasterDataService.ListPartners(kind=="receivable"?"customer":"supplier",false).Select(p=>new ComboItem {Id=p.Id,Name=p.DisplayName}).ToList();
        _target.SelectedIndex=0;
    }
    private void Save()
    {
        _save.IsEnabled=false;
        try
        {
            var date=_date.SelectedDate?.ToString("yyyy-MM-dd")??"";
            if(_original!=null) OpeningBalanceService.Reverse(_original.Id,date,_reason.Text);
            else
            {
                if(_target.SelectedValue is not long target)throw new AppException("请选择调整对象。");
                var amount=Money.ParseSignedOrZero(_amount.Text,"调整金额");
                OpeningBalanceService.Create((string)((ComboBoxItem)_kind.SelectedItem).Tag,target,date,amount,_reason.Text,_request);
            }
            DialogResult=true;
        }
        catch(Exception ex){Ui.Error(ex);_save.IsEnabled=true;}
    }
}
