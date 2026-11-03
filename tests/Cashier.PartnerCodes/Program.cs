using Cashier.Core;
using Cashier.Core.Services;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Reflection;

if (args.Length == 3 && args[0] == "--worker")
{
    AppSession.Db = new Database(args[1]);
    AppSession.Current = new AuthService().Login("admin", "admin888");
    for (var i = 0; i < 10; i++)
        MasterDataService.SavePartner(new Partner { Name = $"进程{args[2]}-{i}", Kind = i % 2 == 0 ? "customer" : "supplier" });
    return 0;
}
if (args.Length != 1 || Directory.Exists(args[0])) throw new Exception("Provide a fresh isolated test directory.");
var root = Path.GetFullPath(args[0]);
Directory.CreateDirectory(root);
int checks = 0, failures = 0;
void Check(string name, bool condition)
{
    checks++;
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")} | {name}");
    if (!condition) failures++;
}
void Sql(Database db, string text)
{
    using var conn = db.Open();
    new SqlCmd(conn, text).Exec();
}
long Scalar(Database db, string text)
{
    using var conn = db.Open();
    return new SqlCmd(conn, text).Long();
}
string? Text(Database db, string text)
{
    using var conn = db.Open();
    return new SqlCmd(conn, text).Str();
}
Database Fresh(string name)
{
    var db = new Database(Path.Combine(root, name));
    db.EnsureCreated();
    AppSession.Db = db;
    AppSession.Current = new AuthService().Login("admin", "admin888");
    return db;
}
Database Legacy(string name)
{
    var db = new Database(Path.Combine(root, name));
    using var conn = db.Open();
    Schema.CreateV1(conn);
    Schema.Seed(conn);
    new SqlCmd(conn, "PRAGMA user_version=1").Exec();
    Schema.Migrate(conn, 1, 2);
    return db;
}
long Add(string name, string kind, string supplied = "") => MasterDataService.SavePartner(new Partner { Name = name, Kind = kind, Code = supplied });
string Code(long id) => MasterDataService.GetPartner(id)!.Code;

var db = Fresh("new");
var c1 = Add("客户1", "customer", "MANUAL");
var c2 = Add("客户2", "customer");
var s1 = Add("供应商1", "supplier", "KH-9999");
var s2 = Add("供应商2", "supplier");
Check("first two customers and suppliers use independent sequences", Code(c1) == "KH-0001" && Code(c2) == "KH-0002" && Code(s1) == "GYS-0001" && Code(s2) == "GYS-0002");
MasterDataService.DeletePartner(c2);
var c3 = Add("客户3", "customer");
var s3 = Add("供应商3", "supplier");
Check("deleted numbers are not reused and mixed creates remain independent", Code(c3) == "KH-0003" && Code(s3) == "GYS-0003");
var quick = MasterDataService.CreateCustomerForInvoice(new("快捷客户"));
Check("invoice quick-create shares the customer sequence", quick.Code == "KH-0004");
var both = Add("兼有单位", "both");
Check("dual role shares the customer sequence", Code(both) == "KH-0005");
AppSession.Db = new Database(db.DataDir);
AppSession.Db.EnsureCreated();
Check("reopen retains customer and supplier counters", Code(Add("重启客户", "customer")) == "KH-0006" && Code(Add("重启供应商", "supplier")) == "GYS-0004");
var edit = MasterDataService.GetPartner(c1)!;
edit.Code = "GYS-9999";
edit.Name = "修改客户";
MasterDataService.SavePartner(edit);
Check("manual code changes are ignored on edit", Code(c1) == "KH-0001");
edit.Kind = "supplier";
MasterDataService.SavePartner(edit);
Check("type change allocates supplier code and preserves ID", Code(c1) == "GYS-0005" && MasterDataService.GetPartner(c1)!.Kind == "supplier");
edit.Kind = "both";
MasterDataService.SavePartner(edit);
Check("supplier to dual role uses a new customer code", Code(c1) == "KH-0007");
edit.Kind = "customer";
MasterDataService.SavePartner(edit);
Check("dual role to customer retains its code", Code(c1) == "KH-0007");

Sql(db, "CREATE TRIGGER fail_insert BEFORE INSERT ON partners WHEN NEW.name='fail' BEGIN SELECT RAISE(ABORT,'insert failure'); END");
try { Add("fail", "customer"); Check("insert failure surfaced", false); } catch (SqliteException) { Check("insert failure surfaced", true); }
Sql(db, "DROP TRIGGER fail_insert");
Check("failed insert does not consume a number", Code(Add("insert recovery", "customer")) == "KH-0008");
Sql(db, "CREATE TRIGGER fail_audit BEFORE INSERT ON audit_log WHEN NEW.entity='partner' BEGIN SELECT RAISE(ABORT,'audit failure'); END");
try { Add("audit fail", "supplier"); Check("audit failure surfaced", false); } catch (SqliteException) { Check("audit failure surfaced", true); }
edit = MasterDataService.GetPartner(c1)!; edit.Kind = "supplier";
try { MasterDataService.SavePartner(edit); Check("type-change audit failure surfaced", false); } catch (SqliteException) { Check("type-change audit failure surfaced", true); }
Sql(db, "DROP TRIGGER fail_audit");
Check("failed audit rolls back partner, type and counter", Code(c1) == "KH-0007" && Code(Add("audit recovery", "supplier")) == "GYS-0006");

