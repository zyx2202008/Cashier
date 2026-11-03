using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Cashier.App.Views;
using Cashier.App.Printing;
using Cashier.Core;
using Cashier.Core.Services;
using ClosedXML.Excel;

namespace Cashier.App;

public static class QaBatchLayout
{
    public static bool Run(string output)
    {
        Directory.CreateDirectory(output);int checks=0,failures=0;var log=new List<string>();
        void Check(string name,bool ok){checks++;if(!ok){failures++;var line="FAIL | "+name;log.Add(line);File.AppendAllText(Path.Combine(output,"layout-progress.txt"),line+Environment.NewLine);}}
        AppSession.Current=new AuthService().Login("admin","admin888");
        using(var c=AppSession.Db.Open())if(new SqlCmd(c,"SELECT COUNT(*) FROM vouchers").Long()>0)throw new Exception("Fresh isolated QA ledger required");
        var customer=MasterDataService.SavePartner(new Partner{Code="C0001",Name="排版客户"+new string('长',100),Kind="customer"});
        var supplier=MasterDataService.SavePartner(new Partner{Code="S0001",Name="排版供应商",Kind="supplier"});
        var bank=MasterDataService.ListAccounts(false).First(x=>x.Kind=="bank").Id;
        var vouchers=new VoucherService();long Post(Voucher v){var id=vouchers.Create(v);vouchers.Audit(id);return id;}
        Post(new Voucher{Kind=VoucherKind.Income,Nature=VoucherNature.OtherIn,BizDate="2026-01-10",AmountCents=1897486,AccountId=bank,Summary="截图复现：18,974.86 元"});
        Post(new Voucher{Kind=VoucherKind.Receivable,BizDate="2026-01-10",AmountCents=Money.MaxCents,PartnerId=customer,Summary=new string('长',120),CountAsRevenue=true});
        var main=new MainWindow();main.Show();Pump();var nav=(ListBox)main.FindName("Nav");var menu=nav.Items.Cast<NavItem>().ToArray();
        foreach(var (width,height,scale) in new[]{(1400d,820d,1d),(1080d,640d,1d),(1400d,900d,1.25d),(1680d,1040d,1.5d)})
        {
            main.Width=width;main.Height=height;((FrameworkElement)main.Content).LayoutTransform=new ScaleTransform(scale,scale);
            foreach(var item in menu)
            {
                File.WriteAllText(Path.Combine(output,"layout-current.txt"),$"{item.Id} {width}x{height} scale {scale}");
                main.Navigate(item.Id);Pump();main.UpdateLayout();
                var host=(ContentControl)main.FindName("PageHost");var page=(FrameworkElement)host.Content;
                var actions=(Panel)main.FindName("PageActions");
                foreach(var button in actions.Children.OfType<FrameworkElement>())
                {
                    var rect=button.TransformToAncestor(actions).TransformBounds(new Rect(button.RenderSize));
                    Check($"actions visible {item.Id}/{scale}",rect.Left>=-1&&rect.Right<=actions.ActualWidth+1&&rect.Top>=-1&&rect.Bottom<=actions.ActualHeight+1);
                }
                foreach(var grid in Desc<DataGrid>(page))
                {
                    Check($"table usable {item.Id}/{scale}",grid.ActualHeight>=24&&grid.ActualWidth>=100);
                    foreach(var header in Desc<DataGridColumnHeader>(grid).Where(h=>h.Column is NumericColumn))
                        Check($"numeric header aligns with data {item.Id}/{header.Content}",header.HorizontalContentAlignment==HorizontalAlignment.Right);
                }
                var count=page.FindName("FootCount") as TextBlock;var total=(page.FindName("FootTotal")??page.FindName("FootSum")) as TextBlock;
                if(count!=null&&total!=null){var a=count.TransformToAncestor(page).TransformBounds(new Rect(count.RenderSize));var b=total.TransformToAncestor(page).TransformBounds(new Rect(total.RenderSize));Check($"footer no overlap {item.Id}/{scale}",!a.IntersectsWith(b));}
            }
            log.Add($"PASS | all {menu.Length} pages visited at {width}x{height}, layout scale {scale}");
            File.WriteAllLines(Path.Combine(output,"layout-progress.txt"),log);
        }
        // 1.1.3：版本断言改为与程序集版本比对，避免写死版本号导致换版后误报失败
        var asmVersion = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "";
        var navText = ((TextBlock)main.FindName("NavMeta")).Text;
        Check("visible version", asmVersion.Length > 0 && navText.Contains(asmVersion));
        Post(new Voucher{Kind=VoucherKind.Payable,PartnerId=supplier,BizDate="2026-01-08",AmountCents=123456789,Summary="月报排版数据"});
        foreach(var reportId in new[]{"supplier_monthly_payable","customer_monthly_receivable"})
        foreach(var size in new[]{(width:1080d,height:640d,scale:1d),(width:1400d,height:900d,scale:1.25d),(width:1680d,height:1040d,scale:1.5d)})
        {
            main.Width=size.width;main.Height=size.height;
            ((FrameworkElement)main.Content).LayoutTransform=new ScaleTransform(size.scale,size.scale);
            main.Navigate("rpt_"+reportId);Pump();main.UpdateLayout();
            var monthly=(ReportView)((ContentControl)main.FindName("PageHost")).Content;
            ((ComboBox)monthly.FindName("CmbYear")).SelectedItem=2026;
            ((ComboBox)monthly.FindName("CmbMonth")).SelectedItem=1;
            monthly.Refresh();Pump();main.UpdateLayout();
            var monthlyGrid=(DataGrid)monthly.FindName("Grid");
            Rect Bounds(string name){var el=(FrameworkElement)monthly.FindName(name);return el.TransformToAncestor(monthly).TransformBounds(new Rect(el.RenderSize));}
            Check($"monthly usable {reportId}/{size.scale}",monthlyGrid.ActualHeight>=50&&monthlyGrid.Columns.Count==8&&monthlyGrid.Items.Count==1);
            Check($"monthly cards separate {reportId}/{size.scale}",Bounds("FilterCard").Bottom<=Bounds("BasisCard").Top&&Bounds("BasisCard").Bottom<=Bounds("TableCard").Top);
            Check($"monthly footer inside {reportId}/{size.scale}",Bounds("FootTotal").Bottom<=monthly.ActualHeight+1);
            Check($"monthly scroll {reportId}/{size.scale}",ScrollViewer.GetHorizontalScrollBarVisibility(monthlyGrid)==ScrollBarVisibility.Auto);
            QaTools.CaptureWindow(main,Path.Combine(output,$"{reportId}-{size.scale}.png"));
        }
        ((FrameworkElement)main.Content).LayoutTransform=Transform.Identity;main.Width=1400;main.Height=820;
        main.Navigate("rpt_account_year");Pump();var report=(ReportView)((ContentControl)main.FindName("PageHost")).Content;
        ((DatePicker)report.FindName("DateFrom")).SelectedDate=new DateTime(2026,1,1);((DatePicker)report.FindName("DateTo")).SelectedDate=new DateTime(2026,10,30);((ComboBox)report.FindName("CmbAccount")).SelectedValue=bank;report.Refresh();Pump();
        var data=(DataGrid)report.FindName("Grid");var row=(ReportGridRow)data.Items[0];Check("screenshot amount remains same column and unchanged",Convert.ToInt64(row.Cells[3])==1897486&&Convert.ToInt64(row.Cells[4])==0&&Convert.ToInt64(row.Cells[5])==1897486);
        QaTools.CaptureWindow(main,Path.Combine(output,"修复后的账户年报.png"));

        // 期间开票与收款统计：专项数据与排版验收
        var qaInvoices=new InvoiceService();
        var qaOutBlue=qaInvoices.Save(new Invoice{Kind=InvoiceKind.Out,InvoiceType="增值税专用发票",InvoiceNo="QA-OUT-BLUE",PartnerId=customer,RegDate="2026-01-06",GrossCents=1_130_000,NetCents=1_000_000,TaxCents=130_000,RateBp=1300});
        qaInvoices.Save(new Invoice{Kind=InvoiceKind.Out,InvoiceType="增值税专用发票",InvoiceNo="QA-OUT-RED",PartnerId=customer,RegDate="2026-01-20",GrossCents=-113_000,NetCents=-100_000,TaxCents=-13_000,RateBp=1300,OriginalInvoiceId=qaOutBlue});
        qaInvoices.Save(new Invoice{Kind=InvoiceKind.In,InvoiceType="增值税专用发票",InvoiceNo="QA-IN-BLUE",PartnerId=supplier,RegDate="2026-01-07",GrossCents=452_000,NetCents=400_000,TaxCents=52_000,RateBp=1300});
        Post(new Voucher{Kind=VoucherKind.Income,Nature=VoucherNature.CustomerReceipt,BizDate="2026-01-10",AmountCents=500_000,PartnerId=customer,AccountId=bank,Summary=new string('长',120)});
        Post(new Voucher{Kind=VoucherKind.Expense,Nature=VoucherNature.SupplierPayment,BizDate="2026-01-12",AmountCents=400_000,PartnerId=supplier,AccountId=bank,Summary="排版付款"});
        Rect CardRect(ReportView view,string name){var el=(FrameworkElement)view.FindName(name);return el.TransformToAncestor(view).TransformBounds(new Rect(el.RenderSize));}
        foreach(var (width,height,scale) in new[]{(1400d,820d,1d),(1080d,640d,1d),(1366d,768d,1d),(1536d,824d,1d),(1400d,880d,1.25d),(1440d,852d,1.33d)})
        {
            main.Width=width;main.Height=height;((FrameworkElement)main.Content).LayoutTransform=new ScaleTransform(scale,scale);
            main.Navigate("rpt_period_invoice_cash");Pump();main.UpdateLayout();
            var sizedReport=(ReportView)((ContentControl)main.FindName("PageHost")).Content;
            ((DatePicker)sizedReport.FindName("DateFrom")).SelectedDate=new DateTime(2026,1,1);
            ((DatePicker)sizedReport.FindName("DateTo")).SelectedDate=new DateTime(2026,1,31);
            sizedReport.Refresh();Pump();main.UpdateLayout();
            var tag=$"period {width}x{height}@{scale}";
            Check($"{tag} window size applied",Math.Abs(main.ActualWidth-width)<=2&&Math.Abs(main.ActualHeight-height)<=2);
            Check($"{tag} logical viewport meets minimum",main.ActualWidth/scale>=1079&&main.ActualHeight/scale>=639);
            var grid=(DataGrid)sizedReport.FindName("Grid");
            Check($"{tag} rows",grid.Items.Count==1);
            Check($"{tag} horizontal scroll",ScrollViewer.GetHorizontalScrollBarVisibility(grid)==ScrollBarVisibility.Auto);
            var wrap=Desc<WrapPanel>(sizedReport).First();
            foreach(var child in wrap.Children.OfType<FrameworkElement>().Where(x=>x.Visibility==Visibility.Visible))
            {
                var rect=child.TransformToAncestor(wrap).TransformBounds(new Rect(child.RenderSize));
                Check($"{tag} toolbar wraps without clipping",rect.Left>=-1&&rect.Top>=-1&&rect.Right<=wrap.ActualWidth+1&&rect.Bottom<=wrap.ActualHeight+1);
            }
            var filter=CardRect(sizedReport,"FilterCard");var basis=CardRect(sizedReport,"BasisCard");var table=CardRect(sizedReport,"TableCard");
            Check($"{tag} filter and basis separated",filter.Bottom<=basis.Top+0.5&&!filter.IntersectsWith(basis));
            Check($"{tag} basis and table separated",basis.Bottom<=table.Top+0.5&&!basis.IntersectsWith(table));
            var gridRect=((FrameworkElement)grid).TransformToAncestor(sizedReport).TransformBounds(new Rect(grid.RenderSize));
            var foot=(FrameworkElement)sizedReport.FindName("FootPanel");var footRect=foot.TransformToAncestor(sizedReport).TransformBounds(new Rect(foot.RenderSize));
            Check($"{tag} table does not overlap footer",gridRect.Bottom<=footRect.Top+0.5&&gridRect.Top<footRect.Top);
            var numericCells=Desc<DataGridCell>(grid).Where(c=>c.Column is NumericColumn).ToList();
            Check($"{tag} numeric cells realized",numericCells.Count>0);
            Check($"{tag} numeric cells right aligned",numericCells.All(c=>Desc<TextBlock>(c).All(t=>string.IsNullOrEmpty(t.Text)||t.TextAlignment==TextAlignment.Right)));
            var nameBlock=Desc<TextBlock>(grid).FirstOrDefault(t=>t.Text.StartsWith("排版客户",StringComparison.Ordinal));
            Check($"{tag} long partner ellipsis and tooltip",nameBlock!=null&&nameBlock.TextTrimming==TextTrimming.CharacterEllipsis&&nameBlock.ToolTip is string tip&&tip==nameBlock.Text);
            Check($"{tag} ten columns",grid.Columns.Count==10&&grid.Columns[9].Header?.ToString()?.StartsWith("票款差额",StringComparison.Ordinal)==true);
            var numericHeaders=Desc<DataGridColumnHeader>(grid).Where(h=>h.Column is NumericColumn).ToList();
            Check($"{tag} numeric headers right",numericHeaders.Count==8&&numericHeaders.All(h=>h.HorizontalContentAlignment==HorizontalAlignment.Right));
            var footCount=(TextBlock)sizedReport.FindName("FootCount");var footTotal=(TextBlock)sizedReport.FindName("FootTotal");
            var fcRect=footCount.TransformToAncestor(sizedReport).TransformBounds(new Rect(footCount.RenderSize));
            var ftRect=footTotal.TransformToAncestor(sizedReport).TransformBounds(new Rect(footTotal.RenderSize));
            Check($"{tag} footer lines separate",fcRect.Bottom<=ftRect.Top+0.5);
            Check($"{tag} footer stays inside table card",footRect.Bottom<=table.Bottom+0.5&&footRect.Top>=gridRect.Bottom-0.5);
            Check($"{tag} direction partner quick visible",new[]{"BoxDirection","BoxPartner","BoxQuick"}.All(n=>((FrameworkElement)sizedReport.FindName(n)).IsVisible));
            log.Add($"size {tag} actual {main.ActualWidth:0}x{main.ActualHeight:0} grid {grid.ActualWidth:0}x{grid.ActualHeight:0}");
        }
        ((FrameworkElement)main.Content).LayoutTransform=Transform.Identity;main.Width=1400;main.Height=820;
        main.Navigate("rpt_period_invoice_cash");Pump();
        var periodView=(ReportView)((ContentControl)main.FindName("PageHost")).Content;
        ((DatePicker)periodView.FindName("DateFrom")).SelectedDate=new DateTime(2026,1,1);
        ((DatePicker)periodView.FindName("DateTo")).SelectedDate=new DateTime(2026,1,31);
        periodView.Refresh();Pump();
        var direction=(ComboBox)periodView.FindName("CmbDirection");
        Check("direction defaults to out",Convert.ToInt64(direction.SelectedValue)==0&&direction.SelectedItem is ComboItem selectedDirection&&selectedDirection.Name=="销项");
        Check("period hides unrelated filters",!((FrameworkElement)periodView.FindName("BoxProject")).IsVisible&&!((FrameworkElement)periodView.FindName("BoxAccount")).IsVisible&&!((FrameworkElement)periodView.FindName("BoxQuickButtons")).IsVisible);
        var partnerCombo=(ComboBox)periodView.FindName("CmbPartner");
        var customerItem=partnerCombo.Items.Cast<ComboItem>().First(x=>x.Name.StartsWith("排版客户",StringComparison.Ordinal));
        partnerCombo.SelectedValue=customerItem.Id;periodView.Refresh();Pump();
        var periodGrid=(DataGrid)periodView.FindName("Grid");
        var periodFoot=(TextBlock)periodView.FindName("FootTotal");
        Check("partner filter applied",periodGrid.Items.Count==1&&periodFoot.Text.Contains("含税金额合计 10,170.00")&&periodFoot.Text.Contains("期间收款金额 5,000.00"));
        direction.SelectedValue=1L;Pump();
        Check("direction switch reloads partners",partnerCombo.Items.Cast<ComboItem>().Any(x=>x.Name=="排版供应商")&&partnerCombo.Items.Cast<ComboItem>().All(x=>!x.Name.StartsWith("排版客户",StringComparison.Ordinal)));
        Check("direction switch resets partner filter",Convert.ToInt64(partnerCombo.SelectedValue)==0);
        var inGrid=(DataGrid)periodView.FindName("Grid");
        Check("direction switch headers and rows",inGrid.Columns[8].Header?.ToString()=="期间付款金额"&&inGrid.Items.Count==1&&Convert.ToInt64(((ReportGridRow)inGrid.Items[0]!).Cells[5])==452_000);
        Check("in direction footer amounts",periodFoot.Text.Contains("含税金额合计 4,520.00")&&periodFoot.Text.Contains("期间付款金额 4,000.00"));
        direction.SelectedValue=0L;Pump();
        Check("direction switch restores out",((DataGrid)periodView.FindName("Grid")).Columns[8].Header?.ToString()=="期间收款金额");
        Check("direction switch back resets partner",Convert.ToInt64(partnerCombo.SelectedValue)==0&&((DataGrid)periodView.FindName("Grid")).Items.Count==1);
        var quick=(ComboBox)periodView.FindName("CmbQuick");
        var dateFrom=(DatePicker)periodView.FindName("DateFrom");var dateTo=(DatePicker)periodView.FindName("DateTo");
        var today=DateTime.Today;var thisMonth=new DateTime(today.Year,today.Month,1);
        quick.SelectedItem="本月";Pump();
        Check("quick period this month",dateFrom.SelectedDate==thisMonth&&dateTo.SelectedDate==today);
        Check("empty quick period footer",((DataGrid)periodView.FindName("Grid")).Items.Count==0&&((TextBlock)periodView.FindName("FootCount")).Text.StartsWith("共 0 行",StringComparison.Ordinal)&&periodFoot.Text.Contains("含税金额合计 0.00"));
        quick.SelectedItem="本季度";Pump();
        var quarterFirst=new DateTime(today.Year,(today.Month-1)/3*3+1,1);
        Check("quick period this quarter",dateFrom.SelectedDate==quarterFirst&&dateTo.SelectedDate==today);
        quick.SelectedItem="本年度";Pump();
        Check("quick period year to today",dateFrom.SelectedDate==new DateTime(today.Year,1,1)&&dateTo.SelectedDate==today);
        Check("year quick period restores rows",((DataGrid)periodView.FindName("Grid")).Items.Count==1);
        var lastMonth=new DateTime(today.Year,today.Month,1).AddMonths(-1);
        quick.SelectedItem="上月";Pump();
        Check("quick period last month",dateFrom.SelectedDate==lastMonth&&dateTo.SelectedDate==lastMonth.AddMonths(1).AddDays(-1));
        var lastMonthFrom=dateFrom.SelectedDate;var lastMonthTo=dateTo.SelectedDate;
        quick.SelectedItem="自定义";Pump();
        Check("custom quick keeps dates",dateFrom.SelectedDate==lastMonthFrom&&dateTo.SelectedDate==lastMonthTo);
        dateFrom.SelectedDate=new DateTime(2026,1,1);
        dateTo.SelectedDate=new DateTime(2026,1,31);
        periodView.Refresh();Pump();main.UpdateLayout();
        Check("manual dates switch quick to custom",(quick.SelectedItem as string)=="自定义");
        var queryButton=Desc<Button>(periodView).FirstOrDefault(b=>Equals(b.Content,"查询"));
        var queryText=queryButton==null?null:Desc<TextBlock>(queryButton).FirstOrDefault(t=>t.Text=="查询");
        Check("period query button white text",queryText!=null&&queryText.Foreground is SolidColorBrush queryBrush&&queryBrush.Color==Colors.White);
        QaTools.CaptureWindow(main,Path.Combine(output,"期间开票与收款统计.png"));
        var detail=new PeriodInvoiceCashDetailWindow(customer,"2026-01-01","2026-01-31",InvoiceKind.Out){Owner=main};
        detail.Show();Pump();detail.UpdateLayout();
        var invGrid=(DataGrid)detail.FindName("InvoiceGrid");var cashGrid=(DataGrid)detail.FindName("CashGrid");
        var exportButton=Desc<Button>(detail).FirstOrDefault(b=>Equals(b.Content,"导出 Excel"));
        var exportText=exportButton==null?null:Desc<TextBlock>(exportButton).FirstOrDefault(t=>t.Text=="导出 Excel");
        Check("detail export button white text",exportText!=null&&exportText.Foreground is SolidColorBrush exportBrush&&exportBrush.Color==Colors.White);
        Check("detail invoice rows",invGrid.Items.Count==2);
        Check("detail cash rows",cashGrid.Items.Count==1);
        Check("detail headers",invGrid.Columns[3].Header?.ToString()=="含税金额"&&cashGrid.Columns[3].Header?.ToString()=="金额");
        Check("detail title and chip",((TextBlock)detail.FindName("TitleText")).Text=="期间开票与收款明细"&&((TextBlock)detail.FindName("ChipText")).Text=="销项");
        Check("detail summary values",((TextBlock)detail.FindName("SummaryText")).Text.Contains("蓝 1 张 / 红 1 张 / 净 0 张")&&((TextBlock)detail.FindName("SummaryText")).Text.Contains("票款差额 5,170.00"));
        Check("detail section totals",((TextBlock)detail.FindName("InvoiceSectionText")).Text.Contains("共 2 张 · 含税合计 10,170.00")&&((TextBlock)detail.FindName("CashSectionText")).Text.Contains("共 1 笔 · 合计 5,000.00"));
        Check("detail numeric headers right",Desc<DataGridColumnHeader>(invGrid).Where(h=>h.Column is NumericColumn).All(h=>h.HorizontalContentAlignment==HorizontalAlignment.Right));
        var detailNumeric=Desc<DataGridCell>(invGrid).Where(c=>c.Column is NumericColumn).ToList();
        Check("detail numeric cells right aligned",detailNumeric.Count>0&&detailNumeric.All(c=>Desc<TextBlock>(c).All(t=>string.IsNullOrEmpty(t.Text)||t.TextAlignment==TextAlignment.Right)));
        var summaryBlock=Desc<TextBlock>(cashGrid).FirstOrDefault(t=>t.Text.StartsWith("长长长",StringComparison.Ordinal));
        Check("detail summary ellipsis and tooltip",summaryBlock!=null&&summaryBlock.TextTrimming==TextTrimming.CharacterEllipsis&&summaryBlock.ToolTip is string summaryTip&&summaryTip==summaryBlock.Text);
        var invRect=((FrameworkElement)invGrid).TransformToAncestor(detail).TransformBounds(new Rect(invGrid.RenderSize));
        var cashRect=((FrameworkElement)cashGrid).TransformToAncestor(detail).TransformBounds(new Rect(cashGrid.RenderSize));
        Check("detail sections do not overlap",invRect.Bottom<=cashRect.Top);
        QaTools.CaptureWindow(detail,Path.Combine(output,"期间开票与收款明细.png"));
        detail.Width=900;detail.Height=560;detail.UpdateLayout();Pump();
        invRect=((FrameworkElement)invGrid).TransformToAncestor(detail).TransformBounds(new Rect(invGrid.RenderSize));
        cashRect=((FrameworkElement)cashGrid).TransformToAncestor(detail).TransformBounds(new Rect(cashGrid.RenderSize));
        Check("detail minimum window grids usable",invGrid.ActualWidth>=100&&invGrid.ActualHeight>=24&&cashGrid.ActualWidth>=100&&cashGrid.ActualHeight>=24);
        Check("detail minimum window sections separate",invRect.Bottom<=cashRect.Top);
        Check("detail minimum window numeric alignment",Desc<DataGridColumnHeader>(invGrid).Where(h=>h.Column is NumericColumn).All(h=>h.HorizontalContentAlignment==HorizontalAlignment.Right)
            &&Desc<DataGridCell>(invGrid).Where(c=>c.Column is NumericColumn).All(c=>Desc<TextBlock>(c).All(t=>string.IsNullOrEmpty(t.Text)||t.TextAlignment==TextAlignment.Right)));
        QaTools.CaptureWindow(detail,Path.Combine(output,"期间开票与收款明细_最小尺寸.png"));detail.Close();
        var detailIn=new PeriodInvoiceCashDetailWindow(supplier,"2026-01-01","2026-01-31",InvoiceKind.In){Owner=main,Width=900,Height=560};
        detailIn.Show();Pump();detailIn.UpdateLayout();
        var invGridIn=(DataGrid)detailIn.FindName("InvoiceGrid");var cashGridIn=(DataGrid)detailIn.FindName("CashGrid");
        Check("in detail title chip section",((TextBlock)detailIn.FindName("TitleText")).Text=="期间收票与付款明细"&&((TextBlock)detailIn.FindName("ChipText")).Text=="进项"&&((TextBlock)detailIn.FindName("CashSectionTitle")).Text=="付款明细");
        Check("in detail rows and totals",invGridIn.Items.Count==1&&cashGridIn.Items.Count==1
            &&((TextBlock)detailIn.FindName("InvoiceSectionText")).Text.Contains("共 1 张 · 含税合计 4,520.00")
            &&((TextBlock)detailIn.FindName("CashSectionText")).Text.Contains("共 1 笔 · 合计 4,000.00")
            &&((TextBlock)detailIn.FindName("SummaryText")).Text.Contains("蓝 1 张 / 红 0 张 / 净 1 张")
            &&((TextBlock)detailIn.FindName("SummaryText")).Text.Contains("票款差额 520.00"));
        var invRectIn=((FrameworkElement)invGridIn).TransformToAncestor(detailIn).TransformBounds(new Rect(invGridIn.RenderSize));
        var cashRectIn=((FrameworkElement)cashGridIn).TransformToAncestor(detailIn).TransformBounds(new Rect(cashGridIn.RenderSize));
        Check("in detail minimum window usable",invGridIn.ActualWidth>=100&&invGridIn.ActualHeight>=24&&cashGridIn.ActualWidth>=100&&cashGridIn.ActualHeight>=24&&invRectIn.Bottom<=cashRectIn.Top);
        Check("in detail numeric alignment",Desc<DataGridColumnHeader>(invGridIn).Where(h=>h.Column is NumericColumn).All(h=>h.HorizontalContentAlignment==HorizontalAlignment.Right)
            &&Desc<DataGridCell>(cashGridIn).Where(c=>c.Column is NumericColumn).All(c=>Desc<TextBlock>(c).All(t=>string.IsNullOrEmpty(t.Text)||t.TextAlignment==TextAlignment.Right)));
        QaTools.CaptureWindow(detailIn,Path.Combine(output,"期间开票与收款明细_进项.png"));detailIn.Close();
        var printResult=new ReportService().Build("period_invoice_cash",new ReportFilter{From="2026-01-01",To="2026-01-31",InvoiceDirection=InvoiceKind.Out});
        var printTable=PrintHelper.BuildReportTable(printResult);
        Check("period report print table",printTable.Headers.Count==10&&printTable.Landscape&&printTable.TotalRow!=null&&printTable.RightAligned.Count(x=>x)==8);
        Check("period report print document",PrintHelper.Build(printTable).Pages.Count==1);
        for(var i=1;i<=30;i++)
        {
            var p=MasterDataService.SavePartner(new Partner{Code="PRINT-"+i.ToString("000"),Name="打印客户"+i,Kind="customer"});
            qaInvoices.Save(new Invoice{Kind=InvoiceKind.Out,InvoiceType="增值税专用发票",InvoiceNo="PRINT-INV-"+i.ToString("000"),PartnerId=p,RegDate="2026-03-05",GrossCents=113_000,NetCents=100_000,TaxCents=13_000,RateBp=1300});
        }
        var marchResult=new ReportService().Build("period_invoice_cash",new ReportFilter{From="2026-03-01",To="2026-03-31",InvoiceDirection=InvoiceKind.Out});
        Check("print report rows",marchResult.Rows.Count==30);
        var marchTable=PrintHelper.BuildReportTable(marchResult);
        Check("print report shape",marchTable.Headers.Count==10&&marchTable.Landscape&&marchTable.TotalRow!=null);
        var marchDoc=PrintHelper.Build(marchTable);
        Check("print report paginates",marchDoc.Pages.Count==2);
        Check("print total on last page",PageText(marchDoc.Pages[^1].Child).Contains("合计"));
        ReportResult Shape(int n)
        {
            var r=new ReportResult{Title="期间开票与收款统计",Period="打印边界",Basis="",FileName="shape"};
            foreach(var c in marchResult.Columns) r.Columns.Add(c);
            for(var i=0;i<n;i++) r.Rows.Add(new ReportRow{Cells=new object?[]{"P"+i,"客户"+i,1,0,1,113_000,100_000,13_000,50_000,63_000}});
            r.TotalRow=new object?[]{"合计","",n,0,n,n*113_000L,n*100_000L,n*13_000L,n*50_000L,n*63_000L};
            return r;
        }
        var shape27=Shape(27);var shape28=Shape(28);
        Check("print 10-column 27 rows one page",PrintHelper.Build(PrintHelper.BuildReportTable(shape27)).Pages.Count==1);
        var doc28=PrintHelper.Build(PrintHelper.BuildReportTable(shape28));
        Check("print 10-column 28 rows two pages",doc28.Pages.Count==2);
        Check("print boundary total on second page",PageText(doc28.Pages[^1].Child).Contains("合计"));
        var emptyPrint=new ReportService().Build("period_invoice_cash",new ReportFilter{From="2031-01-01",To="2031-01-31",InvoiceDirection=InvoiceKind.Out});
        var printRejected=false;try{PrintHelper.PrintReport(emptyPrint);}catch(AppException){printRejected=true;}catch{printRejected=false;}
        Check("empty report print rejected",printRejected);
        main.Close();
        foreach(var kind in new[]{InvoiceKind.Out,InvoiceKind.In})
        {
            var code=MasterDataService.GetPartner(kind==InvoiceKind.Out?customer:supplier)!.Code;var file=Path.Combine(output,kind+"-导入验收.xlsx");ExcelService.CreateInvoiceTemplate(kind,file);
            using(var wb=new XLWorkbook(file)){var ws=wb.Worksheet(1);for(int r=2;r<=3;r++){ws.Cell(r,1).Value=r==2?"2026-02-01":"2026-01-01";ws.Cell(r,2).Value="普通";ws.Cell(r,3).Value=r==2?"0000-RED-"+kind:"0000-BLUE-"+kind;ws.Cell(r,4).Value="000001";ws.Cell(r,5).Value=code;ws.Cell(r,6).Value=r==2?-11.3m:113m;ws.Cell(r,7).Value=13;ws.Cell(r,8).Value=r==2?-10m:100m;ws.Cell(r,9).Value=r==2?-1.3m:13m;if(r==2)ws.Cell(r,11).Value="0000-BLUE-"+kind;}wb.Save();}
            var view=new ImportView(kind);var window=new Window{Title="发票批量导入验收",Width=900,Height=600,Content=view};window.Show();Pump();view.LoadFile(file);Pump();
            var grid=Desc<DataGrid>(view).First();Check("bulk preview exposes invoice tax columns "+kind,new[]{"含税金额","不含税金额","税额","原蓝票号码","往来单位"}.All(n=>grid.Columns.Any(c=>c.Header?.ToString()==n))&&grid.Items.Count==2);
            Check("wide preview supports horizontal scroll "+kind,ScrollViewer.GetHorizontalScrollBarVisibility(grid)==ScrollBarVisibility.Auto);
            Check("all price tax fields visible at 900px "+kind,Desc<DataGridColumnHeader>(grid).Where(h=>h.Content?.ToString() is "含税金额" or "不含税金额" or "税额" or "税率").All(h=>h.TransformToAncestor(grid).TransformBounds(new Rect(h.RenderSize)).Right<=grid.ActualWidth));
            if(kind==InvoiceKind.Out)QaTools.CaptureWindow(window,Path.Combine(output,"发票批量导入预览.png"));
            var commit=Desc<Button>(view).Single(b=>b.Content?.ToString()=="确认导入");Check("bulk commit enabled after valid preview "+kind,commit.IsEnabled);commit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();Check("bulk commit prevents repeated click "+kind,!commit.IsEnabled);
            var saved=new InvoiceService().List(kind,"0000-",null,null,null,false);Check("actual UI bulk save and original IDs "+kind,saved.Count==2&&saved.Single(i=>i.GrossCents<0).OriginalInvoiceId==saved.Single(i=>i.GrossCents>0).Id);
            var savedId=saved.Single(i=>i.GrossCents>0).Id;window.Close();
            var invWindow=new InvoiceWindow(kind,new InvoiceService().Get(savedId));invWindow.Show();Pump();Check("imported invoice reopens with original amount and custom type "+kind,((TextBox)invWindow.FindName("TxtGross")).Text=="113.00"&&((ComboBox)invWindow.FindName("CmbType")).SelectedItem is ComboItem type&&type.Name=="普通");invWindow.Close();
        }
        foreach(var (landscape,rows,expected) in new[]{(false,43,1),(false,44,2),(false,90,3),(true,27,1),(true,28,2)})
        {
            var table=new PrintTable{Title="打印分页边界测试",Subtitle="长标题与长单元格"+new string('长',100),Landscape=landscape,Note=new string('注',120)};
            table.Headers.AddRange(new[]{"账户", "金额"});table.Widths.AddRange(new[]{300d,150d});table.RightAligned.AddRange(new[]{false,true});
            for(int i=0;i<rows;i++)table.Rows.Add(new[]{new string('长',80),"18,974.86"});table.TotalRow=new[]{"合计","18,974.86"};
            var doc=PrintHelper.Build(table);Check($"print total row consumes page slot {landscape}/{rows}",doc.Pages.Count==expected);
            if(!landscape&&rows==44)
            {
                var page=doc.Pages[1].Child;page.Measure(new Size(page.Width,page.Height));page.Arrange(new Rect(0,0,page.Width,page.Height));page.UpdateLayout();
                var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)page.Width,(int)page.Height,96,96,PixelFormats.Pbgra32);bitmap.Render(page);
                var png=new System.Windows.Media.Imaging.PngBitmapEncoder();png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));using var image=File.Create(Path.Combine(output,"合计行单独分页预览.png"));png.Save(image);
            }
        }
        Check("current schema version",AppSession.Db.GetUserVersion()==Database.CurrentVersion);
        log.Add($"RESULT checks={checks} failures={failures}");File.WriteAllLines(Path.Combine(output,"layout-result.txt"),log);return failures==0;
    }
    private static void Pump(){var f=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>f.Continue=false));Dispatcher.PushFrame(f);}
    private static IEnumerable<T> Desc<T>(DependencyObject root) where T:DependencyObject{for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var c=VisualTreeHelper.GetChild(root,i);if(c is T t)yield return t;foreach(var x in Desc<T>(c))yield return x;}}
    private static string PageText(FixedPage page)
    {
        page.Measure(new Size(page.Width,page.Height));page.Arrange(new Rect(0,0,page.Width,page.Height));page.UpdateLayout();
        if(page.Children.Count==0)return "";
        var host=page.Children[0];
        if(VisualTreeHelper.GetChildrenCount(host)==0)return "";
        var drawing=VisualTreeHelper.GetDrawing((DrawingVisual)VisualTreeHelper.GetChild(host,0));
        var text=new System.Text.StringBuilder();
        void Walk(Drawing d)
        {
            if(d is DrawingGroup g){foreach(var child in g.Children)Walk(child);}
            else if(d is GlyphRunDrawing gr&&gr.GlyphRun?.Characters!=null)text.Append(gr.GlyphRun.Characters.ToArray());
        }
        if(drawing!=null)Walk(drawing);
        return text.ToString();
    }
}

