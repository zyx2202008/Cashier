using Cashier.Core;
using Cashier.Core.Services;
using Microsoft.Data.Sqlite;

if(args.Length!=1 || Directory.Exists(args[0])) throw new Exception("Fresh isolated directory required");
AppSession.Db=new Database(args[0]);AppSession.Db.EnsureCreated();
AppSession.Current=new AuthService().Login("admin","admin888");
var vs=new VoucherService();var ins=new InvoiceService();int checks=0,failed=0;
void Check(string n,bool ok){checks++;if(!ok)failed++;Console.WriteLine($"{(ok?"PASS":"FAIL")} | {n}");}
void Sql(string sql){using var c=AppSession.Db.Open();new SqlCmd(c,sql).Exec();}
long Scalar(string sql){using var c=AppSession.Db.Open();return new SqlCmd(c,sql).Long();}
long Linked(long id)=>ins.Get(id)!.LinkedCents;
long Past(long p,bool customer)=>ins.PartnerInvoiceSummary(p,customer?InvoiceKind.Out:InvoiceKind.In,"2024-01-31").invoiced;
void Amount(long id,long amount){var inv=ins.Get(id)!;inv.GrossCents=inv.NetCents=amount;ins.Save(inv);}
foreach(var customer in new[]{true,false}){
 var p=MasterDataService.SavePartner(new Partner{Name=customer?"客户":"供应商",Kind=customer?"customer":"supplier"});
 long Bill(long amount){var b=vs.Create(new Voucher{Kind=customer?VoucherKind.Receivable:VoucherKind.Payable,PartnerId=p,BizDate="2024-01-05",AmountCents=amount,Summary="自动追加验证"});vs.Audit(b);return b;}
 Bill(10000);
 var i=ins.Save(new Invoice{AutoLink=true,Kind=customer?InvoiceKind.Out:InvoiceKind.In,InvoiceType="普通",InvoiceNo="EXPAND-"+p,PartnerId=p,RegDate="2024-01-10",GrossCents=4000,NetCents=4000});
 Check("initial unique automatic match "+customer,Linked(i)==4000);
 Amount(i,7000);
 Check("increase extends existing automatic allocation "+customer,Linked(i)==7000&&ins.Links(i).Count==1);
 Check("increase preserves past cutoff "+customer,Past(p,customer)==4000);
 var h=Scalar("SELECT COUNT(*) FROM invoice_link_history");
 Sql("CREATE TRIGGER fail_extension BEFORE INSERT ON audit_log WHEN NEW.action='invoice-link' BEGIN SELECT RAISE(ABORT,'injected extension failure'); END");
 try{Amount(i,9000);Check("extension failure surfaced",false);}catch(Exception ex) when(ex is SqliteException or AppException){Check("extension failure surfaced",ex.Message.Contains("injected extension failure"));}
 Sql("DROP TRIGGER fail_extension");
 Check("extension failure rolls back amount, relation and history",Linked(i)==7000&&ins.Get(i)!.GrossCents==7000&&Scalar("SELECT COUNT(*) FROM invoice_link_history")==h);
 Amount(i,3000);
 Check("reduction preserves old month and current amount",Linked(i)==3000&&Past(p,customer)==4000);
 Amount(i,15000);
 Check("increase caps association at actual business",Linked(i)==10000&&ins.Get(i)!.GrossCents==15000);
 ins.Void(i,"回归测试");
 Check("void clears current association, retains historical association",ins.Links(i).Count==0&&Past(p,customer)==4000);
}
var pp=MasterDataService.SavePartner(new Partner{Name="歧义",Kind="supplier"});
long AmbBill(){var b=vs.Create(new Voucher{Kind=VoucherKind.Payable,PartnerId=pp,BizDate="2024-02-01",AmountCents=10000,Summary="多个候选"});vs.Audit(b);return b;}
var first=AmbBill();
var ai=ins.Save(new Invoice{AutoLink=true,Kind=InvoiceKind.In,InvoiceType="普通",InvoiceNo="AMB",PartnerId=pp,RegDate="2024-02-02",GrossCents=4000,NetCents=4000});
AmbBill();Amount(ai,7000);
Check("additional candidate prevents arbitrary expansion",Linked(ai)==4000);
var link=ins.Links(ai).Single();ins.Unlink(link.linkId,"人工选择");ins.Link(ai,first,3000);Amount(ai,9000);
Check("manual allocation never silently expanded",Linked(ai)==3000);
var redPartner=MasterDataService.SavePartner(new Partner{Name="红票追加",Kind="customer"});
var redBill=vs.Create(new Voucher{Kind=VoucherKind.Receivable,PartnerId=redPartner,BizDate="2024-01-05",AmountCents=10000,Summary="红票"});vs.Audit(redBill);
var blue=ins.Save(new Invoice{AutoLink=true,Kind=InvoiceKind.Out,InvoiceType="普通",InvoiceNo="RED-BASE",PartnerId=redPartner,RegDate="2024-01-10",GrossCents=10000,NetCents=10000});
var red=ins.Save(new Invoice{AutoLink=true,Kind=InvoiceKind.Out,InvoiceType="普通",InvoiceNo="RED-EXTEND",PartnerId=redPartner,RegDate="2024-01-15",GrossCents=-2000,NetCents=-2000,OriginalInvoiceId=blue});
Amount(red,-4000);
Check("red increase extends signed association",Linked(red)==-4000&&ins.PartnerInvoiceSummary(redPartner,InvoiceKind.Out).invoiced==6000);
Check("red increase preserves earlier net invoice amount",Past(redPartner,true)==8000);
try{Amount(blue,9000);Check("blue with valid red protected",false);}catch(AppException){Check("blue with valid red protected",ins.Get(blue)!.GrossCents==10000);}
Amount(red,-1000);
Check("red decrease recalculates current net",Linked(red)==-1000&&ins.PartnerInvoiceSummary(redPartner,InvoiceKind.Out).invoiced==9000);
PeriodService.Close(AppSession.PeriodOf(AppSession.Today()));
try{Amount(red,-1500);Check("closed adjustment period protected",false);}catch(AppException){Check("closed adjustment period protected",Linked(red)==-1000&&ins.Get(red)!.GrossCents==-1000);}
PeriodService.Reopen(AppSession.PeriodOf(AppSession.Today()),"测试结束");
Check("healthy ledger has no integrity warnings",IntegrityCheckService.FindIssues().Count==0);
Sql("UPDATE invoice_links SET amount_cents=20000 WHERE invoice_id="+ai);
var issues=IntegrityCheckService.FindIssues();
Check("integrity detects over-invoiced invoice",issues.Any(s=>s.StartsWith("发票关联金额")));
Check("integrity detects over-invoiced business",issues.Any(s=>s.StartsWith("业务开票关联")));
Check("integrity check is read-only",Scalar("SELECT amount_cents FROM invoice_links WHERE invoice_id="+ai)==20000);
Console.WriteLine($"RESULT checks={checks} failures={failed}");return failed==0?0:1;
