using Cashier.Core;
using Cashier.Core.Services;
using ClosedXML.Excel;

var dir = Path.GetFullPath(args[0]);
Directory.CreateDirectory(dir);
AppSession.Db = new Database(dir);
if (args.Length > 1 && args[1] == "--migration")
{
    var oldVersion = AppSession.Db.GetUserVersion();
    if (oldVersion != 1) throw new Exception($"Expected original v1 database, got {oldVersion}");
    long voucherCount, legacyInvoiceCount, settlementCount, linkCount;
    using (var conn = AppSession.Db.Open())
    {
        long Count(string table) => new SqlCmd(conn, $"SELECT COUNT(*) FROM {table}").Long();
        voucherCount=Count("vouchers"); legacyInvoiceCount=Count("invoices"); settlementCount=Count("settlements"); linkCount=Count("invoice_links");
    }
    AppSession.Db.EnsureCreated(Console.WriteLine);
    bool valid;
    using (var conn = AppSession.Db.Open())
    {
        long Count(string table) => new SqlCmd(conn, $"SELECT COUNT(*) FROM {table}").Long();
        valid = AppSession.Db.GetUserVersion()==Database.CurrentVersion && Count("vouchers")==voucherCount && Count("invoices")==legacyInvoiceCount &&
                    Count("settlements")==settlementCount && Count("invoice_links")==linkCount &&
                    new SqlCmd(conn,"SELECT COUNT(*) FROM pragma_foreign_key_check").Long()==0 &&
                    Directory.GetFiles(AppSession.Db.BackupDir,$"*preupgrade-v1-to-v{Database.CurrentVersion}*").Length==1;
    }
    Console.WriteLine($"MIGRATION {(valid?"PASS":"FAIL")} | vouchers={voucherCount}; invoices={legacyInvoiceCount}; settlements={settlementCount}; links={linkCount}; version={AppSession.Db.GetUserVersion()}");
    var oldBackup=Directory.GetFiles(AppSession.Db.BackupDir,$"*preupgrade-v1-to-v{Database.CurrentVersion}*").Single();
    new AuthService().Login("admin", "admin888");
    var safety=AppSession.Db.RestoreFrom(oldBackup,Console.WriteLine);
    using(var conn=AppSession.Db.Open())
    {
        valid = valid && File.Exists(safety) && AppSession.Db.GetUserVersion()==Database.CurrentVersion &&
            new SqlCmd(conn,"SELECT COUNT(*) FROM vouchers").Long()==voucherCount &&
            new SqlCmd(conn,"SELECT COUNT(*) FROM invoices").Long()==legacyInvoiceCount &&
            new SqlCmd(conn,"SELECT COUNT(*) FROM pragma_foreign_key_check").Long()==0;
    }
    Console.WriteLine($"RESTORE V1 AND RE-MIGRATE {(valid?"PASS":"FAIL")} | safety backup exists={File.Exists(safety)}");
    return valid ? 0 : 1;
}
AppSession.Db.EnsureCreated();
AppSession.Current = new AuthService().Login("admin", "admin888");
var a = MasterDataService.SavePartner(new Partner { Code="QA-A", Name="独立测试客户A", Kind="customer" });
var aCode = MasterDataService.GetPartner(a)!.Code;
var b = MasterDataService.SavePartner(new Partner { Code="QA-B", Name="独立测试客户B", Kind="customer" });
var accounts = MasterDataService.ListAccounts(false);
var bank = accounts.First(x=>x.Kind=="bank").Id;
var cash = accounts.First(x=>x.Kind=="cash").Id;
var vs = new VoucherService();
long Post(Voucher v) { var id=vs.Create(v); vs.Audit(id); return id; }
var failures=0;
var checks=0;
void Check(string name, bool ok, string evidence) { checks++; if (!ok) failures++; Console.WriteLine($"{(ok?"PASS":"FAIL")} | {name} | {evidence}"); }
ReportFilter Filter(string from, string to, long? partner=null, long? account=null) => new() {From=from, To=to, PartnerId=partner, AccountId=account};
ReportRow Find(ReportResult report,string name) => report.Rows.First(x=>x.Cells[0]?.ToString()==name);

