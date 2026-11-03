using System.Collections.Concurrent;
using System.Text.Json;
using Cashier.Core;
using Cashier.Core.Services;
using ClosedXML.Excel;

if(args.Length!=1||Directory.Exists(args[0]))throw new Exception("Specify a fresh isolated test root");
var root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
int checks=0,failures=0;
void Check(string name,bool ok){checks++;if(!ok)failures++;Console.WriteLine($"{(ok?"PASS":"FAIL")} | {name}");}
void Eq(string name,long expected,long actual)=>Check($"{name}: expected={expected}, actual={actual}",expected==actual);
void Denied(string name,Action action){try{action();Check(name,false);}catch(Exception e) when(e is AppException or Microsoft.Data.Sqlite.SqliteException or OverflowException){Check(name,true);}}
Database New(string name){var db=new Database(Path.Combine(root,name));db.EnsureCreated();AppSession.Db=db;new AuthService().Login("admin","admin888");return db;}
void Admin()=>new AuthService().Login("admin","admin888");
long Account(string code,long opening=0)=>MasterDataService.SaveAccount(new Account {Code=code,Name="账户"+code,OpeningCents=opening});
long Partner(string name,string kind,long ar=0,long ap=0)=>MasterDataService.SavePartner(new Partner{Name=name,Kind=kind,OpeningReceivableCents=ar,OpeningPayableCents=ap});
Account GetAccount(long id){using var c=AppSession.Db.Open();return MasterDataService.GetAccount(c,id)!;}
Partner GetPartner(long id)=>MasterDataService.GetPartner(id)!;
long Scalar(string sql){using var c=AppSession.Db.Open();return new SqlCmd(c,sql).Long();}
void Sql(string sql){using var c=AppSession.Db.Open();new SqlCmd(c,sql).Exec();}
string today=SqlUtil.Today(),yesterday=DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd"),future=DateTime.Today.AddDays(35).ToString("yyyy-MM-dd");
var first=DateTime.Today.AddYears(-1).ToString("yyyy-MM")+"-01";
var db=New("main");
Eq("new schema v5",5,db.GetUserVersion());Check("new ledger setup state",!OpeningBalanceService.State().IsLocked);
var a=Account("TEST-A",100000);var b=Account("TEST-B",-2000);
var customer=Partner("客户","customer",30000);var supplier=Partner("供应商","supplier",0,-4000);var both=Partner("双身份","both",500,700);
var old=GetAccount(a);old.OpeningCents=110000;MasterDataService.SaveAccount(old);
Eq("opening editable before confirmation",110000,GetAccount(a).OpeningCents);
var vouchers=new VoucherService();
long Post(Voucher v){var id=vouchers.Create(v);vouchers.Audit(id);return id;}
Post(new Voucher{Kind=VoucherKind.Income,Nature=VoucherNature.SaleCash,BizDate=first,AccountId=a,AmountCents=12000,Summary="实际收入"});
Post(new Voucher{Kind=VoucherKind.Expense,Nature=VoucherNature.SupplierPayment,BizDate=first,AccountId=a,PartnerId=supplier,AmountCents=2000,Summary="付款"});
Post(new Voucher{Kind=VoucherKind.Receivable,BizDate=first,PartnerId=customer,AmountCents=5000,Summary="应收",CountAsRevenue=true});
Denied("confirm rejects future date",()=>OpeningBalanceService.Confirm(future,"测试"));
Denied("confirm rejects date after existing business",()=>OpeningBalanceService.Confirm(today,"测试"));
Denied("confirm reason required",()=>OpeningBalanceService.Confirm(first," "));
Denied("cannot adjust before confirmation",()=>OpeningBalanceService.Create("account",a,today,100,"test",Guid.NewGuid().ToString()));
OpeningBalanceService.Confirm(first,"核对全部期初后启用");Check("confirmed and locked",OpeningBalanceService.State().IsLocked);
Denied("cannot confirm twice",()=>OpeningBalanceService.Confirm(first,"重复"));
old.OpeningCents++;Denied("stale account edit blocked",()=>MasterDataService.SaveAccount(old));
var cp=GetPartner(customer);cp.OpeningReceivableCents++;Denied("receivable opening edit blocked",()=>MasterDataService.SavePartner(cp));
var sp=GetPartner(supplier);sp.OpeningPayableCents++;Denied("payable opening edit blocked",()=>MasterDataService.SavePartner(sp));
Denied("nonzero new account opening blocked",()=>Account("BAD",1));
Denied("nonzero new partner opening blocked",()=>Partner("非法期初","customer",1));
var zero=Account("ZERO");Check("zero opening new account allowed",zero>0);
old=GetAccount(a);old.Name="允许修改名称";MasterDataService.SaveAccount(old);Check("metadata edit allowed",GetAccount(a).Name==old.Name);
Denied("raw SQL cannot alter locked account opening",()=>Sql($"UPDATE accounts SET opening_cents=opening_cents+1 WHERE id={a}"));
Denied("raw SQL cannot alter locked partner opening",()=>Sql($"UPDATE partners SET opening_receivable_cents=1 WHERE id={customer}"));
Denied("raw SQL cannot unlock",()=>Sql("UPDATE opening_control SET locked_at=''"));
Denied("cannot delete control",()=>Sql("DELETE FROM opening_control"));
Denied("cannot delete unused account with opening",()=>MasterDataService.DeleteAccount(b));
var rs=new ReportService();
ReportFilter Filter(string to,long? partner=null,long? account=null)=>new(){From=first,To=to,PartnerId=partner,AccountId=account};
string Snapshot(string to)=>JsonSerializer.Serialize(ReportService.Catalog.Select(item=>rs.Build(item.id,item.id.Contains("monthly")
    ?new ReportFilter{From=DateTime.Parse(to).ToString("yyyy-MM")+"-01",To=new DateTime(DateTime.Parse(to).Year,DateTime.Parse(to).Month,1).AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd")}
    :Filter(to))));
// Use the previous complete month to include all catalog reports consistently.
var previousMonthEnd=new DateTime(DateTime.Today.Year,DateTime.Today.Month,1).AddDays(-1).ToString("yyyy-MM-dd");
var historical=Snapshot(previousMonthEnd);
var unchangedCash=JsonSerializer.Serialize(rs.Build("cash_detail",Filter(future)));
var unchangedProfit=JsonSerializer.Serialize(rs.Build("project_year",Filter(future)));
var request=Guid.NewGuid().ToString("N");
var adjust=OpeningBalanceService.Create("account",a,today,20000,"账户核对差额",request);
Eq("duplicate request returns same ID",adjust,OpeningBalanceService.Create("account",a,today,20000,"账户核对差额",request));
Denied("duplicate request with changed amount blocked",()=>OpeningBalanceService.Create("account",a,today,20001,"账户核对差额",request));
Eq("account past balance preserved",120000,MasterDataService.AccountBalance(a,yesterday));
Eq("account current balance includes adjustment",140000,MasterDataService.AccountBalance(a,today));
var record=OpeningBalanceService.Query(kind:"account",targetId:a).Single();Eq("snapshot before",120000,record.BalanceBefore);Eq("snapshot after",140000,record.BalanceAfter);
Eq("opening retained",110000,GetAccount(a).OpeningCents);
OpeningBalanceService.Create("receivable",customer,today,-60000,"往来核对",Guid.NewGuid().ToString());
OpeningBalanceService.Create("payable",supplier,today,10000,"往来核对",Guid.NewGuid().ToString());
Eq("negative net receivable retained",-25000,MasterDataService.ListPartners("customer").Single(x=>x.Id==customer).NetReceivableCents);
Eq("payable correction",4000,MasterDataService.ListPartners("supplier").Single(x=>x.Id==supplier).NetPayableCents);
Eq("customer summary corrected",-25000,Convert.ToInt64(rs.Build("customer_summary",Filter(today,customer)).Rows.Single().Cells[5]));
Eq("supplier summary corrected",4000,Convert.ToInt64(rs.Build("supplier_summary",Filter(today,supplier)).Rows.Single().Cells[5]));
var monthFrom=DateTime.Today.ToString("yyyy-MM")+"-01";var monthEnd=new DateTime(DateTime.Today.Year,DateTime.Today.Month,1).AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd");
Eq("monthly customer balance corrected",-25000,Convert.ToInt64(rs.Build("customer_monthly_receivable",new(){From=monthFrom,To=monthEnd,PartnerId=customer}).Rows.Single().Cells[5]));
Eq("monthly supplier balance corrected",4000,Convert.ToInt64(rs.Build("supplier_monthly_payable",new(){From=monthFrom,To=monthEnd,PartnerId=supplier}).Rows.Single().Cells[5]));
Check("all historical reports byte equivalent",historical==Snapshot(previousMonthEnd));
Check("business cash report unchanged",unchangedCash==JsonSerializer.Serialize(rs.Build("cash_detail",Filter(future))));
Check("project profit unchanged",unchangedProfit==JsonSerializer.Serialize(rs.Build("project_year",Filter(future))));
var futureId=OpeningBalanceService.Create("account",a,future,5000,"未来生效",Guid.NewGuid().ToString());
Eq("future correction excluded from current balance",140000,MasterDataService.AccountBalance(a));
Eq("future correction included on date",145000,MasterDataService.AccountBalance(a,future));
var todayDetail=JsonSerializer.Serialize(rs.Build("account_detail",Filter(today,account:a)));
var reverseId=OpeningBalanceService.Reverse(adjust,future,"本期更正已复核，追加反向记录");
Check("future reversal preserves historical detail and status",todayDetail==JsonSerializer.Serialize(rs.Build("account_detail",Filter(today,account:a))));
Eq("future reversal preserves current balance",140000,MasterDataService.AccountBalance(a,today));
Eq("future reversal nets on effective date",125000,MasterDataService.AccountBalance(a,future));
Denied("cannot reverse twice",()=>OpeningBalanceService.Reverse(adjust,future,"重复"));
Denied("cannot reverse a reversal",()=>OpeningBalanceService.Reverse(reverseId,future,"重复"));
Denied("cannot reverse before original date",()=>OpeningBalanceService.Reverse(futureId,today,"提前"));
Denied("immutable adjustment update",()=>Sql($"UPDATE balance_adjustments SET reason='篡改' WHERE id={adjust}"));
Denied("immutable adjustment delete",()=>Sql($"DELETE FROM balance_adjustments WHERE id={adjust}"));
foreach(var (kind,target,date,amount,reason,key) in new[]{
    ("account",a,yesterday,1L,"早于今天","past"),("unknown",a,today,1L,"类型错误","kind"),
    ("account",a,"2026-02-30",1L,"日期错误","date"),("account",a,"2101-01-01",1L,"范围","year"),
    ("account",a,today,0L,"零","zero"),("account",a,today,Money.MaxCents+1,"超额","max"),
    ("account",a,today,long.MinValue,"超额","min"),("account",a,today,1L," ","empty-reason"),
    ("account",a,today,1L,new string('字',501),"long-reason"),("receivable",supplier,today,1L,"身份错误","identity"),
    ("account",999999L,today,1L,"对象不存在","missing"),("account",a,today,1L,"缺少标识","")})
    Denied("reject invalid "+key,()=>OpeningBalanceService.Create(kind,target,date,amount,reason,key));
PeriodService.Close(SqlUtil.PeriodOf(future));
Denied("closed period rejects adjustment",()=>OpeningBalanceService.Create("account",a,future,1,"关账","closed"));
Denied("closed period rejects reverse",()=>OpeningBalanceService.Reverse(futureId,future,"关账"));
PeriodService.Reopen(SqlUtil.PeriodOf(future),"继续隔离测试");
var disabled=GetAccount(a);disabled.IsActive=false;MasterDataService.SaveAccount(disabled);
Denied("inactive target rejects new adjustment",()=>OpeningBalanceService.Create("account",a,today,1,"停用","inactive"));
OpeningBalanceService.Reverse(futureId,future,"停用对象仍允许冲回原调整");
Check("inactive target reversal succeeds",OpeningBalanceService.Query(kind:"account").Single(x=>x.Id==futureId).HasReversal);
disabled.IsActive=true;MasterDataService.SaveAccount(disabled);
OpeningBalanceService.Create("receivable",both,today,1,"双身份","both-ar");OpeningBalanceService.Create("payable",both,today,1,"双身份","both-ap");
var bp=GetPartner(both);bp.Kind="customer";bp.OpeningPayableCents=0;Denied("cannot discard partner role with adjustments",()=>MasterDataService.SavePartner(bp));
var auth=new AuthService();
foreach(var role in new[]{UserRole.Accountant,UserRole.Cashier,UserRole.Viewer})
{
    var username="role_"+role;Admin();auth.Create(username,username,"testing123",role);auth.Login(username,"testing123");
    Denied(role+" cannot confirm opening",()=>OpeningBalanceService.Confirm(first,"越权"));
    if(role==UserRole.Accountant) Check("accountant adjustment allowed",OpeningBalanceService.Create("account",a,today,1,"会计更正","accountant")>0);
    else {Denied(role+" cannot create adjustment",()=>OpeningBalanceService.Create("account",a,today,1,"越权",username));Denied(role+" cannot reverse",()=>OpeningBalanceService.Reverse(reverseId,future,"越权"));}
    Check(role+" can read adjustment history",OpeningBalanceService.Query().Count>0);
}
Admin();var stale=auth.Login("role_Accountant","testing123");Admin();auth.Update(stale.Id,stale.DisplayName,UserRole.Viewer,true);AppSession.Current=stale;
Denied("stale role rejected inside transaction",()=>OpeningBalanceService.Create("account",a,today,1,"旧会话","stale"));
Admin();
var beforeFailure=Scalar("SELECT COUNT(*) FROM balance_adjustments");
Sql("CREATE TRIGGER qa_audit_abort BEFORE INSERT ON audit_log WHEN NEW.action='balance-adjust' BEGIN SELECT RAISE(ABORT,'QA audit failure'); END;");
Denied("audit failure rolls back adjustment",()=>OpeningBalanceService.Create("account",a,today,1,"原子回滚","audit-fail"));
Eq("failed audit leaves no record",beforeFailure,Scalar("SELECT COUNT(*) FROM balance_adjustments"));Sql("DROP TRIGGER qa_audit_abort");
var bag=new ConcurrentBag<long>();Parallel.For(0,8,_=>bag.Add(OpeningBalanceService.Create("account",a,today,7,"并发去重","concurrent")));
Check("eight concurrent retries return one ID",bag.Count==8&&bag.Distinct().Count()==1);Eq("one concurrent record",1,Scalar("SELECT COUNT(*) FROM balance_adjustments WHERE request_id='concurrent'"));
// Independent deterministic oracle over signed adjustments, dates, monthly/yearly reports and both partner identities.
var rng=new Random(20261101);var oracle=new List<(string kind,long target,string date,long amount)>();
for(int i=0;i<160;i++)
{
    var kind=new[]{"account","receivable","payable"}[i%3];var target=kind=="account"?zero:both;
    var date=DateTime.Today.AddDays(rng.Next(0,420)).ToString("yyyy-MM-dd");var amount=rng.Next(-9000,9001);if(amount==0)amount=1;
    OpeningBalanceService.Create(kind,target,date,amount,"独立模型随机调整","random-"+i);oracle.Add((kind,target,date,amount));
}
for(int day=-1;day<=430;day+=7)
{
    var date=DateTime.Today.AddDays(day).ToString("yyyy-MM-dd");
    long Expected(string kind)=>oracle.Where(x=>x.kind==kind&&string.CompareOrdinal(x.date,date)<=0).Sum(x=>x.amount);
    Eq("oracle account "+date,Expected("account"),MasterDataService.AccountBalance(zero,date));
    var pp=MasterDataService.ListPartners("both",true,date).Single(x=>x.Id==both);
    Eq("oracle receivable "+date,500+(day>=0?1:0)+Expected("receivable"),pp.NetReceivableCents);
    Eq("oracle payable "+date,700+(day>=0?1:0)+Expected("payable"),pp.NetPayableCents);
    var ar=rs.Build("account_report",new(){From=first,To=date,AccountId=zero});Eq("oracle account report "+date,Expected("account"),Convert.ToInt64(ar.Rows.Single().Cells[4]));
    foreach(var id in new[]{"account_month","account_year"})
    {
        var r=rs.Build(id,new(){From=first,To=date,AccountId=zero});long running=0;
        foreach(var row in r.Rows){Eq("rolling "+id,running,Convert.ToInt64(row.Cells[2]));Eq("no business inflow "+id,0,Convert.ToInt64(row.Cells[3]));Eq("no business outflow "+id,0,Convert.ToInt64(row.Cells[4]));running+=Convert.ToInt64(row.Cells[6]);Eq("rolling end "+id,running,Convert.ToInt64(row.Cells[5]));}
        Eq("oracle end "+id+date,Expected("account"),running);
    }
}
var excel=Path.Combine(root,"account-adjustments.xlsx");var export=rs.Build("account_report",Filter(future,account:a));ExcelService.ExportReport(export,excel);
using(var workbook=new XLWorkbook(excel)){var sheet=workbook.Worksheet(1);Check("export includes separate adjustment column",sheet.Cell(5,6).GetString()=="本期余额调整");Eq("export ending agrees",Convert.ToInt64(export.Rows[0].Cells[4]),(long)Math.Round(sheet.Cell(6,5).GetDouble()*100));}
Check("historical reports still unchanged after random model",historical==Snapshot(previousMonthEnd));
Eq("foreign key integrity",0,Scalar("SELECT COUNT(*) FROM pragma_foreign_key_check"));
var countBeforeRestore=Scalar("SELECT COUNT(*) FROM balance_adjustments");var backup=db.BackupTo();
OpeningBalanceService.Create("account",a,today,1,"恢复前临时调整","restore-temp");db.RestoreFrom(backup);
Check("restore logs out",AppSession.Current==null);
Denied("adjustment history rejects expired login",()=>OpeningBalanceService.Query());
Check("legacy report snapshot after restore remains available",rs.Build("account_detail",Filter(today)).Rows.Count>0);
Admin();Eq("restored adjustments",countBeforeRestore,Scalar("SELECT COUNT(*) FROM balance_adjustments"));Check("lock survives restore",OpeningBalanceService.State().IsLocked);
// Real v1-v4 fixtures, schema upgrade backup, failure rollback, retry and legacy restore.
var mainDb=db;
for(int version=1;version<=4;version++)
{
    var legacy=new Database(Path.Combine(root,"legacy-v"+version));
    using(var c=legacy.Open()){Schema.CreateV1(c);Schema.Seed(c);new SqlCmd(c,"PRAGMA user_version=1").Exec();if(version>1)Schema.Migrate(c,1,version);new SqlCmd(c,"UPDATE accounts SET opening_cents=12345 WHERE code='1001'").Exec();}
    legacy.EnsureCreated();AppSession.Db=legacy;Admin();Eq("v"+version+" upgrades to v5",5,legacy.GetUserVersion());Check("v"+version+" existing opening auto locks",OpeningBalanceService.State().IsLocked);
    Eq("v"+version+" original balance retained",12345,GetAccount(MasterDataService.ListAccounts().Single(x=>x.Code=="1001").Id).OpeningCents);
    Check("v"+version+" preupgrade backup exists",Directory.GetFiles(legacy.BackupDir,"*preupgrade-v"+version+"-to-v5.db").Length==1);
}
var bad=new Database(Path.Combine(root,"migration-fault"));
using(var c=bad.Open()){Schema.CreateV1(c);Schema.Seed(c);new SqlCmd(c,"PRAGMA user_version=1").Exec();Schema.Migrate(c,1,4);new SqlCmd(c,"CREATE TABLE balance_adjustments(bad INTEGER)").Exec();}
Denied("migration failure reported",()=>bad.EnsureCreated());Eq("migration failure leaves v4",4,bad.GetUserVersion());
using(var c=bad.Open()){Eq("migration rollback removes partial control table",0,new SqlCmd(c,"SELECT COUNT(*) FROM sqlite_master WHERE name='opening_control'").Long());new SqlCmd(c,"DROP TABLE balance_adjustments").Exec();}
bad.EnsureCreated();Eq("migration retry succeeds",5,bad.GetUserVersion());
AppSession.Db=mainDb;Admin();
var corrupt=Path.Combine(root,"corrupt-backup.db");File.Copy(backup,corrupt);
using(var c=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+corrupt)){c.Open();new SqlCmd(c,"DROP TRIGGER tr_adjustment_update").Exec();}
Denied("restore rejects missing adjustment trigger",()=>mainDb.RestoreFrom(corrupt));Eq("failed restore leaves records",countBeforeRestore,Scalar("SELECT COUNT(*) FROM balance_adjustments"));
var legacyBackup=Directory.GetFiles(Path.Combine(root,"legacy-v4/backups"),"*preupgrade-v4-to-v5.db").Single();mainDb.RestoreFrom(legacyBackup);Admin();
Check("legacy restore migrates and auto locks",OpeningBalanceService.State().IsLocked&&mainDb.GetUserVersion()==5);Eq("legacy restore retains opening",12345,MasterDataService.ListAccounts().Single(x=>x.Code=="1001").OpeningCents);
var race=New("lock-race");var results=new ConcurrentBag<bool>();Parallel.For(0,8,_=>{try{OpeningBalanceService.Confirm(today,"并发确认");results.Add(true);}catch(AppException){results.Add(false);}});
Eq("one confirmation wins concurrency",1,results.Count(x=>x));
Console.WriteLine($"RESULT checks={checks} failures={failures} randomAdjustments={oracle.Count}");
File.WriteAllText(Path.Combine(root,"summary.json"),JsonSerializer.Serialize(new{checks,failures,randomAdjustments=oracle.Count},new JsonSerializerOptions{WriteIndented=true}));
return failures==0?0:1;
