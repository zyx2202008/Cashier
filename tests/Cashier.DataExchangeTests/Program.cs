using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cashier.Core;
using Cashier.Core.Services;
using ClosedXML.Excel;
using Microsoft.Data.Sqlite;

if(args.Length!=1||Directory.Exists(args[0]))throw new Exception("Fresh isolated test root required");
var root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
int checks=0,failures=0;
void Check(string name,bool ok){checks++;if(!ok)failures++;Console.WriteLine($"{(ok?"PASS":"FAIL")} | {name}");}
void Denied(string name,Action action){try{action();Check(name,false);}catch(Exception ex) when(ex is AppException or IOException or InvalidDataException or SqliteException){Check(name,true);}}
Database New(string name){var db=new Database(Path.Combine(root,name));db.EnsureCreated();AppSession.Db=db;new AuthService().Login("admin","admin888");return db;}
long Scalar(string sql){using var c=AppSession.Db.Open();return new SqlCmd(c,sql).Long();}
string Snapshot(bool excludeMeta=false)
{
    using var c=AppSession.Db.Open();var tables=new SqlCmd(c,"SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name").List(r=>r.GetString(0));
    return JsonSerializer.Serialize(tables.Where(t=>!excludeMeta||t!="meta").Select(t=>new{table=t,rows=new SqlCmd(c,"SELECT * FROM "+t+" ORDER BY rowid").List(r=>Enumerable.Range(0,r.FieldCount).Select(i=>r.IsDBNull(i)?null:r.GetValue(i)).ToArray())}));
}
void Edit(string path,Action<IXLWorksheet> action){using var w=new XLWorkbook(path);action(w.Worksheet("资料"));w.Save();}
int FindRow(IXLWorksheet sheet,MasterExchangeKind kind,string name)=>sheet.RowsUsed().Skip(1).Single(r=>r.Cell(kind==MasterExchangeKind.Handler?2:3).GetString()==name).RowNumber();
var db=New("masters");
var account=MasterDataService.SaveAccount(new(){Code="000017",Name="原始Account",BankTail="0012",OpeningCents=123456});
var partner=MasterDataService.SavePartner(new(){Name="原始Partner",Kind="customer",Phone="0013800",OpeningReceivableCents=-5000});
MasterDataService.SaveSubject(new(){Code="SUB-17",Name="原始Subject",Direction="in"});
var dept=MasterDataService.SaveDept(new(){Code="DP-0007",Name="原始Dept"});
MasterDataService.SaveProject(new(){Code="PR-17",Name="原始Project",DeptId=dept,Owner="负责人"});
MasterDataService.SaveHandler(new(){Name="原始Handler"});
foreach(var kind in Enum.GetValues<MasterExchangeKind>())
{
    var path=Path.Combine(root,kind+"-edit.xlsx");MasterDataExchangeService.Export(kind,path);
    var before=Snapshot();
    Edit(path,s=>{var row=FindRow(s,kind,"原始"+kind);s.Cell(row,kind==MasterExchangeKind.Handler?2:3).Value="修改后"+kind;});
    var preview=MasterDataExchangeService.Preview(kind,path);
    Check(kind+" preview one change",preview.ErrorCount==0&&preview.ChangeCount==1);
    Check(kind+" preview does not persist rows or audit",before==Snapshot());
    Check(kind+" commit one change",MasterDataExchangeService.Commit(preview)==1);
    Check(kind+" repeated preview skips unchanged",MasterDataExchangeService.Preview(kind,path).ChangeCount==0);
    Denied(kind+" repeated same object denied",()=>MasterDataExchangeService.Commit(preview));
}
Check("leading zero account code retained",MasterDataService.ListAccounts().Single(x=>x.Id==account).Code=="000017");
Check("leading zero phone retained",MasterDataService.GetPartner(partner)!.Phone=="0013800");
using(var w=new XLWorkbook(Path.Combine(root,"Partner-edit.xlsx")))Check("Excel text format protects phone leading zeros",w.Worksheet("资料").Column(6).Style.NumberFormat.Format=="@");
var newValues=new Dictionary<MasterExchangeKind,string[]>{
    [MasterExchangeKind.Account]=["NEW-0001","新账户","bank","0019","1002","0.01","0"],
    [MasterExchangeKind.Partner]=["","新客户","customer","联系人","00123","地址","月结30天","A","0.00","0.00","1"],
    [MasterExchangeKind.Subject]=["NEW-SUB","新科目","in","1"],
    [MasterExchangeKind.Dept]=["NEW-DEPT","新部门","1"],
    [MasterExchangeKind.Project]=["NEW-PROJ","新项目","DP-0007","项目负责人","1"],
    [MasterExchangeKind.Handler]=["新经办人","1"]};