var jan=Post(new Voucher{Kind=VoucherKind.Receivable,BizDate="2026-01-10",AmountCents=100_00,PartnerId=a,Summary="1月业务",CountAsRevenue=true,Invoiceable=true});
var feb=Post(new Voucher{Kind=VoucherKind.Receivable,BizDate="2026-02-10",AmountCents=200_00,PartnerId=a,Summary="2月业务",CountAsRevenue=true,Invoiceable=true});
var rs=new ReportService();
var febReport=rs.Build("customer_summary",Filter("2026-02-01","2026-02-28",a));
var febRow=Find(febReport,"独立测试客户A");
var febReported=Convert.ToInt64(febRow.Cells[2]);
Check("客户汇总本期应收排除上月发生额",febReported==200_00,$"期望200.00元；实际{febReported/100m:0.00}元；期初列{Convert.ToInt64(febRow.Cells[1])/100m:0.00}元");
Check("客户汇总仅显示选定客户",febReport.Rows.Count==1,$"选择客户A，实际{febReport.Rows.Count}行：{string.Join(",",febReport.Rows.Select(x=>x.Cells[0]))}");
var ar=rs.Build("account_report",Filter("2026-01-01","2026-02-28",null,bank));
Check("账户报表仅显示选定账户",ar.Rows.Count==1,$"选择一个账户，实际{ar.Rows.Count}行：{string.Join(",",ar.Rows.Select(x=>x.Cells[0]))}");
var supplier=MasterDataService.SavePartner(new Partner{Code="QA-SUP",Name="独立测试供应商",Kind="supplier"});
var supplierJan=Post(new Voucher{Kind=VoucherKind.Payable,BizDate="2026-01-11",AmountCents=80_00,PartnerId=supplier,Summary="1月应付"});
Post(new Voucher{Kind=VoucherKind.Payable,BizDate="2026-02-11",AmountCents=120_00,PartnerId=supplier,Summary="2月应付"});
var supplierRow=Find(rs.Build("supplier_summary",Filter("2026-02-01","2026-02-28",supplier)),"独立测试供应商");
Check("供应商汇总区分期初与本期",Convert.ToInt64(supplierRow.Cells[1])==80_00 && Convert.ToInt64(supplierRow.Cells[2])==120_00 && Convert.ToInt64(supplierRow.Cells[5])==200_00,$"期初={Convert.ToInt64(supplierRow.Cells[1])/100m:0.00}，本期={Convert.ToInt64(supplierRow.Cells[2])/100m:0.00}，期末={Convert.ToInt64(supplierRow.Cells[5])/100m:0.00}");
var supplierPay=Post(new Voucher{Kind=VoucherKind.Expense,Nature=VoucherNature.SupplierPayment,BizDate="2026-02-12",AmountCents=80_00,PartnerId=supplier,AccountId=bank,Summary="2月供应商付款"});
new SettlementService().Allocate(supplierPay,supplierJan,80_00);
var supplierDetail=rs.Build("payable_detail",Filter("2026-01-01","2026-01-31",supplier)).Rows.Single();
Check("历史应付明细不计未来付款核销",Convert.ToInt64(supplierDetail.Cells[6])==0 && Convert.ToInt64(supplierDetail.Cells[7])==80_00,$"1月底已核销={Convert.ToInt64(supplierDetail.Cells[6])/100m:0.00}，未付={Convert.ToInt64(supplierDetail.Cells[7])/100m:0.00}");

var invo=new InvoiceService();
long SaveInvoice(string no,long partner,string date,long cents) => invo.Save(new Invoice {Kind=InvoiceKind.Out,InvoiceType="普通",InvoiceNo=no,PartnerId=partner,RegDate=date,GrossCents=cents,NetCents=cents,TaxCents=0,RateBp=0});
var future=SaveInvoice("I-FUTURE",a,"2026-02-15",100_00);
invo.Link(future,jan,100_00);
var janRow=Find(rs.Build("customer_summary",Filter("2026-01-01","2026-01-31",a)),"独立测试客户A");
Check("历史客户报表不出现未来发票",Convert.ToInt64(janRow.Cells[6])==0 && Convert.ToInt64(janRow.Cells[7])==100_00,$"截至1月31日，未来2月发票；已开票={Convert.ToInt64(janRow.Cells[6])/100m:0.00}元，未开票={Convert.ToInt64(janRow.Cells[7])/100m:0.00}元");