var parallelDb = Fresh("parallel");
var created = await Task.WhenAll(Enumerable.Range(0, 48).Select(i => Task.Run(() =>
{
    var kind = i % 2 == 0 ? "customer" : "supplier";
    var id = Add("并发" + i, kind);
    return Code(id);
})));
Check("parallel inserts produce distinct codes", created.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 48);
Check("parallel sequences are complete and independent", Enumerable.Range(1, 24).All(i => created.Contains($"KH-{i:D4}") && created.Contains($"GYS-{i:D4}")));
Check("foreign keys intact in new database", Scalar(parallelDb, "SELECT COUNT(*) FROM pragma_foreign_key_check") == 0);

var multiProcess = Fresh("multi-process");
var workers = Enumerable.Range(0, 4).Select(i =>
{
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add("--worker"); start.ArgumentList.Add(multiProcess.DataDir); start.ArgumentList.Add(i.ToString());
    return Process.Start(start)!;
}).ToArray();
await Task.WhenAll(workers.Select(p => p.WaitForExitAsync()));
Check("multiple processes write without collision", workers.All(p => p.ExitCode == 0) && Scalar(multiProcess, "SELECT COUNT(DISTINCT code) FROM partners") == 40);
Check("multiple processes keep both sequences independent", Scalar(multiProcess, "SELECT COUNT(*) FROM partners WHERE kind='customer' AND code LIKE 'KH-%'") == 20 && Scalar(multiProcess, "SELECT COUNT(*) FROM partners WHERE kind='supplier' AND code LIKE 'GYS-%'") == 20 && Text(multiProcess, "SELECT v FROM meta WHERE k='partner_code_sequence_v3.KH'") == "20" && Text(multiProcess, "SELECT v FROM meta WHERE k='partner_code_sequence_v3.GYS'") == "20");
foreach (var p in workers) p.Dispose();
Sql(multiProcess, "UPDATE meta SET v='9999' WHERE k='partner_code_sequence_v3.KH'");
Check("sequence expands beyond four digits", Code(Add("第五位序号", "customer")) == "KH-10000");

var legacy = Legacy("legacy");
Sql(legacy, """
    INSERT INTO partners(id,code,name,kind) VALUES
      (10,'KH-0012','历史客户','customer'),(11,'GYS-0001','保留客户','customer'),
      (20,'KH-0002','旧供应商甲','supplier'),(21,'GY-1000','旧供应商乙','supplier'),
      (22,'GYS-0003','已有标准供应商','supplier'),(23,'gys-0001','大小写冲突供应商','supplier'),
      (30,'LEGACY-BOTH','兼有单位','both');
    INSERT INTO audit_log(ts,username,action,entity,entity_id,doc_no) VALUES
      ('2024-01-01','历史用户','create','partner',90,'KH-0020'),
      ('2024-01-01','历史用户','create','partner',91,'GYS-0008');
    INSERT INTO vouchers(doc_no,kind,nature,biz_date,period,amount_cents,partner_id,summary,status,created_at,created_by)
      VALUES('V-OLD','payable','PurchaseCash','2024-01-01','202401',10000,20,'旧单据','draft','2024-01-01','历史用户');
    INSERT INTO invoices(kind,invoice_type,invoice_no,partner_id,reg_date,gross_cents,net_cents,tax_cents,rate_bp,status,created_at,created_by)
      VALUES('in','普通','OLD-INVOICE',20,'2024-01-01',11300,10000,1300,1300,'valid','2024-01-01','历史用户');
    """);
