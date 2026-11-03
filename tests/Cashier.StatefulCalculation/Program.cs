using Cashier.Core;
using Cashier.Core.Services;
if(args.Length!=1||Directory.Exists(args[0]))throw new Exception("Fresh data directory required");
AppSession.Db=new Database(args[0]);AppSession.Db.EnsureCreated();AppSession.Current=new AuthService().Login("admin","admin888");
int checks=0,fail=0;void Eq(string n,long a,long e){checks++;if(a!=e){fail++;Console.WriteLine($"FAIL | {n} expected={e} actual={a}");}}void Assert(string n,bool ok){checks++;if(!ok){fail++;Console.WriteLine("FAIL | "+n);}}
void Reject(string n,Action a){try{a();Assert(n,false);}catch(AppException){Assert(n,true);}}
var vs=new VoucherService();var ss=new SettlementService();var inv=new InvoiceService();var rs=new ReportService();
long Post(Voucher v){var id=vs.Create(v);vs.Audit(id);return id;}
var bank=MasterDataService.ListAccounts(false).First(x=>x.Kind=="bank").Id;var rand=new Random(71123);
var today=DateTime.Today.ToString("yyyy-MM-dd");
var history=new List<Allocation>();var links=new List<Link>();
for(int partnerIndex=0;partnerIndex<4;partnerIndex++){
 bool cust=partnerIndex%2==0;var p=MasterDataService.SavePartner(new Partner{Code="S"+partnerIndex,Name="S"+partnerIndex,Kind=cust?"customer":"supplier"});
 var bills=new List<(long id,long amount,string date)>();var payments=new List<(long id,long amount,string date)>();
 for(int i=0;i<12;i++){string d=new DateTime(2024,1,1).AddDays(i*27).ToString("yyyy-MM-dd");long amount=rand.Next(15000,40000);long id=Post(new Voucher{Kind=cust?VoucherKind.Receivable:VoucherKind.Payable,BizDate=d,AmountCents=amount,PartnerId=p,Summary="bill "+i,Invoiceable=true});bills.Add((id,amount,d));}
 for(int i=0;i<10;i++){string d=new DateTime(2024,1,10).AddDays(i*35).ToString("yyyy-MM-dd");long amount=rand.Next(8000,22000);long id=Post(new Voucher{Kind=cust?VoucherKind.Income:VoucherKind.Expense,Nature=cust?(i%2==0?VoucherNature.CustomerReceipt:VoucherNature.CustomerPrepay):(i%2==0?VoucherNature.SupplierPayment:VoucherNature.SupplierPrepay),BizDate=d,AmountCents=amount,PartnerId=p,AccountId=bank,Summary="pay "+i});payments.Add((id,amount,d));}
 long netBefore=cust?MasterDataService.GetPartner(p)!.NetReceivableCents:MasterDataService.GetPartner(p)!.NetPayableCents;
 foreach(var pay in payments){foreach(var bill in bills.OrderBy(_=>rand.Next())){long payLeft=pay.amount-history.Where(a=>a.Pay==pay.id&&a.End==null).Sum(a=>a.Amount),bizLeft=bill.amount-history.Where(a=>a.Biz==bill.id&&a.End==null).Sum(a=>a.Amount);long amount=Math.Min(payLeft,bizLeft);if(amount==0)continue;amount=Math.Max(1,amount/2);long sid=ss.Allocate(pay.id,bill.id,amount);history.Add(new Allocation(sid,pay.id,bill.id,amount,string.CompareOrdinal(pay.date,bill.date)>0?pay.date:bill.date));if(history.Count%3==0)break;}}
 foreach(var pay in payments)Eq("payment balance oracle",ss.UnallocatedOf(pay.id),pay.amount-history.Where(a=>a.Pay==pay.id&&a.End==null).Sum(a=>a.Amount));
 foreach(var bill in bills)Eq("bill balance oracle",ss.UnsettledOf(bill.id),bill.amount-history.Where(a=>a.Biz==bill.id&&a.End==null).Sum(a=>a.Amount));
 foreach(var a in history.Where(a=>payments.Any(x=>x.id==a.Pay)&&a.Id%4==0).ToList()){ss.Unallocate(a.Id,"historical state test");a.End=today;var re=ss.Allocate(a.Pay,a.Biz,a.Amount);history.Add(new Allocation(re,a.Pay,a.Biz,a.Amount,today));}
 foreach(var cutoff in new[]{"2024-01-31","2024-03-31","2024-06-30","2024-12-31",today}){
 var report=rs.Build(cust?"receivable_detail":"payable_detail",new ReportFilter{From="2024-01-01",To=cutoff,PartnerId=p});foreach(var bill in bills.Where(b=>string.CompareOrdinal(b.date,cutoff)<=0)){var no=vs.Get(bill.id)!.DocNo;var r=report.Rows.Single(r=>r.Cells[1]?.ToString()==no);long expected=history.Where(a=>a.Biz==bill.id&&string.CompareOrdinal(a.Start,cutoff)<=0&&(a.End==null||string.CompareOrdinal(a.End,cutoff)>0)).Sum(a=>a.Amount);Eq("historical settlement oracle",Convert.ToInt64(r.Cells[6]),expected);Eq("historical unsettled oracle",Convert.ToInt64(r.Cells[7]),bill.amount-expected);}}
 Eq("settlement does not change partner net",cust?MasterDataService.GetPartner(p)!.NetReceivableCents:MasterDataService.GetPartner(p)!.NetPayableCents,netBefore);
 var pay0=payments[0];var bill0=bills[0];long previousPay=ss.UnallocatedOf(pay0.id),previousBill=ss.UnsettledOf(bill0.id);Reject("over allocation rejected",()=>ss.Allocate(pay0.id,bill0.id,Money.MaxCents));Eq("failed allocation pay unchanged",ss.UnallocatedOf(pay0.id),previousPay);Eq("failed allocation bill unchanged",ss.UnsettledOf(bill0.id),previousBill);
 long fundsBefore=VoucherService.TotalFunds();
 for(int i=0;i<12;i++){
 var bill=bills[i];long gross=Math.Min(12000,bill.amount);var split=Money.SplitTax(gross,1300);string date=new DateTime(2024,2,1).AddDays(i*26).ToString("yyyy-MM-dd");
 var blue=inv.Save(new Invoice{Kind=cust?InvoiceKind.Out:InvoiceKind.In,InvoiceType="普通",InvoiceNo=$"B-{p}-{i}",PartnerId=p,RegDate=date,GrossCents=gross,NetCents=split.net,TaxCents=split.tax,RateBp=1300});
 inv.Link(blue,bill.id,gross);var bl=inv.Links(blue).Single();links.Add(new Link(blue,bill.id,gross,string.CompareOrdinal(date,bill.date)>0?date:bill.date));
 var redSplit=Money.SplitTax(3000,1300);var red=inv.Save(new Invoice{Kind=cust?InvoiceKind.Out:InvoiceKind.In,InvoiceType="普通",InvoiceNo=$"R-{p}-{i}",PartnerId=p,RegDate="2024-12-31",GrossCents=-3000,NetCents=-redSplit.net,TaxCents=-redSplit.tax,RateBp=1300,OriginalInvoiceId=blue});
 inv.Link(red,bill.id,3000);links.Add(new Link(red,bill.id,-3000,"2024-12-31"));
 if(i%3==0){inv.Unlink(inv.Links(red).Single().linkId,"red reversal");links.Last().End=today;inv.Unlink(bl.linkId,"blue reversal");links.Single(x=>x.Invoice==blue).End=today;inv.Link(blue,bill.id,gross);links.Add(new Link(blue,bill.id,gross,today));}
 Reject("red total over original rejected",()=>inv.Save(new Invoice{Kind=cust?InvoiceKind.Out:InvoiceKind.In,InvoiceType="普通",InvoiceNo=$"OVER-{p}-{i}",PartnerId=p,RegDate="2025-01-01",GrossCents=-gross,NetCents=-split.net,TaxCents=-split.tax,RateBp=1300,OriginalInvoiceId=blue}));
 }
 foreach(var cutoff in new[]{"2024-01-31","2024-03-31","2024-06-30","2024-12-31",today}){
 var f=new ReportFilter{From="2024-01-01",To=cutoff,PartnerId=p};var detail=rs.Build(cust?"invoice_out_detail":"invoice_in_detail",f);foreach(var row in detail.Rows){var invoice=inv.List(cust?InvoiceKind.Out:InvoiceKind.In,row.Cells[1]?.ToString(),p,null,null).Single(x=>x.InvoiceNo==row.Cells[1]?.ToString());long expected=links.Where(l=>l.Invoice==invoice.Id&&string.CompareOrdinal(l.Start,cutoff)<=0&&(l.End==null||string.CompareOrdinal(l.End,cutoff)>0)).Sum(l=>l.Amount);Eq("invoice cutoff links oracle",Convert.ToInt64(row.Cells[9]),expected);}
 long expectedAll=links.Where(l=>bills.Any(b=>b.id==l.Biz)&&string.CompareOrdinal(l.Start,cutoff)<=0&&(l.End==null||string.CompareOrdinal(l.End,cutoff)>0)).Sum(l=>l.Amount);var summary=inv.PartnerInvoiceSummary(p,cust?InvoiceKind.Out:InvoiceKind.In,cutoff);Eq("invoice summary links oracle",summary.invoiced,expectedAll);
 }
 Eq("invoice registration does not move funds",VoucherService.TotalFunds(),fundsBefore);
}
// Restore the entire linked ledger and compare every report, not only row counts.
var filter=new ReportFilter{From="2024-01-01",To=today};
var reportIds=ReportService.Catalog.Select(x=>x.id).Append("account_detail").ToArray();
string Snapshot(string id){var reportFilter=id is "supplier_monthly_payable" or "customer_monthly_receivable" ? new ReportFilter{From="2024-01-01",To="2024-01-31"} : filter;var report=rs.Build(id,reportFilter);return System.Text.Json.JsonSerializer.Serialize(report.Rows.Select(x=>x.Cells).ToArray())+System.Text.Json.JsonSerializer.Serialize(report.TotalRow);}
var snapshots=reportIds.ToDictionary(id=>id,Snapshot);long beforeFunds=VoucherService.TotalFunds();var backup=AppSession.Db.BackupTo();
Post(new Voucher{Kind=VoucherKind.Income,Nature=VoucherNature.OtherIn,AccountId=bank,AmountCents=12345,BizDate="2025-01-01",Summary="restore mutation"});Eq("backup mutation visible",VoucherService.TotalFunds(),beforeFunds+12345);
var safety=AppSession.Db.RestoreFrom(backup);Assert("restore safety backup exists",File.Exists(safety));Eq("complex restore funds",VoucherService.TotalFunds(),beforeFunds);
foreach(var id in reportIds)Assert("complex restore exact report "+id,Snapshot(id)==snapshots[id]);
using(var c=AppSession.Db.Open()){Eq("foreign key check",new SqlCmd(c,"SELECT COUNT(*) FROM pragma_foreign_key_check").Long(),0);Eq("settlement over payment check",new SqlCmd(c,"SELECT COUNT(*) FROM (SELECT pay_voucher_id FROM settlements s JOIN vouchers v ON v.id=s.pay_voucher_id GROUP BY pay_voucher_id HAVING SUM(s.amount_cents)>MAX(v.amount_cents))").Long(),0);}
Console.WriteLine($"RESULT checks={checks} failures={fail} settlementEvents={history.Count} invoiceLinkEvents={links.Count}");return fail==0?0:1;
class Allocation(long id,long pay,long biz,long amount,string start){public long Id=id,Pay=pay,Biz=biz,Amount=amount;public string Start=start;public string? End;}
class Link(long invoice,long biz,long amount,string start){public long Invoice=invoice,Biz=biz,Amount=amount;public string Start=start;public string? End;}