var dead=SaveInvoice("I-VOID",a,"2026-03-01",100_00); invo.Void(dead,"独立测试作废");
SaveInvoice("I-VALID",a,"2026-03-02",200_00);
var ir=rs.Build("invoice_out_detail",Filter("2026-03-01","2026-03-31",a));
var invTotal=Convert.ToInt64(ir.TotalRow![6]);
Check("作废发票不计入有效发票明细合计",invTotal==200_00,$"有效200.00元，作废100.00元；实际合计{invTotal/100m:0.00}元；行数{ir.Rows.Count}");

var changed=invo.Get(future)!; changed.PartnerId=b;
try { invo.Save(changed); var v=vs.Get(jan)!; var after=invo.Get(future)!; Check("已关联发票不得改为其他客户",false,$"允许修改：业务客户A，发票客户ID变为{after.PartnerId}，业务仍显示已关联{v.InvoiceLinkedCents/100m:0.00}元"); }
catch(Exception e) { Check("已关联发票不得改为其他客户",true,e.Message); }

var pay=Post(new Voucher{Kind=VoucherKind.Income,Nature=VoucherNature.CustomerReceipt,BizDate="2026-02-20",AmountCents=100_00,PartnerId=a,AccountId=bank,Summary="2月回款"});
new SettlementService().Allocate(pay,jan,100_00);
var detail=rs.Build("receivable_detail",Filter("2026-01-01","2026-01-31",a));
var janDetail=detail.Rows.First(x=>x.Cells[0]?.ToString()=="2026-01-10");
Check("历史应收明细不计未来收款核销",Convert.ToInt64(janDetail.Cells[6])==0,$"截至1月31日，2月回款核销后显示已核销{Convert.ToInt64(janDetail.Cells[6])/100m:0.00}元，未收{Convert.ToInt64(janDetail.Cells[7])/100m:0.00}元");
var settlement = new SettlementService().ListByPay(pay).Single();
new SettlementService().Unallocate(settlement.Id,"回归测试撤销");
var oldDetail=rs.Build("receivable_detail",Filter("2026-01-01","2026-08-31",a)).Rows.First(x=>x.Cells[0]?.ToString()=="2026-01-10");
Check("撤销核销保留此前历史报表",Convert.ToInt64(oldDetail.Cells[6])==100_00,$"撤销后8月报表历史核销={Convert.ToInt64(oldDetail.Cells[6])/100m:0.00}");
new SettlementService().Allocate(pay,jan,100_00);
oldDetail=rs.Build("receivable_detail",Filter("2026-01-01","2026-08-31",a)).Rows.First(x=>x.Cells[0]?.ToString()=="2026-01-10");
Check("撤销后重新核销不重复计入历史",Convert.ToInt64(oldDetail.Cells[6])==100_00,$"重新核销后8月历史核销={Convert.ToInt64(oldDetail.Cells[6])/100m:0.00}");

