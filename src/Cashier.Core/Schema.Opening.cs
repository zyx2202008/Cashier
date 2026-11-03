using Microsoft.Data.Sqlite;

namespace Cashier.Core;

public static partial class Schema
{
    private static void MigrateV5(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction(deferred: false);
        var version = new SqlCmd(conn, "PRAGMA user_version", tx).Long();
        if (version >= 5) { tx.Commit(); return; }
        if (version != 4) throw new AppException("账套版本不符合期初锁定升级要求。");
        new SqlCmd(conn, """
            CREATE TABLE opening_control(
                id INTEGER PRIMARY KEY CHECK(id=1),
                start_date TEXT NOT NULL DEFAULT '',
                locked_at TEXT NOT NULL DEFAULT '',
                locked_by TEXT NOT NULL DEFAULT '',
                reason TEXT NOT NULL DEFAULT ''
            );
            INSERT INTO opening_control(id) VALUES(1);
            UPDATE opening_control SET
                start_date=COALESCE((SELECT MIN(d) FROM (SELECT biz_date d FROM vouchers UNION ALL SELECT reg_date FROM invoices)),date('now','localtime')),
                locked_at=datetime('now','localtime'),locked_by='系统升级',reason='旧账套已有业务或期初余额，升级时自动锁定'
            WHERE EXISTS(SELECT 1 FROM vouchers) OR EXISTS(SELECT 1 FROM invoices)
               OR EXISTS(SELECT 1 FROM accounts WHERE opening_cents<>0)
               OR EXISTS(SELECT 1 FROM partners WHERE opening_receivable_cents<>0 OR opening_payable_cents<>0)
               OR EXISTS(SELECT 1 FROM periods WHERE status='closed');
            INSERT INTO audit_log(ts,username,action,entity,entity_id,after_json,reason)
                SELECT locked_at,locked_by,'opening-lock','opening',1,start_date,reason FROM opening_control WHERE locked_at<>'';
            CREATE TABLE balance_adjustments(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                request_id TEXT NOT NULL UNIQUE,
                kind TEXT NOT NULL CHECK(kind IN ('account','receivable','payable')),
                account_id INTEGER REFERENCES accounts(id),
                partner_id INTEGER REFERENCES partners(id),
                effective_date TEXT NOT NULL CHECK(length(effective_date)=10 AND effective_date BETWEEN '1990-01-01' AND '2100-12-31' AND date(effective_date,'+0 days') IS NOT NULL AND date(effective_date,'+0 days')=effective_date),
                delta_cents INTEGER NOT NULL CHECK(typeof(delta_cents)='integer' AND delta_cents<>0 AND delta_cents BETWEEN -99999999999 AND 99999999999),
                balance_before INTEGER NOT NULL,
                balance_after INTEGER NOT NULL CHECK(balance_after=balance_before+delta_cents),
                reason TEXT NOT NULL CHECK(length(trim(reason)) BETWEEN 1 AND 500),
                created_at TEXT NOT NULL,
                created_by TEXT NOT NULL,
                user_id INTEGER NOT NULL REFERENCES users(id),
                reversal_of INTEGER UNIQUE REFERENCES balance_adjustments(id),
                CHECK((kind='account' AND account_id IS NOT NULL AND partner_id IS NULL) OR (kind IN ('receivable','payable') AND partner_id IS NOT NULL AND account_id IS NULL))
            );
            CREATE INDEX ix_adjust_account_date ON balance_adjustments(account_id,effective_date);
            CREATE INDEX ix_adjust_partner_date ON balance_adjustments(partner_id,kind,effective_date);
            CREATE TRIGGER tr_opening_control_update BEFORE UPDATE ON opening_control WHEN OLD.locked_at<>'' BEGIN
                SELECT RAISE(ABORT,'期初已锁定，不能解锁或改写确认信息');
            END;
            CREATE TRIGGER tr_opening_control_delete BEFORE DELETE ON opening_control BEGIN
                SELECT RAISE(ABORT,'不能删除期初控制信息');
            END;
            CREATE TRIGGER tr_opening_account_update BEFORE UPDATE OF opening_cents ON accounts
            WHEN NEW.opening_cents<>OLD.opening_cents AND EXISTS(SELECT 1 FROM opening_control WHERE locked_at<>'') BEGIN
                SELECT RAISE(ABORT,'期初已锁定，请使用余额调整');
            END;
            CREATE TRIGGER tr_opening_account_insert BEFORE INSERT ON accounts
            WHEN NEW.opening_cents<>0 AND EXISTS(SELECT 1 FROM opening_control WHERE locked_at<>'') BEGIN
                SELECT RAISE(ABORT,'期初已锁定，新账户请以零期初建立，再使用余额调整');
            END;
            CREATE TRIGGER tr_opening_account_delete BEFORE DELETE ON accounts
            WHEN OLD.opening_cents<>0 AND EXISTS(SELECT 1 FROM opening_control WHERE locked_at<>'') BEGIN
                SELECT RAISE(ABORT,'期初已锁定，该账户请改为停用');
            END;
            CREATE TRIGGER tr_opening_partner_update BEFORE UPDATE OF opening_receivable_cents,opening_payable_cents ON partners
            WHEN (NEW.opening_receivable_cents<>OLD.opening_receivable_cents OR NEW.opening_payable_cents<>OLD.opening_payable_cents)
              AND EXISTS(SELECT 1 FROM opening_control WHERE locked_at<>'') BEGIN
                SELECT RAISE(ABORT,'期初已锁定，请使用余额调整');
            END;
            CREATE TRIGGER tr_opening_partner_insert BEFORE INSERT ON partners
            WHEN (NEW.opening_receivable_cents<>0 OR NEW.opening_payable_cents<>0) AND EXISTS(SELECT 1 FROM opening_control WHERE locked_at<>'') BEGIN
                SELECT RAISE(ABORT,'期初已锁定，新单位请以零期初建立，再使用余额调整');
            END;
            CREATE TRIGGER tr_opening_partner_delete BEFORE DELETE ON partners
            WHEN (OLD.opening_receivable_cents<>0 OR OLD.opening_payable_cents<>0) AND EXISTS(SELECT 1 FROM opening_control WHERE locked_at<>'') BEGIN
                SELECT RAISE(ABORT,'期初已锁定，该单位请改为停用');
            END;
            CREATE TRIGGER tr_adjustment_update BEFORE UPDATE ON balance_adjustments BEGIN
                SELECT RAISE(ABORT,'调整记录不可修改，请追加反向调整');
            END;
            CREATE TRIGGER tr_adjustment_delete BEFORE DELETE ON balance_adjustments BEGIN
                SELECT RAISE(ABORT,'调整记录不可删除，请追加反向调整');
            END;
            CREATE TRIGGER tr_adjustment_insert BEFORE INSERT ON balance_adjustments BEGIN
                SELECT CASE WHEN NOT EXISTS(SELECT 1 FROM opening_control WHERE locked_at<>'' AND start_date<=NEW.effective_date)
                    THEN RAISE(ABORT,'请先确认锁定期初，并检查调整生效日') END;
                SELECT CASE WHEN NEW.effective_date<date('now','localtime') THEN RAISE(ABORT,'调整生效日不得早于登记当天') END;
                SELECT CASE WHEN EXISTS(SELECT 1 FROM periods WHERE period=substr(NEW.effective_date,1,4)||substr(NEW.effective_date,6,2) AND status='closed')
                    THEN RAISE(ABORT,'调整生效期间已关闭') END;
                SELECT CASE WHEN NEW.kind='receivable' AND NOT EXISTS(SELECT 1 FROM partners WHERE id=NEW.partner_id AND kind IN ('customer','both'))
                    THEN RAISE(ABORT,'应收调整必须选择客户') END;
                SELECT CASE WHEN NEW.kind='payable' AND NOT EXISTS(SELECT 1 FROM partners WHERE id=NEW.partner_id AND kind IN ('supplier','both'))
                    THEN RAISE(ABORT,'应付调整必须选择供应商') END;
                SELECT CASE WHEN NEW.reversal_of IS NOT NULL AND NOT EXISTS(SELECT 1 FROM balance_adjustments a WHERE a.id=NEW.reversal_of
                    AND a.reversal_of IS NULL AND a.kind=NEW.kind AND a.account_id IS NEW.account_id AND a.partner_id IS NEW.partner_id
                    AND a.delta_cents=-NEW.delta_cents AND a.effective_date<=NEW.effective_date)
                    THEN RAISE(ABORT,'反向调整与原记录不一致') END;
            END;
            PRAGMA user_version=5;
            """, tx).Exec();
        tx.Commit();
    }
}
