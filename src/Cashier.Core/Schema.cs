using Microsoft.Data.Sqlite;
using Cashier.Core.Services;

namespace Cashier.Core;

public static partial class Schema
{
    public static void CreateV1(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Ddl;
        cmd.ExecuteNonQuery();
    }

    public static void Migrate(SqliteConnection conn, int from, int to)
    {
        // 目前仅 v1；后续版本在此追加顺序升级脚本
        for (var v = from + 1; v <= to; v++)
        {
            switch (v)
            {
                case 1:
                    CreateV1(conn);
                    break;
                case 2:
                    MigrateV2(conn);
                    break;
                case 3:
                    PartnerCodeService.MigrateV3(conn);
                    break;
                case 4:
                    MigrateV4(conn);
                    break;
                case 5:
                    MigrateV5(conn);
                    break;
                default:
                    throw new AppException($"缺少数据库升级脚本：v{v}");
            }
        }
    }

    private static void MigrateV4(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();
        var current = new SqlCmd(conn, "PRAGMA user_version", tx).Long();
        if (current >= 4) { tx.Commit(); return; }
        if (current != 3) throw new AppException("账套版本不符合修订号升级要求。");
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            ALTER TABLE vouchers ADD COLUMN revision INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE invoices ADD COLUMN revision INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE users ADD COLUMN auth_revision INTEGER NOT NULL DEFAULT 0;
            INSERT OR IGNORE INTO meta(k,v) VALUES('session.epoch',lower(hex(randomblob(16))));
            CREATE TRIGGER tr_vouchers_revision AFTER UPDATE ON vouchers
            WHEN NEW.revision=OLD.revision BEGIN
                UPDATE vouchers SET revision=OLD.revision+1 WHERE id=NEW.id;
            END;
            CREATE TRIGGER tr_invoices_revision AFTER UPDATE ON invoices
            WHEN NEW.revision=OLD.revision BEGIN
                UPDATE invoices SET revision=OLD.revision+1 WHERE id=NEW.id;
            END;
            PRAGMA user_version=4;
            """;
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    private static void MigrateV2(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            ALTER TABLE invoices ADD COLUMN sign INTEGER NOT NULL DEFAULT 1 CHECK(sign IN (-1,1));
            ALTER TABLE invoices ADD COLUMN original_invoice_id INTEGER REFERENCES invoices(id);
            ALTER TABLE invoice_links ADD COLUMN sign INTEGER NOT NULL DEFAULT 1 CHECK(sign IN (-1,1));
            ALTER TABLE invoice_links ADD COLUMN effective_date TEXT NOT NULL DEFAULT '';
            UPDATE invoice_links SET effective_date=(SELECT MAX(i.reg_date,v.biz_date) FROM invoices i, vouchers v
                WHERE i.id=invoice_links.invoice_id AND v.id=invoice_links.voucher_id);
            ALTER TABLE settlements ADD COLUMN effective_date TEXT NOT NULL DEFAULT '';
            UPDATE settlements SET effective_date=(SELECT MAX(p.biz_date,b.biz_date) FROM vouchers p, vouchers b
                WHERE p.id=settlements.pay_voucher_id AND b.id=settlements.biz_voucher_id);
            CREATE TABLE settlement_history(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                pay_voucher_id INTEGER NOT NULL,
                biz_voucher_id INTEGER NOT NULL,
                amount_cents INTEGER NOT NULL,
                effective_date TEXT NOT NULL,
                ended_date TEXT NOT NULL,
                ended_at TEXT NOT NULL,
                ended_by TEXT NOT NULL,
                reason TEXT NOT NULL
            );
            CREATE INDEX ix_settlement_history_biz ON settlement_history(biz_voucher_id, effective_date, ended_date);
            CREATE TABLE invoice_link_history(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                invoice_id INTEGER NOT NULL,
                voucher_id INTEGER NOT NULL,
                amount_cents INTEGER NOT NULL,
                sign INTEGER NOT NULL CHECK(sign IN (-1,1)),
                effective_date TEXT NOT NULL,
                ended_date TEXT NOT NULL,
                ended_at TEXT NOT NULL,
                ended_by TEXT NOT NULL,
                reason TEXT NOT NULL
            );
            CREATE INDEX ix_invoice_link_history_voucher ON invoice_link_history(voucher_id, effective_date, ended_date);
            PRAGMA user_version=2;
            """;
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    public const string Ddl = """
        CREATE TABLE IF NOT EXISTS meta(
            k TEXT PRIMARY KEY,
            v TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS users(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            username TEXT NOT NULL UNIQUE,
            display_name TEXT NOT NULL,
            pwd_hash TEXT NOT NULL,
            salt TEXT NOT NULL,
            role TEXT NOT NULL,
            is_active INTEGER NOT NULL DEFAULT 1,
            created_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS audit_log(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            ts TEXT NOT NULL,
            user_id INTEGER,
            username TEXT NOT NULL,
            action TEXT NOT NULL,
            entity TEXT NOT NULL,
            entity_id INTEGER,
            doc_no TEXT,
            before_json TEXT,
            after_json TEXT,
            reason TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_log_ts ON audit_log(ts);
        CREATE INDEX IF NOT EXISTS ix_log_entity ON audit_log(entity, entity_id);

        CREATE TABLE IF NOT EXISTS accounts(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            code TEXT NOT NULL UNIQUE,
            name TEXT NOT NULL,
            kind TEXT NOT NULL CHECK(kind IN ('bank','cash')),
            bank_tail TEXT,
            subject_code TEXT,
            opening_cents INTEGER NOT NULL DEFAULT 0,
            is_active INTEGER NOT NULL DEFAULT 1
        );

        CREATE TABLE IF NOT EXISTS subjects(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            code TEXT NOT NULL UNIQUE,
            name TEXT NOT NULL,
            direction TEXT NOT NULL CHECK(direction IN ('in','out')),
            is_active INTEGER NOT NULL DEFAULT 1
        );

        CREATE TABLE IF NOT EXISTS partners(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            code TEXT NOT NULL UNIQUE,
            name TEXT NOT NULL,
            kind TEXT NOT NULL CHECK(kind IN ('customer','supplier','both')),
            contact TEXT, phone TEXT, address TEXT, terms TEXT, level TEXT,
            opening_receivable_cents INTEGER NOT NULL DEFAULT 0,
            opening_payable_cents INTEGER NOT NULL DEFAULT 0,
            is_active INTEGER NOT NULL DEFAULT 1
        );

        CREATE TABLE IF NOT EXISTS depts(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            code TEXT NOT NULL UNIQUE,
            name TEXT NOT NULL,
            is_active INTEGER NOT NULL DEFAULT 1
        );

        CREATE TABLE IF NOT EXISTS projects(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            code TEXT NOT NULL UNIQUE,
            name TEXT NOT NULL,
            dept_id INTEGER REFERENCES depts(id),
            owner TEXT,
            is_active INTEGER NOT NULL DEFAULT 1
        );

        CREATE TABLE IF NOT EXISTS handlers(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT NOT NULL UNIQUE,
            is_active INTEGER NOT NULL DEFAULT 1
        );

        CREATE TABLE IF NOT EXISTS vouchers(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            doc_no TEXT NOT NULL UNIQUE,
            kind TEXT NOT NULL CHECK(kind IN ('income','expense','receivable','payable','transfer')),
            nature TEXT NOT NULL,
            biz_date TEXT NOT NULL,
            period TEXT NOT NULL,
            amount_cents INTEGER NOT NULL CHECK(amount_cents > 0 AND amount_cents <= 99999999999),
            account_id INTEGER REFERENCES accounts(id),
            to_account_id INTEGER REFERENCES accounts(id),
            fee_cents INTEGER NOT NULL DEFAULT 0 CHECK(fee_cents >= 0),
            partner_id INTEGER REFERENCES partners(id),
            project_id INTEGER REFERENCES projects(id),
            dept_id INTEGER REFERENCES depts(id),
            handler TEXT,
            subject_id INTEGER REFERENCES subjects(id),
            summary TEXT NOT NULL,
            note TEXT,
            status TEXT NOT NULL CHECK(status IN ('draft','pending','audited','void')),
            count_as_revenue INTEGER NOT NULL DEFAULT 0,
            count_as_cost INTEGER NOT NULL DEFAULT 0,
            invoiceable INTEGER NOT NULL DEFAULT 1,
            created_at TEXT NOT NULL,
            created_by TEXT NOT NULL,
            updated_at TEXT, updated_by TEXT,
            audited_at TEXT, audited_by TEXT,
            voided_at TEXT, voided_by TEXT, void_reason TEXT,
            ref_id INTEGER REFERENCES vouchers(id)
        );
        CREATE INDEX IF NOT EXISTS ix_v_date ON vouchers(biz_date);
        CREATE INDEX IF NOT EXISTS ix_v_kind_date ON vouchers(kind, biz_date);
        CREATE INDEX IF NOT EXISTS ix_v_status ON vouchers(status);
        CREATE INDEX IF NOT EXISTS ix_v_partner ON vouchers(partner_id);
        CREATE INDEX IF NOT EXISTS ix_v_project ON vouchers(project_id);
        CREATE INDEX IF NOT EXISTS ix_v_account ON vouchers(account_id);

        CREATE TABLE IF NOT EXISTS settlements(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            pay_voucher_id INTEGER NOT NULL REFERENCES vouchers(id),
            biz_voucher_id INTEGER NOT NULL REFERENCES vouchers(id),
            amount_cents INTEGER NOT NULL CHECK(amount_cents > 0),
            created_at TEXT NOT NULL,
            created_by TEXT NOT NULL,
            UNIQUE(pay_voucher_id, biz_voucher_id)
        );
        CREATE INDEX IF NOT EXISTS ix_st_pay ON settlements(pay_voucher_id);
        CREATE INDEX IF NOT EXISTS ix_st_biz ON settlements(biz_voucher_id);

        CREATE TABLE IF NOT EXISTS invoices(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            kind TEXT NOT NULL CHECK(kind IN ('out','in')),
            invoice_type TEXT NOT NULL,
            invoice_no TEXT NOT NULL,
            invoice_code TEXT,
            partner_id INTEGER NOT NULL REFERENCES partners(id),
            reg_date TEXT NOT NULL,
            deliver_date TEXT,
            gross_cents INTEGER NOT NULL CHECK(gross_cents > 0 AND gross_cents <= 99999999999),
            net_cents INTEGER NOT NULL CHECK(net_cents > 0),
            tax_cents INTEGER NOT NULL CHECK(tax_cents >= 0),
            rate_bp INTEGER NOT NULL CHECK(rate_bp >= 0 AND rate_bp <= 10000),
            content TEXT, note TEXT,
            status TEXT NOT NULL CHECK(status IN ('valid','void')),
            created_at TEXT NOT NULL,
            created_by TEXT NOT NULL,
            voided_at TEXT, voided_by TEXT, void_reason TEXT
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_invoice_no ON invoices(kind, invoice_no) WHERE status='valid';
        CREATE INDEX IF NOT EXISTS ix_inv_partner ON invoices(partner_id);
        CREATE INDEX IF NOT EXISTS ix_inv_date ON invoices(reg_date);

        CREATE TABLE IF NOT EXISTS invoice_links(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            invoice_id INTEGER NOT NULL REFERENCES invoices(id),
            voucher_id INTEGER NOT NULL REFERENCES vouchers(id),
            amount_cents INTEGER NOT NULL CHECK(amount_cents > 0),
            UNIQUE(invoice_id, voucher_id)
        );
        CREATE INDEX IF NOT EXISTS ix_il_inv ON invoice_links(invoice_id);
        CREATE INDEX IF NOT EXISTS ix_il_v ON invoice_links(voucher_id);

        CREATE TABLE IF NOT EXISTS periods(
            period TEXT PRIMARY KEY,
            status TEXT NOT NULL CHECK(status IN ('open','closed')),
            closed_at TEXT, closed_by TEXT
        );

        CREATE TABLE IF NOT EXISTS import_batches(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            ts TEXT NOT NULL,
            file_name TEXT,
            kind TEXT,
            total_rows INTEGER NOT NULL DEFAULT 0,
            ok_rows INTEGER NOT NULL DEFAULT 0,
            fail_rows INTEGER NOT NULL DEFAULT 0,
            user_name TEXT,
            detail_json TEXT
        );

        CREATE TABLE IF NOT EXISTS settings(
            k TEXT PRIMARY KEY,
            v TEXT NOT NULL
        );
        """;

    public static void Seed(SqliteConnection conn)
    {
        var now = AppSession.Now();
        using var tx = conn.BeginTransaction();

        void Exec(string sql, params (string k, object? v)[] ps)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        // 管理员：admin / admin888（首次登录后请修改）
        var (hash, salt) = PasswordHasher.Hash("admin888");
        Exec("INSERT INTO users(username, display_name, pwd_hash, salt, role, is_active, created_at) VALUES($u,$d,$h,$s,'admin',1,$t)",
            ("$u", "admin"), ("$d", "系统管理员"), ("$h", hash), ("$s", salt), ("$t", now));

        // 资金账户
        Exec("INSERT INTO accounts(code,name,kind,bank_tail,subject_code,opening_cents) VALUES('100201','工商银行·基本户','bank','—','1002',0)");
        Exec("INSERT INTO accounts(code,name,kind,bank_tail,subject_code,opening_cents) VALUES('100202','建设银行·一般户','bank','—','1002',0)");
        Exec("INSERT INTO accounts(code,name,kind,bank_tail,subject_code,opening_cents) VALUES('1001','现金账户','cash','—','1001',0)");

        // 收入 / 支出科目
        Exec("INSERT INTO subjects(code,name,direction) VALUES('5001','主营业务收入','in')");
        Exec("INSERT INTO subjects(code,name,direction) VALUES('5051','其他业务收入','in')");
        Exec("INSERT INTO subjects(code,name,direction) VALUES('5301','营业外收入','in')");
        Exec("INSERT INTO subjects(code,name,direction) VALUES('5401','主营业务成本','out')");
        Exec("INSERT INTO subjects(code,name,direction) VALUES('5601','销售费用','out')");
        Exec("INSERT INTO subjects(code,name,direction) VALUES('5602','管理费用','out')");
        Exec("INSERT INTO subjects(code,name,direction) VALUES('5603','财务费用','out')");

        // 部门
        Exec("INSERT INTO depts(code,name) VALUES('D01','业务部')");
        Exec("INSERT INTO depts(code,name) VALUES('D02','财务部')");
        Exec("INSERT INTO depts(code,name) VALUES('D03','行政部')");
        Exec("INSERT INTO depts(code,name) VALUES('D04','采购部')");

        // 账套设置
        Exec("INSERT INTO settings(k,v) VALUES('book.name','默认账套')");
        Exec("INSERT INTO settings(k,v) VALUES('book.currency','人民币')");
        Exec("INSERT INTO settings(k,v) VALUES('book.company','')");
        Exec("INSERT INTO settings(k,v) VALUES('ui.pageSize','50')");

        // 期间：当前月开放
        var p = DateTime.Today.ToString("yyyyMM");
        Exec("INSERT INTO periods(period,status) VALUES($p,'open')", ("$p", p));

        tx.Commit();
    }
}

public static class PasswordHasher
{
    private const int Iterations = 120_000;

    public static (string hash, string salt) Hash(string password)
    {
        var saltBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var hash = Derive(password, saltBytes);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(saltBytes));
    }

    public static bool Verify(string password, string hashB64, string saltB64)
    {
        try
        {
            var salt = Convert.FromBase64String(saltB64);
            var expected = Convert.FromBase64String(hashB64);
            var actual = Derive(password, salt);
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch
        {
            return false;
        }
    }

    private static byte[] Derive(string password, byte[] salt) =>
        System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations,
            System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
}