long priorBank=MasterDataService.AccountBalance(bank), priorCash=MasterDataService.AccountBalance(cash);
var transfer=vs.Create(new Voucher{Kind=VoucherKind.Transfer,BizDate="2026-04-10",AmountCents=1_000_00,FeeCents=2_00,AccountId=bank,ToAccountId=cash,Summary="带手续费转账"});
vs.Audit(transfer);
var afterBank=MasterDataService.AccountBalance(bank); var afterCash=MasterDataService.AccountBalance(cash);
var fee=vs.Query(new VoucherQuery{Kind=VoucherKind.Expense,From="2026-04-10",To="2026-04-10"}).First(x=>x.RefId==transfer);
Check("审核转账同时计入手续费",priorBank-afterBank==1_002_00 && afterCash-priorCash==1_000_00,$"转1000.00元，手续费2.00元；银行减少{(priorBank-afterBank)/100m:0.00}元，现金增加{(afterCash-priorCash)/100m:0.00}元，手续费状态={fee.Status}");
try { vs.Unaudit(fee.Id,"测试"); Check("禁止单独反审核手续费",false,"手续费可单独反审核"); }
catch(AppException e) { Check("禁止单独反审核手续费",true,e.Message); }
vs.Unaudit(transfer,"测试转账反审核");
Check("反审核转账同时反审核手续费",vs.Get(fee.Id)!.Status==VoucherStatus.Pending && MasterDataService.AccountBalance(bank)==priorBank && MasterDataService.AccountBalance(cash)==priorCash,"余额与手续费状态同时回退");
vs.Audit(transfer);
Check("重新审核转账不会重复计费",MasterDataService.AccountBalance(bank)==priorBank-1_002_00,"重新审核后仅扣一次手续费");
vs.Void(transfer,"测试作废转账");
Check("作废转账同时作废手续费",vs.Get(fee.Id)!.Status==VoucherStatus.Void && MasterDataService.AccountBalance(bank)==priorBank && MasterDataService.AccountBalance(cash)==priorCash,"转账、手续费同时作废");

var pendingTransfer=vs.Create(new Voucher{Kind=VoucherKind.Transfer,BizDate="2026-04-11",AmountCents=100_00,FeeCents=2_00,AccountId=bank,ToAccountId=cash,Summary="修改手续费"});
var pt=vs.Get(pendingTransfer)!; pt.FeeCents=5_00; vs.Update(pt);
var fee2=vs.Query(new VoucherQuery{Kind=VoucherKind.Expense,From="2026-04-11",To="2026-04-11"}).First(x=>x.RefId==pendingTransfer);
Check("修改转账手续费后关联费用同步",fee2.AmountCents==5_00,$"转账手续费改为5.00元，关联费用仍为{fee2.AmountCents/100m:0.00}元");

var closedBiz=Post(new Voucher{Kind=VoucherKind.Receivable,BizDate="2026-05-10",AmountCents=50_00,PartnerId=b,Summary="关闭期间应收"});
var closedPay=Post(new Voucher{Kind=VoucherKind.Income,Nature=VoucherNature.CustomerReceipt,BizDate="2026-05-11",AmountCents=50_00,PartnerId=b,AccountId=bank,Summary="关闭期间回款"});
PeriodService.Close("202605");
try { new SettlementService().Allocate(closedPay,closedBiz,50_00); Check("关闭期间限制核销变动",false,"5月已关闭，仍允许对5月业务及5月回款新增核销"); }
catch(Exception e) { Check("关闭期间限制核销变动",true,e.Message); }

var lockedInvId=SaveInvoice("I-LOCK",b,"2026-06-10",50_00);
PeriodService.Close("202606");
var lockedInv=invo.Get(lockedInvId)!; lockedInv.RegDate="2026-07-10";
try { invo.Save(lockedInv); Check("关闭期间发票不可修改并移出",false,"6月已关闭，仍允许将原6月发票日期修改为7月"); }
catch(Exception e) { Check("关闭期间发票不可修改并移出",true,e.Message); }