legacy.EnsureCreated();
AppSession.Db = legacy;
new AuthService().Login("admin", "admin888");
Check("legacy database migrated to version 3 and backed up", legacy.GetUserVersion() == Database.CurrentVersion && Directory.GetFiles(legacy.BackupDir, $"*preupgrade-v2-to-v{Database.CurrentVersion}.db").Length == 1);
Check("legacy customers and dual role unchanged", Code(10) == "KH-0012" && Code(11) == "GYS-0001" && Code(30) == "LEGACY-BOTH");
Check("supplier migration preserves canonical code and avoids conflicts", Code(20) == "GYS-0009" && Code(21) == "GYS-0010" && Code(22) == "GYS-0003" && Code(23) == "GYS-0011");
Check("historical voucher and invoice keep supplier ID", Scalar(legacy, "SELECT COUNT(*) FROM vouchers WHERE doc_no='V-OLD' AND partner_id=20") == 1 && Scalar(legacy, "SELECT COUNT(*) FROM invoices WHERE invoice_no='OLD-INVOICE' AND partner_id=20") == 1);
Check("old-to-new mapping recorded in audit history", Scalar(legacy, "SELECT COUNT(*) FROM audit_log WHERE entity='partner' AND entity_id=20 AND before_json='KH-0002' AND after_json='GYS-0009'") == 1);
Check("historical deleted codes are reserved when audit records exist", Code(Add("迁移后客户", "customer")) == "KH-0021" && Code(Add("迁移后供应商", "supplier")) == "GYS-0012");
var legacyBoth = MasterDataService.GetPartner(30)!;
legacyBoth.Kind = "customer";
MasterDataService.SavePartner(legacyBoth);
Check("type change corrects a nonstandard dual-role legacy code", Code(30) == "KH-0022");
Check("legacy foreign keys intact", Scalar(legacy, "SELECT COUNT(*) FROM pragma_foreign_key_check") == 0);
using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Directory.GetFiles(legacy.BackupDir, $"*preupgrade-v2-to-v{Database.CurrentVersion}.db").Single(), Mode = SqliteOpenMode.ReadOnly }.ToString()))
{
    backup.Open();
    Check("backup is pre-migration and retains old supplier code", new SqlCmd(backup, "PRAGMA user_version").Long() == 2 && new SqlCmd(backup, "SELECT code FROM partners WHERE id=20").Str() == "KH-0002");
}

var v1 = new Database(Path.Combine(root, "legacy-v1"));
using (var conn = v1.Open())
{
    Schema.CreateV1(conn); Schema.Seed(conn);
    new SqlCmd(conn, "PRAGMA user_version=1").Exec();
    new SqlCmd(conn, "INSERT INTO partners(id,code,name,kind) VALUES(50,'KH-0008','v1 supplier','supplier')").Exec();
}
v1.EnsureCreated();
Check("version 1 upgrades through both migrations", v1.GetUserVersion() == Database.CurrentVersion && Text(v1, "SELECT code FROM partners WHERE id=50") == "GYS-0001");
var v1Backup = Directory.GetFiles(v1.BackupDir, $"*preupgrade-v1-to-v{Database.CurrentVersion}.db").Single();
AppSession.Db = v1;
new AuthService().Login("admin", "admin888");
var safety = v1.RestoreFrom(v1Backup);
Check("restoring old backup re-migrates and creates a safety backup", File.Exists(safety) && v1.GetUserVersion() == Database.CurrentVersion && Text(v1, "SELECT code FROM partners WHERE id=50") == "GYS-0001");

var bad = Legacy("collision");
Sql(bad, "INSERT INTO partners(code,name,kind) VALUES('KH-0040','A','customer'),('kh-0040','B','customer')");
try { bad.EnsureCreated(); Check("pre-existing preserved code collision stops migration", false); } catch (AppException) { Check("pre-existing preserved code collision stops migration", true); }
Check("collision migration rolls back completely", bad.GetUserVersion() == 2 && Scalar(bad, "SELECT COUNT(*) FROM partners") == 2 && Scalar(bad, "SELECT COUNT(*) FROM meta WHERE k LIKE 'partner_code_sequence_v3.%'") == 0);

var failedMigration = Legacy("migration-failure");
Sql(failedMigration, "INSERT INTO partners(code,name,kind) VALUES('KH-0001','old supplier','supplier'); CREATE TRIGGER stop_upgrade BEFORE INSERT ON audit_log BEGIN SELECT RAISE(ABORT,'upgrade failure'); END");
try { failedMigration.EnsureCreated(); Check("migration failure surfaced", false); } catch (SqliteException) { Check("migration failure surfaced", true); }
Check("migration failure preserves version, code and counters", failedMigration.GetUserVersion() == 2 && Text(failedMigration, "SELECT code FROM partners LIMIT 1") == "KH-0001" && Scalar(failedMigration, "SELECT COUNT(*) FROM meta WHERE k LIKE 'partner_code_sequence_v3.%'") == 0);
Sql(failedMigration, "DROP TRIGGER stop_upgrade");
failedMigration.EnsureCreated();
Check("migration succeeds when transient failure is removed", failedMigration.GetUserVersion() == Database.CurrentVersion && Text(failedMigration, "SELECT code FROM partners LIMIT 1") == "GYS-0001");

Console.WriteLine($"RESULT checks={checks} failures={failures}");
return failures == 0 ? 0 : 1;

