using Cashier.Core;
using Cashier.Core.Services;
using System.Numerics;

if(args.Length!=1 || Directory.Exists(args[0])) throw new Exception("Fresh data directory required");
var root=Path.GetFullPath(args[0]);
int checks=0,failures=0; var details=new List<string>();
void Check(string label,bool ok,string evidence="") { checks++; if(!ok){failures++;details.Add(label+" | "+evidence);Console.WriteLine("FAIL | "+label+" | "+evidence);} }
void Eq(string label,long actual,long expected)=>Check(label,actual==expected,$"expected={expected} actual={actual} cents");
void Reject(string label,Action action){try{action();Check(label,false,"accepted invalid operation");}catch(AppException){Check(label,true);}}
void Fresh(string name){AppSession.Db=new Database(Path.Combine(root,name));AppSession.Db.EnsureCreated();AppSession.Current=new AuthService().Login("admin","admin888");}
var vs=new VoucherService();var rs=new ReportService();
long Post(Voucher v){var id=vs.Create(v);vs.Audit(id);return id;}
long Acc(string code,long opening=0)=>MasterDataService.SaveAccount(new Account{Code=code,Name=code,Kind="bank",OpeningCents=opening});
long Partner(string code,string kind="both")=>MasterDataService.SavePartner(new Partner{Code=code,Name=code,Kind=kind});
long Cell(ReportResult r,int col)=>Convert.ToInt64(r.TotalRow![col]);
Console.WriteLine("SEED=20261030; isolated synthetic databases; all amounts in cents");
Fresh("volume");var account=Acc("VOL");var partner=Partner("VOL");
// A new single-day number must remain unique after the suffix exceeds three digits.
for(int i=0;i<1000;i++)vs.Create(new Voucher{Kind=VoucherKind.Income,Nature=VoucherNature.OtherIn,BizDate="2024-02-29",AmountCents=1,AccountId=account,Summary="sequence"});
try{var id=vs.Create(new Voucher{Kind=VoucherKind.Income,Nature=VoucherNature.OtherIn,BizDate="2024-02-29",AmountCents=1,AccountId=account,Summary="1001"});Check("daily sequence after 1000",vs.Get(id)!.DocNo.EndsWith("1001"));}catch(Exception e){Check("daily sequence after 1000",false,e.Message);}
// Build a large fixture in one transaction; each row satisfies normal voucher constraints.
using(var c=AppSession.Db.Open()){using var tx=c.BeginTransaction();for(int i=0;i<5005;i++)new SqlCmd(c,@"INSERT INTO vouchers(doc_no,kind,nature,biz_date,period,amount_cents,partner_id,summary,status,created_at,created_by,count_as_revenue,count_as_cost,invoiceable)
VALUES($no,'receivable','None',$d,$per,1,$p,'large FIFO fixture','audited','2020-01-01','admin',0,0,1)",tx).P("$no",$"FIFO-{i:000000}").P("$d",new DateTime(2000,1,1).AddDays(i).ToString("yyyy-MM-dd")).P("$per",new DateTime(2000,1,1).AddDays(i).ToString("yyyyMM")).P("$p",partner).Exec();tx.Commit();}
var payment=Post(new Voucher{Kind=VoucherKind.Income,Nature=VoucherNature.CustomerReceipt,BizDate="2024-03-01",AmountCents=5005,AccountId=account,PartnerId=partner,Summary="FIFO payment"});
var settle=new SettlementService();var allocations=settle.AutoAllocate(payment);
Eq("FIFO covers more than 5000 open bills",allocations.Sum(x=>x.amount),5005);
Check("FIFO starts with oldest bill",allocations.FirstOrDefault().docNo=="FIFO-000000",allocations.FirstOrDefault().docNo??"empty");
Eq("large allocation payment unallocated",settle.UnallocatedOf(payment),0);
Fresh("validation");account=Acc("ACTIVE",10000);var customer=Partner("CUSTOMER","customer");var supplier=Partner("SUPPLIER","supplier");
var a=MasterDataService.ListAccounts().Single(x=>x.Id==account);a.IsActive=false;MasterDataService.SaveAccount(a);
Eq("inactive account retains total funds",VoucherService.TotalFunds(),10000);
foreach(var kind in new[]{VoucherKind.Receivable,VoucherKind.Payable})Reject("wrong partner kind "+kind,()=>Post(new Voucher{Kind=kind,BizDate="2024-02-01",AmountCents=100,PartnerId=kind==VoucherKind.Receivable?supplier:customer,Summary="invalid kind"}));
foreach(var nature in new[]{VoucherNature.CustomerRefund,VoucherNature.SupplierRefund})Reject("refund requires partner "+nature,()=>Post(new Voucher{Kind=nature==VoucherNature.CustomerRefund?VoucherKind.Expense:VoucherKind.Income,Nature=nature,BizDate="2024-02-01",AmountCents=100,AccountId=account,Summary="invalid refund"}));
foreach(var kind in new[]{InvoiceKind.Out,InvoiceKind.In})Reject("wrong invoice partner "+kind,()=>new InvoiceService().Save(new Invoice{Kind=kind,InvoiceType="普通",InvoiceNo="BAD-"+kind,PartnerId=kind==InvoiceKind.Out?supplier:customer,RegDate="2024-02-01",GrossCents=100,NetCents=100,TaxCents=0}));
// A valid dual-role partner must still work in both directions.
var dual=Partner("DUAL");
foreach(var kind in new[]{VoucherKind.Receivable,VoucherKind.Payable}){var id=Post(new Voucher{Kind=kind,BizDate="2024-02-01",AmountCents=100,PartnerId=dual,Summary="valid dual partner"});Check("dual partner "+kind,vs.Get(id)!.Status==VoucherStatus.Audited);}
Fresh("random-ledger");var rng=new Random(20261030);
var accounts=Enumerable.Range(0,4).Select(i=>Acc("R"+i,10000*(i-1))).ToArray();
var partners=Enumerable.Range(0,4).Select(i=>Partner("P"+i)).ToArray();
var projects=Enumerable.Range(0,3).Select(i=>MasterDataService.SaveProject(new Project{Code="PR"+i,Name="PR"+i})).ToArray();
var entries=new List<Entry>();var ids=new List<long>();
VoucherNature[] natures={VoucherNature.SaleCash,VoucherNature.CustomerReceipt,VoucherNature.CustomerPrepay,VoucherNature.SupplierRefund,VoucherNature.OtherIn,VoucherNature.PurchaseCash,VoucherNature.SupplierPayment,VoucherNature.SupplierPrepay,VoucherNature.CustomerRefund,VoucherNature.OtherOut};
for(int i=0;i<640;i++){
 var t=i%16;var kind=t<5?VoucherKind.Income:t<10?VoucherKind.Expense:t<12?VoucherKind.Receivable:t<14?VoucherKind.Payable:VoucherKind.Transfer;
 var nature=t<10?natures[t]:VoucherNature.None;var date=new DateTime(2023,11,1).AddDays(rng.Next(0,520)).ToString("yyyy-MM-dd");
 long amount=i%97==0?Money.MaxCents:rng.Next(1,10000000);int ai=rng.Next(4);long? acc=kind is VoucherKind.Receivable or VoucherKind.Payable?null:accounts[ai];
 long? to=kind==VoucherKind.Transfer?accounts[(ai+1+rng.Next(3))%4]:null;long fee=kind==VoucherKind.Transfer?rng.Next(0,1000):0;var p=partners[rng.Next(4)];var pr=projects[rng.Next(3)];bool rev=kind==VoucherKind.Receivable && i%3!=0,cost=kind==VoucherKind.Payable && i%3!=0;
 var v=new Voucher{Kind=kind,Nature=nature,BizDate=date,AmountCents=amount,AccountId=acc,ToAccountId=to,FeeCents=fee,PartnerId=p,ProjectId=pr,Summary="deterministic random ledger",CountAsRevenue=rev,CountAsCost=cost};
 var id=Post(v);ids.Add(id);var entry=new Entry(id,date,kind,nature,amount,acc,to,p,pr,rev,cost,true);entries.Add(entry);
 if(fee>0)entries.Add(new Entry(-id,date,VoucherKind.Expense,VoucherNature.OtherOut,fee,acc,null,null,null,false,true,true));
 if(i%23==0){vs.Unaudit(id,"state transition test");entry.Active=false;foreach(var e in entries.Where(e=>e.Id==-id))e.Active=false;}
 else if(i%31==0){vs.Void(id,"void test");entry.Active=false;foreach(var e in entries.Where(e=>e.Id==-id))e.Active=false;}
}
long Flow(Entry e,long acc)=>e.Kind==VoucherKind.Income && e.Account==acc?e.Amount:e.Kind==VoucherKind.Expense && e.Account==acc?-e.Amount:e.Kind==VoucherKind.Transfer?(e.To==acc?e.Amount:0)-(e.Account==acc?e.Amount:0):0;
bool Revenue(Entry e)=>e.Kind==VoucherKind.Income && e.Nature is VoucherNature.SaleCash or VoucherNature.OtherIn || e.Kind==VoucherKind.Receivable&&e.Revenue;
bool Cost(Entry e)=>e.Kind==VoucherKind.Expense && e.Nature is VoucherNature.PurchaseCash or VoucherNature.OtherOut || e.Kind==VoucherKind.Payable&&e.Cost;
for(int range=0;range<36;range++){
 var from=new DateTime(2023,11,1).AddDays(rng.Next(0,450)).ToString("yyyy-MM-dd");var end=DateTime.Parse(from).AddDays(rng.Next(0,120)).ToString("yyyy-MM-dd");var f=new ReportFilter{From=from,To=end};
 var active=entries.Where(e=>e.Active).ToList();var period=active.Where(e=>string.CompareOrdinal(e.Date,from)>=0&&string.CompareOrdinal(e.Date,end)<=0).ToList();
 foreach(var acc in accounts){var af=new ReportFilter{From=from,To=end,AccountId=acc};var report=rs.Build("account_report",af);var row=report.Rows.Single().Cells;long opening=10000*(Array.IndexOf(accounts,acc)-1)+active.Where(e=>string.CompareOrdinal(e.Date,from)<0).Sum(e=>Flow(e,acc));long inflow=period.Sum(e=>e.Kind==VoucherKind.Income&&e.Account==acc||e.Kind==VoucherKind.Transfer&&e.To==acc?e.Amount:0);long outflow=period.Sum(e=>e.Kind==VoucherKind.Expense&&e.Account==acc||e.Kind==VoucherKind.Transfer&&e.Account==acc?e.Amount:0);
  Eq("random opening",Convert.ToInt64(row[1]),opening);Eq("random inflow",Convert.ToInt64(row[2]),inflow);Eq("random outflow",Convert.ToInt64(row[3]),outflow);Eq("random ending",Convert.ToInt64(row[4]),opening+inflow-outflow);Eq("balance API",MasterDataService.AccountBalance(acc,end),opening+inflow-outflow);
  var detail=rs.Build("account_detail",af);Eq("account detail inflow",Cell(detail,7),inflow);Eq("account detail outflow",Cell(detail,8),outflow);
  foreach(var id in new[]{"account_month","account_year"}){var pr=rs.Build(id,af);Eq(id+" inflow",pr.Rows.Sum(r=>Convert.ToInt64(r.Cells[3])),inflow);Eq(id+" outflow",pr.Rows.Sum(r=>Convert.ToInt64(r.Cells[4])),outflow);if(pr.Rows.Count>0)Eq(id+" ending",Convert.ToInt64(pr.Rows.Last().Cells[5]),opening+inflow-outflow);}
 }
 foreach(var p in partners){var pf=new ReportFilter{From=from,To=end,PartnerId=p};var pe=active.Where(e=>e.Partner==p&&string.CompareOrdinal(e.Date,end)<=0).ToList();long cr=pe.Sum(e=>e.Kind==VoucherKind.Receivable||e.Nature==VoucherNature.CustomerRefund?e.Amount:e.Nature is VoucherNature.CustomerReceipt or VoucherNature.CustomerPrepay?-e.Amount:0);long sp=pe.Sum(e=>e.Kind==VoucherKind.Payable||e.Nature==VoucherNature.SupplierRefund?e.Amount:e.Nature is VoucherNature.SupplierPayment or VoucherNature.SupplierPrepay?-e.Amount:0);Eq("random customer net",Convert.ToInt64(rs.Build("customer_summary",pf).Rows.Single().Cells[5]),cr);Eq("random supplier net",Convert.ToInt64(rs.Build("supplier_summary",pf).Rows.Single().Cells[5]),sp);}
 Eq("income report",Cell(rs.Build("income_detail",f),8),period.Where(Revenue).Sum(e=>e.Amount));Eq("expense report",Cell(rs.Build("expense_detail",f),8),period.Where(Cost).Sum(e=>e.Amount));
 foreach(var id in new[]{"cash_month","cash_year"}){var report=rs.Build(id,f);Eq(id+" inflow",Cell(report,1),period.Where(e=>e.Kind==VoucherKind.Income).Sum(e=>e.Amount));Eq(id+" outflow",Cell(report,2),period.Where(e=>e.Kind==VoucherKind.Expense).Sum(e=>e.Amount));}
 Eq("subject totals",Cell(rs.Build("subject_summary",f),3),period.Where(e=>Revenue(e)||Cost(e)).Sum(e=>e.Amount));
 foreach(var prj in projects){var pf=new ReportFilter{From=from,To=end,ProjectId=prj};long rev=period.Where(e=>e.Project==prj&&Revenue(e)).Sum(e=>e.Amount),cost=period.Where(e=>e.Project==prj&&Cost(e)).Sum(e=>e.Amount);foreach(var id in new[]{"project_month","project_year"}){var r=rs.Build(id,pf);Eq("project revenue",Cell(r,2),rev);Eq("project cost",Cell(r,3),cost);Eq("project profit",Cell(r,4),rev-cost);Check("profit margin weighted",rev==0?r.TotalRow![5]==null:Convert.ToDecimal(r.TotalRow![5])==(decimal)(rev-cost)/rev);}}
}
// Independent BigInteger oracle, including signed extremes and half-cent rounding.
long Round(BigInteger n,BigInteger d){var sign=n.Sign;return (long)((BigInteger.Abs(n)+d/2)/d)*sign;}
for(int i=0;i<3000;i++){long gross=i<6?new[]{1L,-1,Money.MaxCents,-Money.MaxCents,0,113}[i]:rng.NextInt64(-Money.MaxCents,Money.MaxCents);int bp=rng.Next(0,10001);var split=Money.SplitTax(gross,bp);Eq("tax oracle net",split.net,Round((BigInteger)gross*10000,10000+bp));Eq("tax conservation",split.net+split.tax,gross);var back=Money.GrossFromNet(gross,bp);Eq("gross from net tax oracle",back.tax,Round((BigInteger)gross*bp,10000));Eq("gross from net conservation",back.gross,gross+back.tax);}
using(var c=AppSession.Db.Open())Eq("foreign key integrity",new SqlCmd(c,"SELECT COUNT(*) FROM pragma_foreign_key_check").Long(),0);
Console.WriteLine($"RESULT checks={checks} failures={failures} entries={entries.Count}");
File.WriteAllLines(Path.Combine(root,"failures.txt"),details);
return failures==0?0:1;
class Entry(long id,string date,VoucherKind kind,VoucherNature nature,long amount,long? account,long? to,long? partner,long? project,bool revenue,bool cost,bool active){public long Id=id;public string Date=date;public VoucherKind Kind=kind;public VoucherNature Nature=nature;public long Amount=amount;public long? Account=account,To=to,Partner=partner,Project=project;public bool Revenue=revenue,Cost=cost,Active=active;}