var redId = invo.Save(new Invoice {Kind=InvoiceKind.Out,InvoiceType="普通",InvoiceNo="I-NEG",PartnerId=a,RegDate="2026-07-10",GrossCents=-30_00,NetCents=-30_00,TaxCents=0,RateBp=0,OriginalInvoiceId=future});
invo.Link(redId,jan,30_00);
var signedInv=invo.Get(redId)!;
var (invoiceable, invoiced, uninvoiced) = invo.PartnerInvoiceSummary(a,InvoiceKind.Out);
Check("红字发票正确冲减已关联金额",signedInv.GrossCents==-30_00 && signedInv.LinkedCents==-30_00 && invoiced==70_00 && uninvoiced==230_00,$"红票={signedInv.GrossCents/100m:0.00}，已关联={signedInv.LinkedCents/100m:0.00}，已开={invoiced/100m:0.00}，未开={uninvoiced/100m:0.00}");
var redChoices=invo.InvoiceableVouchers(a,InvoiceKind.Out,future);
Check("红字发票业务选择显示原票剩余可冲金额",redChoices.Count==1 && redChoices[0].Id==jan && redChoices[0].UninvoicedCents==70_00,$"可选业务={redChoices.Count}，剩余可冲={redChoices.FirstOrDefault()?.UninvoicedCents/100m:0.00}");
try { invo.Unlink(invo.Links(future).Single().linkId,"测试"); Check("原票有红字关联时不能解除",false,"允许解除原票关联"); }
catch(AppException e) { Check("原票有红字关联时不能解除",true,e.Message); }
var redLink=invo.Links(redId).Single();
invo.Unlink(redLink.linkId,"回归测试撤销红字关联");
var oldInvoiceRow=Find(rs.Build("customer_summary",Filter("2026-01-01","2026-08-31",a)),"独立测试客户A");
Check("撤销红票关联保留历史结果",Convert.ToInt64(oldInvoiceRow.Cells[6])==70_00,$"8月已开票={Convert.ToInt64(oldInvoiceRow.Cells[6])/100m:0.00}");
invo.Link(redId,jan,30_00);
oldInvoiceRow=Find(rs.Build("customer_summary",Filter("2026-01-01","2026-08-31",a)),"独立测试客户A");
Check("重新关联红票不重复影响历史",Convert.ToInt64(oldInvoiceRow.Cells[6])==70_00,$"8月已开票={Convert.ToInt64(oldInvoiceRow.Cells[6])/100m:0.00}");
try { invo.Save(new Invoice {Kind=InvoiceKind.Out,InvoiceType="普通",InvoiceNo="I-INVALID",PartnerId=a,RegDate="2026-07-10",GrossCents=-1_00,NetCents=-1_00,TaxCents=0}); Check("红字发票必须指定原票",false,"缺少原票仍允许保存"); }
catch(AppException e) { Check("红字发票必须指定原票",true,e.Message); }
try { invo.Save(new Invoice {Kind=InvoiceKind.Out,InvoiceType="普通",InvoiceNo="I-TOOMUCH",PartnerId=a,RegDate="2026-07-10",GrossCents=-80_00,NetCents=-80_00,TaxCents=0,OriginalInvoiceId=future}); Check("红字金额不能超过原票",false,"超额红票仍允许保存"); }
catch(AppException e) { Check("红字金额不能超过原票",true,e.Message); }

var bankCode=accounts.First(x=>x.Id==bank).Code;
var dupFile=Path.Combine(dir,"重复导入.xlsx");
ExcelService.CreateVoucherTemplate(VoucherKind.Income,dupFile);
using(var w=new XLWorkbook(dupFile)) {
 var s=w.Worksheet(1);
 for(int r=2;r<=3;r++) { s.Cell(r,1).Value="2026-08-10"; s.Cell(r,2).Value="SaleCash"; s.Cell(r,3).Value=12.34; s.Cell(r,4).Value=bankCode; s.Cell(r,10).Value="独立重复样例"; }
 w.Save();
}
var dupPreview=ExcelService.PreviewVoucherImport(VoucherKind.Income,dupFile);
var dupCount=ExcelService.CommitVoucherImport(dupPreview);
Check("同一Excel内重复行拦截",dupCount==1,$"两行日期、性质、金额、账户、摘要完全相同；预览重复{dupPreview.DuplicateCount}行，实际导入{dupCount}张");