foreach(var kind in Enum.GetValues<MasterExchangeKind>())
{
    var path=Path.Combine(root,kind+"-new.xlsx");MasterDataExchangeService.Export(kind,path,true);
    Edit(path,s=>{for(int i=0;i<newValues[kind].Length;i++)s.Cell(2,i+2).Value=newValues[kind][i];});
    var preview=MasterDataExchangeService.Preview(kind,path);Check(kind+" new row valid",preview.ErrorCount==0&&preview.ChangeCount==1);
    if(preview.ErrorCount>0)Console.WriteLine(JsonSerializer.Serialize(preview.Rows));
    Check(kind+" new commit",MasterDataExchangeService.Commit(preview)==1);
    var repeat=MasterDataExchangeService.Preview(kind,path);Check(kind+" reimport deduplicates",repeat.ErrorCount==0&&repeat.ChangeCount==0&&repeat.SkipCount==1);
}
Check("new inactive account retained",!MasterDataService.ListAccounts().Single(x=>x.Code=="NEW-0001").IsActive);
Check("integer cents preserved",MasterDataService.ListAccounts().Single(x=>x.Code=="NEW-0001").OpeningCents==1);
Check("project department mapped by code",MasterDataService.ListProjects().Single(x=>x.Code=="NEW-PROJ").DeptId==dept);
var mixed=Path.Combine(root,"mixed.xlsx");MasterDataExchangeService.Export(MasterExchangeKind.Dept,mixed,true);
Edit(mixed,s=>{s.Cell(2,2).Value="VALID-NEW";s.Cell(2,3).Value="可导入部门";s.Cell(2,4).Value="1";s.Cell(3,2).Value="BAD-NEW";s.Cell(3,4).Value="1";});
var mixedBefore=Snapshot();var mixedPreview=MasterDataExchangeService.Preview(MasterExchangeKind.Dept,mixed);
Check("mixed batch marks invalid row",mixedPreview.ErrorCount==1&&mixedPreview.ChangeCount==1);Check("mixed preview leaves no additions",Snapshot()==mixedBefore);
Denied("mixed batch entirely blocked",()=>MasterDataExchangeService.Commit(mixedPreview));Check("blocked commit unchanged",Snapshot()==mixedBefore);
MasterDataExchangeService.ExportValidation(mixedPreview,Path.Combine(root,"validation.xlsx"));Check("validation export exists",File.Exists(Path.Combine(root,"validation.xlsx")));
var stale=Path.Combine(root,"stale.xlsx");MasterDataExchangeService.Export(MasterExchangeKind.Account,stale);
Edit(stale,s=>s.Cell(FindRow(s,MasterExchangeKind.Account,"修改后Account"),3).Value="Excel变更");
var stalePreview=MasterDataExchangeService.Preview(MasterExchangeKind.Account,stale);
var acc=MasterDataService.ListAccounts().Single(x=>x.Id==account);acc.Name="其他窗口变更";MasterDataService.SaveAccount(acc);
var staleBefore=Snapshot();Denied("stale export cannot overwrite latest data",()=>MasterDataExchangeService.Commit(stalePreview));Check("stale rejection leaves all rows unchanged",Snapshot()==staleBefore);
var duplicateId=Path.Combine(root,"duplicate-id.xlsx");MasterDataExchangeService.Export(MasterExchangeKind.Handler,duplicateId);
Edit(duplicateId,s=>s.Row(2).CopyTo(s.Row((s.LastRowUsed()?.RowNumber()??1)+1)));
Check("duplicate record IDs detected",MasterDataExchangeService.Preview(MasterExchangeKind.Handler,duplicateId).ErrorCount==1);
var foreign=Path.Combine(root,"foreign.xlsx");MasterDataExchangeService.Export(MasterExchangeKind.Account,foreign);
New("foreign-target");Check("foreign existing rows rejected",MasterDataExchangeService.Preview(MasterExchangeKind.Account,foreign).ErrorCount>0);AppSession.Db=db;new AuthService().Login("admin","admin888");
var formula=Path.Combine(root,"formula.xlsx");MasterDataExchangeService.Export(MasterExchangeKind.Dept,formula,true);
Edit(formula,s=>{s.Cell(2,2).Value="FORMULA";s.Cell(2,3).FormulaA1="1+1";s.Cell(2,4).Value="1";});
Check("formulas rejected",MasterDataExchangeService.Preview(MasterExchangeKind.Dept,formula).ErrorCount==1);
Edit(formula,s=>s.Cell(2,3).FormulaA1="UNSUPPORTED_CASHIER_FUNCTION()");
Check("unsupported formulas rejected by row without evaluation",MasterDataExchangeService.Preview(MasterExchangeKind.Dept,formula).Rows.Single().Error?.Contains("不支持公式")==true);
OpeningBalanceService.Confirm(SqlUtil.Today(),"Excel安全测试锁定");
var locked=Path.Combine(root,"locked.xlsx");MasterDataExchangeService.Export(MasterExchangeKind.Account,locked);
Edit(locked,s=>s.Cell(FindRow(s,MasterExchangeKind.Account,"其他窗口变更"),7).Value="999.00");
Check("Excel cannot change locked opening",MasterDataExchangeService.Preview(MasterExchangeKind.Account,locked).ErrorCount==1);
var lockAfterPreview=New("lock-after-preview");var la=MasterDataService.SaveAccount(new(){Code="LATER-LOCK",Name="稍后锁定",OpeningCents=100});
var laterFile=Path.Combine(root,"lock-after-preview.xlsx");MasterDataExchangeService.Export(MasterExchangeKind.Account,laterFile);
Edit(laterFile,s=>s.Cell(FindRow(s,MasterExchangeKind.Account,"稍后锁定"),7).Value="2.00");var laterPreview=MasterDataExchangeService.Preview(MasterExchangeKind.Account,laterFile);
OpeningBalanceService.Confirm(SqlUtil.Today(),"预览后锁定");Denied("commit rechecks lock changed after preview",()=>MasterDataExchangeService.Commit(laterPreview));
Check("failed commit keeps old opening",MasterDataService.ListAccounts().Single(x=>x.Id==la).OpeningCents==100);
var fault=Path.Combine(root,"audit-fault.xlsx");MasterDataExchangeService.Export(MasterExchangeKind.Dept,fault,true);
Edit(fault,s=>{s.Cell(2,2).Value="FAULT";s.Cell(2,3).Value="回滚测试";s.Cell(2,4).Value="1";});var faultPreview=MasterDataExchangeService.Preview(MasterExchangeKind.Dept,fault);
using(var c=AppSession.Db.Open())new SqlCmd(c,"CREATE TRIGGER qa_import_audit BEFORE INSERT ON audit_log WHEN NEW.action='master-import' BEGIN SELECT RAISE(ABORT,'QA injected audit failure'); END").Exec();
var faultBefore=Snapshot();Denied("audit failure rolls back whole master batch",()=>MasterDataExchangeService.Commit(faultPreview));Check("audit failure leaves all rows unchanged",faultBefore==Snapshot());
using(var c=AppSession.Db.Open())new SqlCmd(c,"DROP TRIGGER qa_import_audit").Exec();
var source=New("package-source");
var bank=MasterDataService.SaveAccount(new(){Code="MIG-0001",Name="迁移资金",OpeningCents=100000});var customer=MasterDataService.SavePartner(new(){Name="迁移客户",Kind="customer",OpeningReceivableCents=20000});
var date=DateTime.Today.AddMonths(-1).ToString("yyyy-MM")+"-05";
var svc=new VoucherService();long Post(Voucher v){var id=svc.Create(v);svc.Audit(id);return id;}
var recv=Post(new(){Kind=VoucherKind.Receivable,BizDate=date,PartnerId=customer,AmountCents=20000,Summary="迁移应收",CountAsRevenue=true});
var cash=Post(new(){Kind=VoucherKind.Income,Nature=VoucherNature.CustomerReceipt,BizDate=date,PartnerId=customer,AccountId=bank,AmountCents=10000,Summary="迁移回款"});
new SettlementService().Allocate(cash,recv,5000);
var invoice=new InvoiceService();var inv=invoice.Save(new(){Kind=InvoiceKind.Out,InvoiceType="普通",InvoiceNo="000000123456",InvoiceCode="000012",PartnerId=customer,RegDate=date,GrossCents=10600,NetCents=10000,TaxCents=600,RateBp=600});invoice.Link(inv,recv,5000);
OpeningBalanceService.Confirm(DateTime.Today.AddMonths(-1).ToString("yyyy-MM")+"-01","迁移测试确认");OpeningBalanceService.Create("account",bank,SqlUtil.Today(),1234,"迁移调整","migration-adjust");
PeriodService.Close(SqlUtil.PeriodOf(date));new AuthService().Create("migrated_accountant","迁移会计","testing123",UserRole.Accountant);
var portableExcel=Path.Combine(root,"portable-account.xlsx");MasterDataExchangeService.Export(MasterExchangeKind.Account,portableExcel);
var package=Path.Combine(root,"complete.cashier");var manifest=source.ExportDataPackage(package);var previewPackage=source.PreviewDataPackage(package);
Check("manifest contains all data categories",manifest.TableCounts.Count==20&&manifest.TableCounts["vouchers"]==2&&manifest.TableCounts["balance_adjustments"]==1);
Check("export preview hash consistent",manifest.FileSha256==previewPackage.FileSha256);
var sourceBefore=Snapshot();var beforeFunds=VoucherService.TotalFunds();var beforePartner=MasterDataService.GetPartner(customer)!.NetReceivableCents;
var target=Path.Combine(root,"migrated");source.ImportDataPackage(package,target,previewPackage.FileSha256);
Check("native import does not change source",Snapshot()==sourceBefore);Check("current session remains source",AppSession.Db.FilePath==source.FilePath&&AppSession.Current!=null);
var imported=new Database(target);AppSession.Db=imported;Denied("old session cannot operate new ledger",()=>OpeningBalanceService.State());new AuthService().Login("admin","admin888");
Check("migrated funds exact",beforeFunds==VoucherService.TotalFunds());Check("migrated partner balance exact",beforePartner==MasterDataService.GetPartner(customer)!.NetReceivableCents);
Check("migrated relationships preserved",Scalar("SELECT COUNT(*) FROM settlements")==1&&Scalar("SELECT COUNT(*) FROM invoice_links")==1);
Check("migrated opening locked",OpeningBalanceService.State().IsLocked);Check("migrated closed period retained",PeriodService.IsClosed(SqlUtil.PeriodOf(date)));
Check("migrated original invoice zeros retained",invoice.Get(inv)!.InvoiceNo=="000000123456"&&invoice.Get(inv)!.InvoiceCode=="000012");
Check("imported accounts usable",new AuthService().Login("migrated_accountant","testing123").Role==UserRole.Accountant);
new AuthService().Login("admin","admin888");Check("migrated integrity",Scalar("SELECT COUNT(*) FROM pragma_foreign_key_check")==0);
Check("Excel identity portable with migrated ledger",MasterDataExchangeService.Preview(MasterExchangeKind.Account,portableExcel).ErrorCount==0);
AppSession.Db=source;new AuthService().Login("admin","admin888");
Denied("existing ledger not overwritten",()=>source.ImportDataPackage(package,target,previewPackage.FileSha256));
Denied("current ledger not overwritten",()=>source.ImportDataPackage(package,source.DataDir,previewPackage.FileSha256));
Denied("nested current directory rejected",()=>source.ImportDataPackage(package,Path.Combine(source.DataDir,"nested"),previewPackage.FileSha256));
var nonempty=Path.Combine(root,"nonempty");Directory.CreateDirectory(nonempty);File.WriteAllText(Path.Combine(nonempty,"keep.txt"),"keep");
Denied("nonempty directory not overwritten",()=>source.ImportDataPackage(package,nonempty,previewPackage.FileSha256));Check("existing file retained",File.ReadAllText(Path.Combine(nonempty,"keep.txt"))=="keep");
var empty=Path.Combine(root,"empty-target");Directory.CreateDirectory(empty);source.ImportDataPackage(package,empty,previewPackage.FileSha256);Check("empty selected directory supported",File.Exists(Path.Combine(empty,"cashier.db")));
var corrupt=Path.Combine(root,"corrupt.cashier");File.Copy(package,corrupt);
using(var zip=ZipFile.Open(corrupt,ZipArchiveMode.Update)){using var stream=zip.GetEntry("manifest.json")!.Open();using var reader=new StreamReader(stream,leaveOpen:true);var json=reader.ReadToEnd();stream.SetLength(0);stream.Position=0;using var writer=new StreamWriter(stream);writer.Write(json.Replace("CashierLedger","InvalidFormat"));}
var beforeCorrupt=Snapshot();Denied("invalid package rejected",()=>source.PreviewDataPackage(corrupt));Check("failed preview leaves source unchanged",Snapshot()==beforeCorrupt);
var badDigest=Path.Combine(root,"digest.cashier");File.Copy(package,badDigest);
using(var zip=ZipFile.Open(badDigest,ZipArchiveMode.Update)){using var stream=zip.GetEntry("ledger.db")!.Open();var bytes=new byte[stream.Length];stream.ReadExactly(bytes);bytes[^1]^=1;stream.Position=0;stream.Write(bytes);}
Denied("modified database bytes rejected by checksum",()=>source.PreviewDataPackage(badDigest));
var extra=Path.Combine(root,"extra.cashier");File.Copy(package,extra);using(var zip=ZipFile.Open(extra,ZipArchiveMode.Update)){using var writer=new StreamWriter(zip.CreateEntry("../escape.txt").Open());writer.Write("invalid");}
Denied("extra and traversal entries rejected",()=>source.PreviewDataPackage(extra));Check("no escaped file created",!File.Exists(Path.Combine(root,"escape.txt")));
Denied("changed file hash rejects import",()=>source.ImportDataPackage(package,Path.Combine(root,"hash-invalid"),new string('0',64)));
var raceTarget=Path.Combine(root,"race-import");var race=new ConcurrentBag<bool>();Parallel.For(0,2,_=>{try{source.ImportDataPackage(package,raceTarget,previewPackage.FileSha256);race.Add(true);}catch(Exception e) when(e is IOException or AppException){race.Add(false);}});
Check("concurrent imports never overwrite each other",race.Count(x=>x)==1&&File.Exists(Path.Combine(raceTarget,"cashier.db")));
Check("no native preparation leftovers",!Directory.EnumerateFiles(source.DataDir,".package-check-*").Any()&&!Directory.EnumerateDirectories(root,".cashier-import-*").Any());
var auth=new AuthService();
foreach(var role in new[]{UserRole.Accountant,UserRole.Cashier,UserRole.Viewer})
{
    auth.Login("admin","admin888");var user="role_"+role;auth.Create(user,user,"testing123",role);auth.Login(user,"testing123");
    if(role==UserRole.Accountant)Check("accountant may export ledger",File.Exists((source.ExportDataPackage(Path.Combine(root,"accountant.cashier")),Path.Combine(root,"accountant.cashier")).Item2));
    else{Denied(role+" cannot export ledger",()=>source.ExportDataPackage(Path.Combine(root,role+".cashier")));Denied(role+" cannot export master data",()=>MasterDataExchangeService.Export(MasterExchangeKind.Account,Path.Combine(root,role+".xlsx")));}
    Denied(role+" cannot import whole ledger",()=>source.ImportDataPackage(package,Path.Combine(root,"denied-"+role),previewPackage.FileSha256));
}
auth.Login("admin","admin888");
var bulk=New("bulk");var bulkFile=Path.Combine(root,"bulk.xlsx");MasterDataExchangeService.Export(MasterExchangeKind.Dept,bulkFile,true);
Edit(bulkFile,s=>{for(int i=0;i<1000;i++){s.Cell(i+2,2).Value="BULK-"+i.ToString("D5");s.Cell(i+2,3).Value="批量部门"+i;s.Cell(i+2,4).Value="1";}});
var timer=System.Diagnostics.Stopwatch.StartNew();var bulkPreview=MasterDataExchangeService.Preview(MasterExchangeKind.Dept,bulkFile);
Check("1000 row preview valid",bulkPreview.ErrorCount==0&&bulkPreview.ChangeCount==1000);
Check("1000 row preview leaves no rows",Scalar("SELECT COUNT(*) FROM depts WHERE code LIKE 'BULK-%'")==0);
Check("1000 row commit complete",MasterDataExchangeService.Commit(bulkPreview)==1000);timer.Stop();
Check("1000 unique codes retained",Scalar("SELECT COUNT(DISTINCT code) FROM depts WHERE code LIKE 'BULK-%'")==1000);
Console.WriteLine("BULK_1000_PREVIEW_COMMIT_SECONDS="+timer.Elapsed.TotalSeconds.ToString("F3",System.Globalization.CultureInfo.InvariantCulture));
Console.WriteLine($"RESULT checks={checks} failures={failures}");File.WriteAllText(Path.Combine(root,"summary.json"),JsonSerializer.Serialize(new{checks,failures},new JsonSerializerOptions{WriteIndented=true}));
return failures==0?0:1;
