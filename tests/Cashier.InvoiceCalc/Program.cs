using Cashier.Core;
using Cashier.Core.Services;
using ClosedXML.Excel;

var dir = Path.GetFullPath(args[0]);
Directory.CreateDirectory(dir);
AppSession.Db = new Database(dir);
AppSession.Db.EnsureCreated();
AppSession.Current = new AuthService().Login("admin", "admin888");
var fails = 0;
void Check(string label, bool ok) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")} | {label}"); if (!ok) fails++; }
foreach (var text in new[] { "0", "13", "13.01", "100", "6.010" })
{
    try { var rate = InvoiceService.ParseRate(text); Check($"合法税率 {text}", rate == (int)(decimal.Parse(text) * 100)); }
    catch { Check($"合法税率 {text}", false); }
}
foreach (var text in new[] { "13.001", "13.999", "0.001", "100.001", "-0.01" })
{
    try { InvoiceService.ParseRate(text); Check($"拒绝不支持的税率 {text}", false); }
    catch (AppException) { Check($"拒绝不支持的税率 {text}", true); }
}
var partner = MasterDataService.SavePartner(new Partner { Code="INV-TEST", Name="发票测试客户", Kind="customer" });
var partnerCode = MasterDataService.GetPartner(partner)!.Code;
var vs = new VoucherService();
var biz = vs.Create(new Voucher { Kind=VoucherKind.Receivable, BizDate="2020-02-10", AmountCents=10000, PartnerId=partner, Summary="跨月业务", Invoiceable=true });
vs.Audit(biz);
var svc = new InvoiceService();
var invoiceId = svc.Save(new Invoice { Kind=InvoiceKind.Out, InvoiceType="普通", InvoiceNo="TEST-INV", PartnerId=partner, RegDate="2020-01-10", GrossCents=10000, NetCents=10000, TaxCents=0, RateBp=0 });
svc.Link(invoiceId, biz, 6000);
var reports = new ReportService();
long Linked(string end, string no) => Convert.ToInt64(reports.Build("invoice_out_detail", new ReportFilter { From="2020-01-01", To=end, PartnerId=partner }).Rows.Single(x => x.Cells[1]?.ToString()==no).Cells[9]);
Check("关联生效前不计入", Linked("2020-01-31", "TEST-INV")==0);
Check("关联生效后计入", Linked("2020-02-28", "TEST-INV")==6000);
var red = svc.Save(new Invoice { Kind=InvoiceKind.Out, InvoiceType="普通", InvoiceNo="TEST-RED", PartnerId=partner, RegDate="2020-03-10", GrossCents=-2000, NetCents=-2000, TaxCents=0, RateBp=0, OriginalInvoiceId=invoiceId });
svc.Link(red, biz, 2000);
Check("红字关联按负数显示", Linked(DateTime.Today.ToString("yyyy-MM-dd"), "TEST-RED")==-2000);
svc.Unlink(svc.Links(red).Single().linkId, "验证红票历史");
Check("解除红字关联保留历史", Linked("2020-03-31", "TEST-RED")==-2000);
svc.Unlink(svc.Links(invoiceId).Single().linkId, "验证历史");
Check("解除后保留生效期间历史", Linked("2020-02-28", "TEST-INV")==6000);
Check("解除后当前截止日不计入", Linked(DateTime.Today.ToString("yyyy-MM-dd"), "TEST-INV")==0);
svc.Link(invoiceId, biz, 6000);
Check("重新关联不重复计入历史", Linked("2020-02-28", "TEST-INV")==6000);
Check("重新关联当前计入一次", Linked(DateTime.Today.ToString("yyyy-MM-dd"), "TEST-INV")==6000);
var red2 = svc.Save(new Invoice { Kind=InvoiceKind.Out, InvoiceType="普通", InvoiceNo="TEST-RED2", PartnerId=partner, RegDate="2020-03-11", GrossCents=-1000, NetCents=-1000, TaxCents=0, RateBp=0, OriginalInvoiceId=invoiceId });
svc.Link(red2, biz, 1000);
Check("红票关联不得早于重建的蓝票关联", Linked("2020-03-31", "TEST-RED2")==0);
Check("蓝票重新关联生效后红票冲减", Linked(DateTime.Today.ToString("yyyy-MM-dd"), "TEST-RED2")==-1000);
var import = Path.Combine(dir, "invoice-precision.xlsx");
ExcelService.CreateInvoiceTemplate(InvoiceKind.Out, import);
using (var book = new XLWorkbook(import))
{
    var sheet = book.Worksheet(1);
    for (int row=2; row<=7; row++)
    {
        sheet.Cell(row,1).Value="2020-04-10"; sheet.Cell(row,2).Value="普通";
        sheet.Cell(row,3).Value="P"+row; sheet.Cell(row,5).Value=partnerCode;
        sheet.Cell(row,6).Value=113m; sheet.Cell(row,8).Value=100m; sheet.Cell(row,9).Value=13m;
    }
    sheet.Cell(2,7).Value=13.999; sheet.Cell(2,7).Style.NumberFormat.Format="0.00";
    sheet.Cell(3,7).Value=0.13999; sheet.Cell(3,7).Style.NumberFormat.Format="0.00%";
    sheet.Cell(4,7).Value=0.13; sheet.Cell(4,7).Style.NumberFormat.Format="0.00%";
    sheet.Cell(5,7).Value=13; sheet.Cell(5,7).Style.NumberFormat.Format="0.00";
    sheet.Cell(6,7).Value=13; sheet.Cell(6,7).Style.NumberFormat.Format="0.00\"%\"";
    sheet.Cell(7,7).Value=13; sheet.Cell(7,6).Value=113.001; sheet.Cell(7,6).Style.NumberFormat.Format="0.00";
    book.Save();
}
var preview=ExcelService.PreviewInvoiceImport(InvoiceKind.Out,import);
var rows=preview.Rows.ToDictionary(x=>x.RowNo);
Check("数字格式不能掩盖税率精度", !rows[2].Ok);
Check("百分比格式不能掩盖税率精度", !rows[3].Ok);
Check("百分比数字单元格正确识别", rows[4].Ok && rows[4].Invoice!.RateBp==1300);
Check("普通数字单元格正确识别", rows[5].Ok && rows[5].Invoice!.RateBp==1300);
Check("百分号字面文本不改变数值", rows[6].Ok && rows[6].Invoice!.RateBp==1300);
Check("金额格式不能掩盖精度", !rows[7].Ok);
Console.WriteLine($"RESULT failures={fails}");
return fails == 0 ? 0 : 1;