var invoiceFile=Path.Combine(dir,"发票导入.xlsx");
ExcelService.CreateInvoiceTemplate(InvoiceKind.Out,invoiceFile);
using(var w=new XLWorkbook(invoiceFile)) {
 var s=w.Worksheet(1);
 for(int r=2;r<=3;r++) { s.Cell(r,1).Value="2026-08-15"; s.Cell(r,2).Value="普通"; s.Cell(r,3).Value="00001234"; s.Cell(r,4).Value="0012"; s.Cell(r,5).Value=aCode; s.Cell(r,6).Value=113.00; s.Cell(r,7).Value=13; s.Cell(r,8).Value=100.01; s.Cell(r,9).Value=12.99; }
 w.Save();
}
var invoicePreview=ExcelService.PreviewInvoiceImport(InvoiceKind.Out,invoiceFile);
var invoiceCount=ExcelService.CommitInvoiceImport(invoicePreview);
var importedInvoice=invo.List(InvoiceKind.Out,"00001234",null,null,null,false).Single();
Check("发票导入去重并保留前导零和票面金额",invoiceCount==1 && invoicePreview.DuplicateCount==1 && importedInvoice.InvoiceCode=="0012" && importedInvoice.NetCents==100_01 && importedInvoice.TaxCents==12_99,$"导入{invoiceCount}张，重复{invoicePreview.DuplicateCount}行，代码={importedInvoice.InvoiceCode}，票面={importedInvoice.NetCents/100m:0.00}+{importedInvoice.TaxCents/100m:0.00}");

var invRollbackFile=Path.Combine(dir,"发票批量回滚.xlsx");
ExcelService.CreateInvoiceTemplate(InvoiceKind.Out,invRollbackFile);
using(var w=new XLWorkbook(invRollbackFile)) {
 var s=w.Worksheet(1);
 for(int r=2;r<=3;r++) { s.Cell(r,1).Value=r==2?"2026-08-16":"2026-05-16"; s.Cell(r,2).Value="普通"; s.Cell(r,3).Value="I-ROLL"+r; s.Cell(r,5).Value=aCode; s.Cell(r,6).Value=10; s.Cell(r,7).Value=0; s.Cell(r,8).Value=10; s.Cell(r,9).Value=0; }
 w.Save();
}
PeriodService.Reopen("202605","测试发票导入");
var invRollbackPreview=ExcelService.PreviewInvoiceImport(InvoiceKind.Out,invRollbackFile);
PeriodService.Close("202605");
try { ExcelService.CommitInvoiceImport(invRollbackPreview); Check("发票导入失败整批回滚",false,"关闭期间后仍导入成功"); }
catch(Exception e) { var kept=invo.List(InvoiceKind.Out,"I-ROLL2",null,null,null,false).Count; Check("发票导入失败整批回滚",invRollbackPreview.OkCount==2 && kept==0,$"预览{invRollbackPreview.OkCount}张，失败后第一张存留{kept}，原因：{e.Message}"); }

var redImportFile=Path.Combine(dir,"红字发票导入.xlsx");
ExcelService.CreateInvoiceTemplate(InvoiceKind.Out,redImportFile);
using(var w=new XLWorkbook(redImportFile)) {
 var s=w.Worksheet(1); s.Cell(2,1).Value="2026-08-18"; s.Cell(2,2).Value="普通"; s.Cell(2,3).Value="I-RED-IMPORT";
 s.Cell(2,5).Value=aCode; s.Cell(2,6).Value=-10; s.Cell(2,7).Value=0; s.Cell(2,8).Value=-10; s.Cell(2,9).Value=0; s.Cell(2,11).Value="I-FUTURE"; w.Save();
}
var redPreview=ExcelService.PreviewInvoiceImport(InvoiceKind.Out,redImportFile);
try { var count=ExcelService.CommitInvoiceImport(redPreview); var ri=invo.List(InvoiceKind.Out,"I-RED-IMPORT",null,null,null,false).Single(); Check("红字发票按原票导入",count==1 && ri.GrossCents==-10_00 && ri.OriginalInvoiceId==future,$"导入{count}张，金额{ri.GrossCents/100m:0.00}元，原票ID={ri.OriginalInvoiceId}"); }
catch(Exception e) { Check("红字发票按原票导入",false,e.Message); }

var pastPartner=MasterDataService.SavePartner(new Partner{Code="QA-CROSS",Name="跨月核销客户",Kind="customer"});
var pastBiz=Post(new Voucher{Kind=VoucherKind.Receivable,BizDate="2026-01-20",AmountCents=10_00,PartnerId=pastPartner,Summary="上月应收"});
PeriodService.Close("202601");
var currentPay=Post(new Voucher{Kind=VoucherKind.Income,Nature=VoucherNature.CustomerReceipt,BizDate="2026-02-22",AmountCents=10_00,PartnerId=pastPartner,AccountId=bank,Summary="本月收款"});
try { new SettlementService().Allocate(currentPay,pastBiz,10_00); Check("上月关闭后可在本月核销",true,"核销在2月生效"); }
catch(Exception e) { Check("上月关闭后可在本月核销",false,e.Message); }

