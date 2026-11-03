using Cashier.Core;
using Cashier.Core.Services;

if (args.Length != 1 || Directory.Exists(args[0])) throw new Exception("Specify a fresh, non-existing test data directory");
var dir = Path.GetFullPath(args[0]);
AppSession.Db = new Database(dir); AppSession.Db.EnsureCreated();
AppSession.Current = new AuthService().Login("admin", "admin888");
int checks=0, failures=0;
void Check(string label, long actual, long expected) { checks++; if(actual!=expected) { failures++; Console.WriteLine($"FAIL | {label} | expected={expected}, actual={actual} (分)"); } }
var bank=MasterDataService.SaveAccount(new Account{Code="D-B",Name="计算测试银行",Kind="bank",OpeningCents=100000});
var cash=MasterDataService.SaveAccount(new Account{Code="D-C",Name="计算测试现金",Kind="cash",OpeningCents=20000});
var customer=MasterDataService.SavePartner(new Partner{Code="D-CUST",Name="计算测试客户",Kind="customer",OpeningReceivableCents=30000});
var supplier=MasterDataService.SavePartner(new Partner{Code="D-SUP",Name="计算测试供应商",Kind="supplier",OpeningPayableCents=40000});
var svc=new VoucherService();
long Post(VoucherKind kind,VoucherNature nature,string date,long amount,long? acc=null,long? to=null,long? partner=null,long fee=0,bool audited=true,bool rev=false,bool cost=false) {
 var v=new Voucher{Kind=kind,Nature=nature,BizDate=date,AmountCents=amount,AccountId=acc,ToAccountId=to,PartnerId=partner,FeeCents=fee,Summary="独立计算测试",CountAsRevenue=rev,CountAsCost=cost};
 long id=svc.Create(v); if(audited) svc.Audit(id); return id;
}
Post(VoucherKind.Income,VoucherNature.OtherIn,"2025-12-31",10000,bank);
Post(VoucherKind.Income,VoucherNature.CustomerReceipt,"2026-01-01",5000,bank,partner:customer);
Post(VoucherKind.Expense,VoucherNature.SupplierPayment,"2026-01-31",7000,bank,partner:supplier);
Post(VoucherKind.Transfer,VoucherNature.None,"2026-01-15",3000,bank,cash,fee:100);
Post(VoucherKind.Income,VoucherNature.CustomerPrepay,"2026-01-10",2000,cash,partner:customer);
Post(VoucherKind.Expense,VoucherNature.CustomerRefund,"2026-01-12",1000,cash,partner:customer);
Post(VoucherKind.Income,VoucherNature.SupplierRefund,"2026-01-20",500,bank,partner:supplier);
Post(VoucherKind.Expense,VoucherNature.SupplierPrepay,"2026-01-20",3000,bank,partner:supplier);
Post(VoucherKind.Receivable,VoucherNature.None,"2025-12-25",20000,partner:customer,rev:true);
Post(VoucherKind.Receivable,VoucherNature.None,"2026-01-12",10000,partner:customer,rev:true);
Post(VoucherKind.Payable,VoucherNature.None,"2025-12-25",15000,partner:supplier,cost:true);
Post(VoucherKind.Payable,VoucherNature.None,"2026-01-12",8000,partner:supplier,cost:true);
Post(VoucherKind.Income,VoucherNature.OtherIn,"2026-02-01",99999,bank);
Post(VoucherKind.Income,VoucherNature.OtherIn,"2026-01-18",99999,bank,audited:false);
var rs=new ReportService();
var filter=new ReportFilter{From="2026-01-01",To="2026-01-31"};
var account=rs.Build("account_report",filter);
var b=account.Rows.Single(r=>r.DrillId==bank).Cells; var c=account.Rows.Single(r=>r.DrillId==cash).Cells;
Check("银行期初",Convert.ToInt64(b[1]),110000); Check("银行流入",Convert.ToInt64(b[2]),5500); Check("银行流出",Convert.ToInt64(b[3]),13100); Check("银行期末",Convert.ToInt64(b[4]),102400);
Check("现金期初",Convert.ToInt64(c[1]),20000); Check("现金流入",Convert.ToInt64(c[2]),5000); Check("现金流出",Convert.ToInt64(c[3]),1000); Check("现金期末",Convert.ToInt64(c[4]),24000);
Check("截至1月银行余额",MasterDataService.AccountBalance(bank,"2026-01-31"),102400); Check("截至1月现金余额",MasterDataService.AccountBalance(cash,"2026-01-31"),24000);
var cr=rs.Build("customer_summary",new ReportFilter{From=filter.From,To=filter.To,PartnerId=customer}).Rows.Single().Cells;
Check("客户期初",Convert.ToInt64(cr[1]),50000); Check("客户应收发生",Convert.ToInt64(cr[2]),10000); Check("客户回款发生",Convert.ToInt64(cr[3]),5000); Check("客户预收发生",Convert.ToInt64(cr[4]),2000); Check("客户净应收含退款",Convert.ToInt64(cr[5]),54000);
var sr=rs.Build("supplier_summary",new ReportFilter{From=filter.From,To=filter.To,PartnerId=supplier}).Rows.Single().Cells;
Check("供应商期初",Convert.ToInt64(sr[1]),55000); Check("供应商应付发生",Convert.ToInt64(sr[2]),8000); Check("供应商付款发生",Convert.ToInt64(sr[3]),7000); Check("供应商预付发生",Convert.ToInt64(sr[4]),3000); Check("供应商净应付含退回",Convert.ToInt64(sr[5]),53500);
var inc=rs.Build("income_detail",filter); var exp=rs.Build("expense_detail",filter);
Check("业务收入不含客户回款预收及供应商退回",Convert.ToInt64(inc.TotalRow![8]),10000); Check("业务成本含应付和转账手续费，不含往来收付",Convert.ToInt64(exp.TotalRow![8]),8100);
foreach(var acc in new[]{bank,cash}) {
 var ar=rs.Build("account_report",new ReportFilter{From=filter.From,To=filter.To,AccountId=acc}).Rows.Single().Cells;
 var mr=rs.Build("account_month",new ReportFilter{From=filter.From,To=filter.To,AccountId=acc}).Rows.Single().Cells;
 for(int i=1;i<=4;i++) Check($"账户月报与期间报表第{i}列一致 {acc}",Convert.ToInt64(mr[i+1]),Convert.ToInt64(ar[i]));
}
// Exact integer arithmetic supplies an independent oracle for signed, half-cent and tax-rate cases.
long RoundRatio(long numerator,long denominator) { long abs=Math.Abs(numerator); long rounded=(abs+denominator/2)/denominator; return numerator<0?-rounded:rounded; }
foreach(int bp in new[]{0,100,300,600,900,1300,2000,10000}) foreach(long gross in new long[]{-999999,-11301,-113,-100,-1,0,1,2,3,50,99,100,101,113,114,9999,11301,999999}) {
 var (net,tax)=Money.SplitTax(gross,bp); var expected=RoundRatio(gross*10000,10000+bp);
 Check($"税分离 净额 gross={gross} bp={bp}",net,expected); Check($"税分离守恒 gross={gross} bp={bp}",net+tax,gross);
}
foreach(int bp in new[]{0,100,300,600,900,1300,2000,10000}) foreach(long net in new long[]{-999999,-101,-50,-1,0,1,2,3,50,99,100,101,999999}) {
 var (gross,tax)=Money.GrossFromNet(net,bp); var expected=RoundRatio(net*bp,10000);
 Check($"价外税 税额 net={net} bp={bp}",tax,expected); Check($"价外税守恒 net={net} bp={bp}",gross,net+expected);
}
Post(VoucherKind.Income,VoucherNature.OtherIn,"1990-01-01",1234,bank);
var old=rs.Build("account_report",new ReportFilter{From="2026-01-01",To="2026-01-31",AccountId=bank}).Rows.Single().Cells;
Check("最早允许日期资金计入期初",Convert.ToInt64(old[1]),111234);
Check("最早日期资金后的账户报表和余额一致",Convert.ToInt64(old[4]),MasterDataService.AccountBalance(bank,"2026-01-31"));
// Deterministic ledger cross-check: all ten cash transaction natures across periods and accounts.
var randBank=MasterDataService.SaveAccount(new Account{Code="R-B",Name="交叉测试银行",Kind="bank",OpeningCents=12345});
var randCash=MasterDataService.SaveAccount(new Account{Code="R-C",Name="交叉测试现金",Kind="cash",OpeningCents=67890});
var randCustomer=MasterDataService.SavePartner(new Partner{Code="R-CUST",Name="交叉测试客户",Kind="customer",OpeningReceivableCents=1111});
var randSupplier=MasterDataService.SavePartner(new Partner{Code="R-SUP",Name="交叉测试供应商",Kind="supplier",OpeningPayableCents=2222});
var natures=new[]{VoucherNature.SaleCash,VoucherNature.CustomerReceipt,VoucherNature.CustomerPrepay,VoucherNature.SupplierRefund,VoucherNature.OtherIn,VoucherNature.PurchaseCash,VoucherNature.SupplierPayment,VoucherNature.SupplierPrepay,VoucherNature.CustomerRefund,VoucherNature.OtherOut};
var entries=new List<(string date,long account,long amount,int nature)>();
for(int i=0;i<40;i++) {
 int n=i%10, month=1+i/10; long amount=101+i*37; long acc=i%2==0?randBank:randCash; string date=$"2026-{month:00}-{(1+i%25):00}";
 var partner=n is 1 or 2 or 8 ? randCustomer : n is 3 or 6 or 7 ? randSupplier : (long?)null;
 Post(n<5?VoucherKind.Income:VoucherKind.Expense,natures[n],date,amount,acc,partner:partner);
 entries.Add((date,acc,amount,n));
}
foreach(int m in new[]{1,2,3,4}) {
 string start=$"2026-{m:00}-01",end=new DateTime(2026,m,DateTime.DaysInMonth(2026,m)).ToString("yyyy-MM-dd");
 foreach(var acc in new[]{randBank,randCash}) {
  long open=acc==randBank?12345:67890;
  open+=entries.Where(e=>e.account==acc && string.CompareOrdinal(e.date,start)<0).Sum(e=>e.nature<5?e.amount:-e.amount);
  long inflow=entries.Where(e=>e.account==acc && e.date.StartsWith($"2026-{m:00}") && e.nature<5).Sum(e=>e.amount);
  long outflow=entries.Where(e=>e.account==acc && e.date.StartsWith($"2026-{m:00}") && e.nature>=5).Sum(e=>e.amount);
  var row=rs.Build("account_report",new ReportFilter{From=start,To=end,AccountId=acc}).Rows.Single().Cells;
  Check($"分账户{m}月期初",Convert.ToInt64(row[1]),open); Check($"分账户{m}月流入",Convert.ToInt64(row[2]),inflow); Check($"分账户{m}月流出",Convert.ToInt64(row[3]),outflow); Check($"分账户{m}月期末",Convert.ToInt64(row[4]),open+inflow-outflow);
 }
 var cust=rs.Build("customer_summary",new ReportFilter{From=start,To=end,PartnerId=randCustomer}).Rows.Single().Cells;
 var supp=rs.Build("supplier_summary",new ReportFilter{From=start,To=end,PartnerId=randSupplier}).Rows.Single().Cells;
 long crNet=1111+entries.Where(e=>string.CompareOrdinal(e.date,end)<=0).Sum(e=>e.nature is 1 or 2 ? -e.amount : e.nature==8 ? e.amount : 0);
 long suNet=2222+entries.Where(e=>string.CompareOrdinal(e.date,end)<=0).Sum(e=>e.nature is 6 or 7 ? -e.amount : e.nature==3 ? e.amount : 0);
 Check($"多月客户净额 {m}",Convert.ToInt64(cust[5]),crNet); Check($"多月供应商净额 {m}",Convert.ToInt64(supp[5]),suNet);
 var allMonth=entries.Where(e=>e.date.StartsWith($"2026-{m:00}")).ToList();
 Check($"多月业务收入 {m}",Convert.ToInt64(rs.Build("income_detail",new ReportFilter{From=start,To=end}).TotalRow![8]),allMonth.Where(e=>e.nature is 0 or 4).Sum(e=>e.amount)+(m==1?10000:m==2?99999:0));
 Check($"多月业务成本 {m}",Convert.ToInt64(rs.Build("expense_detail",new ReportFilter{From=start,To=end}).TotalRow![8]),allMonth.Where(e=>e.nature is 5 or 9).Sum(e=>e.amount)+(m==1?8100:0));
}
Console.WriteLine($"TOTAL checks={checks} failures={failures}");
return failures==0?0:1;



