using Cashier.Core;
using Cashier.Core.Services;
using ClosedXML.Excel;
if (args.Length != 1 || Directory.Exists(args[0])) throw new Exception("Fresh isolated directory required");
AppSession.Db = new Database(args[0]); AppSession.Db.EnsureCreated(); AppSession.Current = new AuthService().Login("admin", "admin888");
var vs = new VoucherService(); var ins = new InvoiceService(); var date = AppSession.Today();
int checks=0,failures=0;
void Check(string text,bool ok){checks++;if(!ok)failures++;Console.WriteLine($"{(ok?"PASS":"FAIL")} | {text}");}
long Partner(string kind)=>MasterDataService.SavePartner(new Partner{Name=Guid.NewGuid().ToString(),Kind=kind});
long Bill(long partner,bool customer,long amount=10000){var id=vs.Create(new Voucher{Kind=customer?VoucherKind.Receivable:VoucherKind.Payable,PartnerId=partner,BizDate=date,AmountCents=amount,Summary="业务"});vs.Audit(id);return id;}
long Inv(long partner,bool customer,bool automatic=true,long amount=10000)=>ins.Save(new Invoice{AutoLink=automatic,Kind=customer?InvoiceKind.Out:InvoiceKind.In,InvoiceType="普通",InvoiceNo=Guid.NewGuid().ToString("N")[..12],PartnerId=partner,RegDate=date,GrossCents=amount,NetCents=amount,TaxCents=0});
foreach(var customer in new[]{true,false}){
 var p=Partner(customer?"customer":"supplier");var b=Bill(p,customer);var i=Inv(p,customer);
 Check("one invoice and one bill link automatically",ins.Links(i).Single().voucherId==b);
 var auto=ins.Links(i).Single();ins.Unlink(auto.linkId,"用户调整");ins.Link(i,b,5000);
 Check("user can unlink automatic match and choose amount",ins.Links(i).Single().amount==5000);
 using(var c=AppSession.Db.Open())Check("automatic unlink retains history",new SqlCmd(c,"SELECT COUNT(*) FROM invoice_link_history WHERE invoice_id=$i").P("$i",i).Long()==1);
 var changed=ins.Get(i)!;changed.GrossCents=4000;changed.NetCents=4000;
 try{ins.Save(changed);Check("manual association protected against automatic reduction",false);}catch(AppException){Check("manual association protected against automatic reduction",ins.Get(i)!.GrossCents==10000&&ins.Links(i).Single().amount==5000);}
 var p2=Partner(customer?"customer":"supplier");Bill(p2,customer);Bill(p2,customer);var i2=Inv(p2,customer);
 Check("two possible bills remain unlinked",ins.Links(i2).Count==0);
 var p3=Partner(customer?"customer":"supplier");var old=Inv(p3,customer,false);var b3=Bill(p3,customer);
 Check("legacy manual invoice is not automatically linked",ins.Links(old).Count==0);ins.Link(old,b3,10000);
 Check("legacy manual linking remains usable",ins.Links(old).Single().amount==10000);
 var p4=Partner(customer?"customer":"supplier");var x=Inv(p4,customer);var y=Inv(p4,customer);Bill(p4,customer);
 Check("two possible invoices are not automatically linked to later bill",ins.Links(x).Count==0&&ins.Links(y).Count==0);
 var p5=Partner(customer?"customer":"supplier");var pre=Inv(p5,customer);var after=Bill(p5,customer);
 Check("unique invoice entered before business links on audit",ins.Links(pre).Single().voucherId==after);
}
// Insert a whole batch before allocating, so no first row can appear unique merely
// because the second invoice has not yet been written.
var batchPartner=Partner("supplier");
var firstBill=Bill(batchPartner,false);
var batchFile=Path.Combine(args[0],"ambiguous-batch.xlsx");
ExcelService.CreateInvoiceTemplate(InvoiceKind.In,batchFile);
using(var book=new XLWorkbook(batchFile)){
 var sheet=book.Worksheet(1);var code=MasterDataService.GetPartner(batchPartner)!.Code;
 for(var row=2;row<=3;row++){
  sheet.Cell(row,1).Value=date;sheet.Cell(row,2).Value="普通";sheet.Cell(row,3).Value="BATCH-"+row;
  sheet.Cell(row,5).Value=code;sheet.Cell(row,6).Value=100m;sheet.Cell(row,7).Value=0m;
  sheet.Cell(row,8).Value=100m;sheet.Cell(row,9).Value=0m;
 }
 book.Save();
}
var batch=ExcelService.PreviewInvoiceImport(InvoiceKind.In,batchFile);
Check("multiple invoice batch passes validation",batch.OkCount==2&&batch.ErrorCount==0);
Check("multiple invoice batch commits atomically",ExcelService.CommitInvoiceImport(batch)==2);
var batched=ins.List(InvoiceKind.In,"BATCH-",batchPartner,null,null,false).OrderBy(i=>i.InvoiceNo).ToArray();
Check("batch ambiguity does not allocate the first-written invoice",batched.Length==2&&batched.All(i=>ins.Links(i.Id).Count==0)&&batch.UnlinkedInvoiceCount==2);
ins.Link(batched[0].Id,firstBill,10000);
var laterBill=Bill(batchPartner,false);
Check("later audit can automatically link the now-unique remaining batch invoice",ins.Links(batched[1].Id).Single().voucherId==laterBill);
Check("manual first invoice allocation remains intact",ins.Links(batched[0].Id).Single().voucherId==firstBill);
var rollbackPartner=Partner("supplier");Bill(rollbackPartner,false);
var rollbackFile=Path.Combine(args[0],"failing-batch.xlsx");
ExcelService.CreateInvoiceTemplate(InvoiceKind.In,rollbackFile);
using(var book=new XLWorkbook(rollbackFile)){
 var sheet=book.Worksheet(1);var code=MasterDataService.GetPartner(rollbackPartner)!.Code;
 for(var row=2;row<=3;row++){
  sheet.Cell(row,1).Value=date;sheet.Cell(row,2).Value="普通";sheet.Cell(row,3).Value="BATCH-FAIL-"+(row-1);
  sheet.Cell(row,5).Value=code;sheet.Cell(row,6).Value=100m;sheet.Cell(row,7).Value=0m;
  sheet.Cell(row,8).Value=100m;sheet.Cell(row,9).Value=0m;
 }
 book.Save();
}
var rollbackPreview=ExcelService.PreviewInvoiceImport(InvoiceKind.In,rollbackFile);
long metaBefore, linksBefore;
using(var c=AppSession.Db.Open()){
 metaBefore=new SqlCmd(c,"SELECT COUNT(*) FROM meta WHERE k LIKE 'invoice.autoLink.%'").Long();
 linksBefore=new SqlCmd(c,"SELECT COUNT(*) FROM invoice_links").Long();
 new SqlCmd(c,"CREATE TRIGGER fail_second_invoice BEFORE INSERT ON invoices WHEN NEW.invoice_no='BATCH-FAIL-2' BEGIN SELECT RAISE(ABORT,'injected failure'); END").Exec();
}
try{ExcelService.CommitInvoiceImport(rollbackPreview);Check("injected second-row failure is reported",false);}catch(AppException){Check("injected second-row failure is reported",true);}
using(var c=AppSession.Db.Open()){
 Check("batch failure rolls back first invoice, links and eligibility marker",new SqlCmd(c,"SELECT COUNT(*) FROM invoices WHERE invoice_no LIKE 'BATCH-FAIL-%'").Long()==0&&new SqlCmd(c,"SELECT COUNT(*) FROM invoice_links").Long()==linksBefore&&new SqlCmd(c,"SELECT COUNT(*) FROM meta WHERE k LIKE 'invoice.autoLink.%'").Long()==metaBefore);
 new SqlCmd(c,"DROP TRIGGER fail_second_invoice").Exec();
}
AppSession.Db=new Database(args[0]);AppSession.Db.EnsureCreated();
using(var c=AppSession.Db.Open())Check("reopen retains linkage and integrity",new SqlCmd(c,"SELECT COUNT(*) FROM invoice_links").Long()>0&&new SqlCmd(c,"SELECT COUNT(*) FROM pragma_foreign_key_check").Long()==0);
Console.WriteLine($"RESULT checks={checks} failures={failures}");return failures==0?0:1;