var partialFile=Path.Combine(dir,"部分失败导入.xlsx");
ExcelService.CreateVoucherTemplate(VoucherKind.Income,partialFile);
using(var w=new XLWorkbook(partialFile)) {
 var s=w.Worksheet(1);
 for(int r=2;r<=3;r++) { s.Cell(r,1).Value=r==2?"2026-08-11":"2026-05-12"; s.Cell(r,2).Value="SaleCash"; s.Cell(r,3).Value=42.0; s.Cell(r,4).Value=bankCode; s.Cell(r,10).Value="导入部分失败"+r; }
 w.Save();
}
PeriodService.Reopen("202605","测试提交期间变化");
var partialPreview=ExcelService.PreviewVoucherImport(VoucherKind.Income,partialFile);
PeriodService.Close("202605");
foreach(var r in partialPreview.Rows) Console.WriteLine($"INFO | 导入预览 | 行={r.RowNo} 日期={r.Voucher?.BizDate} 可用={r.Ok} 重复={r.Duplicate} 错误={r.Error}");
try { ExcelService.CommitVoucherImport(partialPreview); Check("导入异常时整批回滚",false,"关闭期间后仍导入成功"); }
catch(Exception e) { var kept=vs.Query(new VoucherQuery {Kind=VoucherKind.Income,From="2026-08-11",To="2026-08-11"}).Count(v=>v.Summary=="导入部分失败2"); Check("导入异常时整批回滚",kept==0,$"预览可导入={partialPreview.OkCount}；后行失败={e.Message}；前行仍存留={kept}"); }

var bad=rs.Build("cash_detail",Filter("2026-02-01","2026-02-28",a));
try { var outFile=Path.Combine(dir,"独立报表.xlsx"); ExcelService.ExportReport(bad,outFile); Check("真实生成Excel文件",File.Exists(outFile) && new FileInfo(outFile).Length>1000,$"文件 {outFile}, 字节 {new FileInfo(outFile).Length}"); }
catch(Exception e) { Check("真实生成Excel文件",false,e.GetType().Name+": "+e.Message); }
using(var w=new XLWorkbook(Path.Combine(dir,"独立报表.xlsx"))) { Check("Excel保留报表口径",w.Worksheet(1).Cell(3,1).GetString().Contains("口径说明"),w.Worksheet(1).Cell(3,1).GetString()); }

var phantom=Post(new Voucher{Kind=VoucherKind.Income,Nature=VoucherNature.SaleCash,BizDate="2026-09-10",AmountCents=19_00,AccountId=bank,Summary="模拟旧版隐藏转入账户"});
using(var conn=AppSession.Db.Open()) new SqlCmd(conn,"UPDATE vouchers SET to_account_id=$cash WHERE id=$id").P("$cash",cash).P("$id",phantom).Exec();
var cashRow=rs.Build("account_report",Filter("2026-09-01","2026-09-30",null,cash)).Rows.Single();
var cashMonths=rs.Build("account_month",Filter("2026-09-01","2026-09-30",null,cash));
var phantomQuery=vs.Query(new VoucherQuery{Kind=VoucherKind.Income,From="2026-09-01",To="2026-09-30",AccountId=cash});
Check("旧版多余转入账户不产生虚假余额",Convert.ToInt64(cashRow.Cells[2])==0 && cashMonths.Rows.Count==0 && phantomQuery.All(v=>v.Id!=phantom),$"现金账户流入={Convert.ToInt64(cashRow.Cells[2])/100m:0.00}，月报行数={cashMonths.Rows.Count}，错误查询结果={phantomQuery.Count}");
Check("旧版多余账户被标记核对",IntegrityCheckService.FindIssues().Any(x=>x.Contains("多余账户")),"发现旧版单据多余账户提示");
try { vs.Create(new Voucher{Kind=VoucherKind.Income,Nature=VoucherNature.SaleCash,BizDate="2026-09-11",AmountCents=100,AccountId=bank,ToAccountId=cash,Summary="禁止多余转入账户"}); Check("新收支单拒绝多余转入账户",false,"错误单据被接受"); }
catch(AppException e) { Check("新收支单拒绝多余转入账户",true,e.Message); }
foreach (var badAmount in new[]{"abc","-1","0.001"})
{
 try { Money.ParseOrZero(badAmount); Check("手续费输入校验 "+badAmount,false,"错误手续费被接受"); }
 catch(AppException e) { Check("手续费输入校验 "+badAmount,true,e.Message); }
}
try { Money.Parse("1.001"); Check("核销金额小数位校验",false,"三位小数被接受"); }
catch(AppException e) { Check("核销金额小数位校验",true,e.Message); }
try { SqlUtil.ValidateDate("2026/09/01"); Check("日期格式保证可正确比较",false,"非标准日期被接受"); }
catch(AppException e) { Check("日期格式保证可正确比较",true,e.Message); }

