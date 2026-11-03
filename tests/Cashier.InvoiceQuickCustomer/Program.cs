using Cashier.Core;
using Cashier.Core.Services;
using Microsoft.Data.Sqlite;

if(args.Length!=1 || Directory.Exists(args[0])) throw new Exception("Provide one fresh, non-existing isolated data directory");
AppSession.Db=new Database(args[0]); AppSession.Db.EnsureCreated();
var admin=new AuthService().Login("admin","admin888"); AppSession.Current=admin;
int checks=0,failures=0;
void Check(string n,bool ok){checks++;Console.WriteLine($"{(ok?"PASS":"FAIL")} | {n}");if(!ok)failures++;}
void Reject(string n,Action a){try{a();Check(n,false);}catch(AppException){Check(n,true);}}
long Count(string where="1=1"){using var c=AppSession.Db.Open();return new SqlCmd(c,"SELECT COUNT(*) FROM partners WHERE "+where).Long();}
long InvCount(){using var c=AppSession.Db.Open();return new SqlCmd(c,"SELECT COUNT(*) FROM invoices").Long();}
void Sql(string sql){using var c=AppSession.Db.Open();new SqlCmd(c,sql).Exec();}
InvoiceCustomerResult Create(string name,string? token=null)=>MasterDataService.CreateCustomerForInvoice(new(name," 联系人 "," 010-12345678 转 123 "," 地址 "),token);
long Seed(string code,string name,string kind="customer",bool active=true){var id=MasterDataService.SavePartner(new Partner{Code=code,Name=name,Kind=kind,Contact="原联系人",Phone="原电话",Address="原地址"});if(!active){var p=MasterDataService.GetPartner(id)!;p.IsActive=false;MasterDataService.SavePartner(p);}return id;}
var bank=MasterDataService.ListAccounts(false).First().Id;var initialFunds=VoucherService.TotalFunds();
// Explicit legacy fixtures bypass normal creation, which now assigns codes itself.
Sql("INSERT INTO partners(code,name,kind,is_active) VALUES('KH-0001','occupied supplier','supplier',1),('KH-0002','inactive','customer',0),('kh-0003','lower case','customer',1),('KH-whatever','nonstandard','customer',1),('KH-922337203685477580899999','huge suffix','customer',1)");
var result=Create("  测试客户  ");var customer=MasterDataService.GetPartner(result.Id!.Value)!;
Check("created result has real ID",result.Outcome==InvoiceCustomerOutcome.Created&&customer.Id>0&&result.Code==customer.Code);
Check("allocate actual unused code across all roles/states",customer.Code=="KH-0004");
Check("customer fixed defaults",customer.Kind=="customer"&&customer.IsActive&&customer.OpeningReceivableCents==0&&customer.OpeningPayableCents==0);
Check("trim optional fields and permit landline extension",customer.Name=="测试客户"&&customer.Contact=="联系人"&&customer.Phone=="010-12345678 转 123"&&customer.Address=="地址");
using(var c=AppSession.Db.Open())Check("same transaction audit row",new SqlCmd(c,"SELECT COUNT(*) FROM audit_log WHERE entity='partner' AND entity_id=$i AND action='create'").P("$i",customer.Id).Long()==1);
var before=Count();foreach(var s in new[]{""," ","\t\r\n"})Reject("reject blank name",()=>Create(s));Check("blank names write nothing",Count()==before);
Reject("reject oversized optional field",()=>MasterDataService.CreateCustomerForInvoice(new("long",Phone:new string('1',101))));
var nulls=MasterDataService.CreateCustomerForInvoice(new("无选填", " ","",null));var np=MasterDataService.GetPartner(nulls.Id!.Value)!;Check("empty optional fields become null",np.Contact==null&&np.Phone==null&&np.Address==null);
var invsvc=new InvoiceService();Invoice Invoice(long p,string no)=>new(){Kind=InvoiceKind.Out,InvoiceType="普通",InvoiceNo=no,PartnerId=p,RegDate="2024-02-01",GrossCents=11300,NetCents=10001,TaxCents=1299,RateBp=1300};
var invoiceId=invsvc.Save(Invoice(customer.Id,"QUICK-1"));Check("invoice stores returned ID and manual tax",invsvc.Get(invoiceId)!.PartnerId==result.Id&&invsvc.Get(invoiceId)!.TaxCents==1299);
AppSession.Db=new Database(args[0]);AppSession.Db.EnsureCreated();Check("reopen same database retains customer",MasterDataService.ListPartners("customer",false).Any(p=>p.Id==result.Id));
before=InvCount();var savedThenCancel=Create("取消发票仍保留");Check("customer save without invoice persists",MasterDataService.GetPartner(savedThenCancel.Id!.Value)!=null&&InvCount()==before);
var savedThenFail=Create("发票失败仍保留");Reject("duplicate invoice rejected",()=>invsvc.Save(Invoice(savedThenFail.Id!.Value,"QUICK-1")));Check("invoice failure preserves customer, adds no invoice",InvCount()==before&&MasterDataService.GetPartner(savedThenFail.Id!.Value)!=null);
PeriodService.Close("202402");var closed=Create("关账后客户仍可保存");Reject("closed period invoice rejected",()=>invsvc.Save(Invoice(closed.Id!.Value,"CLOSED")));Check("closed period leaves no partial invoice",InvCount()==before&&MasterDataService.GetPartner(closed.Id!.Value)!=null);PeriodService.Reopen("202402","测试重开");
var single=Create("测试客户");Check("single same-name needs explicit decision",single.Outcome==InvoiceCustomerOutcome.ReviewRequired&&single.Id==null&&single.Candidates.Count==1);
var selected=MasterDataService.SelectExistingInvoiceCustomer("测试客户",customer.Id,single.Snapshot);Check("explicit selection returns exact ID",selected.Outcome==InvoiceCustomerOutcome.Selected&&selected.Id==customer.Id);
var duplicate=Create("测试客户",single.Snapshot);Check("confirmed same-name distinct customer allowed",duplicate.Outcome==InvoiceCustomerOutcome.Created&&duplicate.Id!=customer.Id);
before=Count();var multi=Create("测试客户");Check("multiple same-names never guessed",multi.Candidates.Count==2&&multi.Id==null);
var selection2=MasterDataService.SelectExistingInvoiceCustomer("测试客户",duplicate.Id!.Value,multi.Snapshot);Check("choose specific duplicate by ID",selection2.Id==duplicate.Id&&Count()==before);
Check("existing contact is not overwritten",MasterDataService.GetPartner(customer.Id)!.Contact=="联系人");
var ascii=Seed("ASCII","Acme  Ltd");Sql("UPDATE partners SET name='  Acme  Ltd  ' WHERE id="+ascii);
var caseMatch=Create(" acme  ltd ");Check("historical trim and ordinal ignore case match",caseMatch.Candidates.Single().Id==ascii);
Check("internal whitespace not merged",Create("Acme Ltd").Outcome==InvoiceCustomerOutcome.Created);
foreach(var (kind,active) in new[]{("customer",false),("supplier",true),("both",true)}){
 var id=Seed("SPECIAL-"+kind, "同名-"+kind,kind,active);var review=Create("同名-"+kind);Check("candidate identity/status included",review.Candidates.Single().Id==id&&review.Candidates.Single().Kind==kind&&review.Candidates.Single().IsActive==active);
 if(kind=="both")Check("active both selectable",MasterDataService.SelectExistingInvoiceCustomer("同名-"+kind,id,review.Snapshot).Id==id);
 else Reject("inactive/supplier cannot silently select",()=>MasterDataService.SelectExistingInvoiceCustomer("同名-"+kind,id,review.Snapshot));
 var newOne=Create("同名-"+kind,review.Snapshot);Check("distinct new customer preserves old identity",newOne.Id!=id&&MasterDataService.GetPartner(id)!.Kind==kind&&MasterDataService.GetPartner(id)!.IsActive==active&&MasterDataService.GetPartner(id)!.Phone=="原电话");
}
var stale=Create("测试客户");Seed("NEW-CANDIDATE","测试客户");before=Count();var changed=Create("测试客户",stale.Snapshot);Check("changed candidate set requires reconfirmation",changed.Outcome==InvoiceCustomerOutcome.ReviewRequired&&Count()==before&&changed.Snapshot!=stale.Snapshot);
var pchanged=MasterDataService.GetPartner(changed.Candidates[0].Id)!;pchanged.Phone="新电话";MasterDataService.SavePartner(pchanged);Check("changed contact also invalidates review",Create("测试客户",changed.Snapshot).Outcome==InvoiceCustomerOutcome.ReviewRequired);
Sql("CREATE TRIGGER fail_partner_audit BEFORE INSERT ON audit_log WHEN NEW.entity='partner' BEGIN SELECT RAISE(ABORT,'injected audit failure'); END;");
before=Count();try{Create("审计失败");Check("audit failure reported",false);}catch(SqliteException){Check("audit failure reported",true);}Check("audit failure rolls back customer",Count()==before);Sql("DROP TRIGGER fail_partner_audit;");
Sql("CREATE TRIGGER fail_customer BEFORE INSERT ON partners WHEN NEW.name='数据库失败' BEGIN SELECT RAISE(ABORT,'injected database failure'); END;");
before=Count();try{Create("数据库失败");Check("database failure not hidden",false);}catch(SqliteException ex){Check("database failure not disguised as code duplicate",ex.Message.Contains("injected database failure"));}Check("database failure no partial customer",Count()==before);Sql("DROP TRIGGER fail_customer;");
Sql("CREATE TRIGGER force_code_conflict BEFORE INSERT ON partners WHEN NEW.name='编码冲突' BEGIN INSERT INTO partners(code,name,kind) VALUES(NEW.code,'trigger fixture','customer'); END;");before=Count();Reject("code collision has bounded retries",()=>Create("编码冲突"));Check("exhausted code collision rolls back",Count()==before);Sql("DROP TRIGGER force_code_conflict;");
Sql("CREATE TRIGGER audit_code_fault BEFORE INSERT ON audit_log WHEN NEW.entity='partner' BEGIN INSERT INTO partners(code,name,kind) VALUES('KH-0001','audit duplicate','customer'); END;");
before=Count();try{Create("审计编码异常");Check("audit code failure reported without retry",false);}catch(SqliteException){Check("audit code failure reported without retry",true);}Check("audit code failure rolls back all customer writes",Count()==before);Sql("DROP TRIGGER audit_code_fault;");
var parallel=await Task.WhenAll(Enumerable.Range(0,8).Select(i=>Task.Run(()=>Create("并发不同客户"+i))));Check("parallel distinct names unique real IDs/codes",parallel.All(r=>r.Outcome==InvoiceCustomerOutcome.Created)&&parallel.Select(r=>r.Id).Distinct().Count()==8&&parallel.Select(r=>r.Code).Distinct().Count()==8);
parallel=await Task.WhenAll(Enumerable.Range(0,8).Select(i=>Task.Run(()=>Create("重复点击同一客户"))));Check("concurrent same-name submission creates only one",parallel.Count(r=>r.Outcome==InvoiceCustomerOutcome.Created)==1&&parallel.Count(r=>r.Outcome==InvoiceCustomerOutcome.ReviewRequired)==7);
foreach(var role in new[]{UserRole.Admin,UserRole.Accountant,UserRole.Cashier,UserRole.Viewer}){
 AppSession.Current=admin;var auth=new AuthService();var username="role_"+role;
 auth.Create(username,role.ToString(),"role-test-4821",role);auth.Login(username,"role-test-4821");
 if(role is UserRole.Admin or UserRole.Accountant)Check("allowed role "+role,Create("角色"+role).Outcome==InvoiceCustomerOutcome.Created);else{before=Count();Reject("service role restriction "+role,()=>Create("角色"+role));Reject("SavePartner permission retained "+role,()=>Seed("BLOCK"+role,"blocked"));Check("permission failure writes nothing",Count()==before);}
}AppSession.Current=admin;
Check("quick creation leaves existing funds unchanged",VoucherService.TotalFunds()==initialFunds);
Check("existing invoice manual values unchanged",invsvc.Get(invoiceId)!.GrossCents==11300&&invsvc.Get(invoiceId)!.NetCents==10001&&invsvc.Get(invoiceId)!.TaxCents==1299);
Check("database is current version",AppSession.Db.GetUserVersion()==Database.CurrentVersion);
using(var c=AppSession.Db.Open()){Check("foreign keys intact",new SqlCmd(c,"SELECT COUNT(*) FROM pragma_foreign_key_check").Long()==0);Check("no global name unique constraint introduced",new SqlCmd(c,"SELECT COUNT(*) FROM partners WHERE name='测试客户'").Long()==3);}
Console.WriteLine($"RESULT checks={checks} failures={failures}");return failures==0?0:1;



