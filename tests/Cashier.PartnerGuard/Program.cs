using Cashier.Core;
using Cashier.Core.Services;
AppSession.Db=new Database(args[0]);AppSession.Db.EnsureCreated();AppSession.Current=new AuthService().Login("admin","admin888");
int checks=0,fail=0;void Check(string n,bool ok){checks++;if(!ok){fail++;Console.WriteLine("FAIL | "+n);}}void Reject(string n,Action a){try{a();Check(n,false);}catch(AppException){Check(n,true);}}
var p=MasterDataService.SavePartner(new Partner{Code="BOTH",Name="往来双方",Kind="both"});var vs=new VoucherService();
foreach(var kind in new[]{VoucherKind.Receivable,VoucherKind.Payable}){var id=vs.Create(new Voucher{Kind=kind,AmountCents=100,BizDate="2024-01-01",PartnerId=p,Summary="role guard"});vs.Audit(id);}
foreach(var kind in new[]{"customer","supplier"}){var partner=MasterDataService.GetPartner(p)!;partner.Kind=kind;Reject("used dual partner cannot lose role "+kind,()=>MasterDataService.SavePartner(partner));}
// If baseline accepted a change, restore both before checking unchanged totals.
var restore=MasterDataService.GetPartner(p)!;restore.Kind="both";MasterDataService.SavePartner(restore);
foreach(var kind in new[]{"customer","supplier"})Reject("opening balance must match role "+kind,()=>MasterDataService.SavePartner(new Partner{Code="BAD-"+kind,Name="invalid opening",Kind=kind,OpeningReceivableCents=kind=="supplier"?100:0,OpeningPayableCents=kind=="customer"?100:0}));
var good=MasterDataService.SavePartner(new Partner{Code="GOOD",Name="可扩展客户",Kind="customer",OpeningReceivableCents=-100});var g=MasterDataService.GetPartner(good)!;g.Kind="both";MasterDataService.SavePartner(g);Check("role widening preserves negative opening",MasterDataService.GetPartner(good)!.OpeningReceivableCents==-100);
var cs=new ReportService().Build("customer_summary",new ReportFilter{From="2024-01-01",To="2024-01-31",PartnerId=p});var sp=new ReportService().Build("supplier_summary",new ReportFilter{From="2024-01-01",To="2024-01-31",PartnerId=p});Check("customer reporting preserved",Convert.ToInt64(cs.Rows.Single().Cells[5])==100);Check("supplier reporting preserved",Convert.ToInt64(sp.Rows.Single().Cells[5])==100);
Console.WriteLine($"RESULT checks={checks} failures={fail}");return fail==0?0:1;