var autoPartner=MasterDataService.SavePartner(new Partner{Code="QA-AUTO",Name="自动核销客户",Kind="customer"});
var autoBiz1=Post(new Voucher{Kind=VoucherKind.Receivable,BizDate="2026-11-01",AmountCents=10_00,PartnerId=autoPartner,Summary="自动核销1"});
var autoBiz2=Post(new Voucher{Kind=VoucherKind.Receivable,BizDate="2026-11-02",AmountCents=15_00,PartnerId=autoPartner,Summary="自动核销2"});
var autoPay=Post(new Voucher{Kind=VoucherKind.Income,Nature=VoucherNature.CustomerReceipt,BizDate="2026-11-03",AmountCents=25_00,PartnerId=autoPartner,AccountId=bank,Summary="自动核销回款"});
var autoResult=new SettlementService().AutoAllocate(autoPay);
Check("自动核销按顺序完整分配",autoResult.Count==2 && autoResult[0].bizId==autoBiz1 && autoResult[1].bizId==autoBiz2 && autoResult.Sum(x=>x.amount)==25_00,$"分配{autoResult.Count}笔、合计{autoResult.Sum(x=>x.amount)/100m:0.00}元");

var backup1=AppSession.Db.BackupTo(null,"unique-test");
var backup2=AppSession.Db.BackupTo(null,"unique-test");
Check("连续备份不覆盖",backup1!=backup2 && File.Exists(backup1) && File.Exists(backup2),"已生成两个不同的备份文件");
var invalidDb=Path.Combine(dir,"not-cashier.db");
using(var conn=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+invalidDb)) { conn.Open(); new SqlCmd(conn,"CREATE TABLE unrelated(id INTEGER); PRAGMA user_version=2;").Exec(); }
var countBefore=vs.Query(new VoucherQuery{Kind=VoucherKind.Income,From="2026-09-01",To="2026-09-30"}).Count;
try { AppSession.Db.RestoreFrom(invalidDb); Check("拒绝非账套数据库恢复",false,"错误数据库被接受"); }
catch(Exception e) { var countAfter=vs.Query(new VoucherQuery{Kind=VoucherKind.Income,From="2026-09-01",To="2026-09-30"}).Count; Check("拒绝非账套数据库恢复",countAfter==countBefore,$"拦截原因：{e.Message}；原单据数量{countAfter}"); }
try { var safety=AppSession.Db.RestoreFrom(backup1); var after=vs.Query(new VoucherQuery{Kind=VoucherKind.Income,From="2026-09-01",To="2026-09-30"}).Count; Check("有效备份恢复并保留恢复前备份",File.Exists(safety) && after==countBefore,"恢复完成，恢复前安全备份存在"); }
catch(Exception e) { Check("有效备份恢复并保留恢复前备份",false,e.Message); }
Console.WriteLine($"RESULT checks={checks} failures={failures}");
return failures==0 ? 0 : 1;

