using Microsoft.Data.Sqlite;

namespace Cashier.Core.Services;

public static partial class MasterDataService
{
    // ---------------- 资金账户 ----------------
    public static List<Account> ListAccounts(bool includeInactive = true, string? asOf = null)
    {
        using var conn = AppSession.Db.Open();
        var sql = "SELECT * FROM accounts" + (includeInactive ? "" : " WHERE is_active=1") + " ORDER BY code";
        var list = new SqlCmd(conn, sql).List(r => new Account
        {
            Id = R.L(r, "id"),
            Code = R.S(r, "code"),
            Name = R.S(r, "name"),
            Kind = R.S(r, "kind"),
            BankTail = R.SN(r, "bank_tail"),
            SubjectCode = R.SN(r, "subject_code"),
            OpeningCents = R.L(r, "opening_cents"),
            IsActive = R.B(r, "is_active"),
        });
        foreach (var a in list) a.BalanceCents = AccountBalance(conn, a, asOf);
        return list;
    }

    /// <summary>账户余额 = 期初 + 资金流入 − 资金流出（含内部转账转入/转出，不含手续费单据以外的东西）。</summary>
    public static long AccountBalance(SqliteConnection conn, Account a, string? asOf = null)
    {
        var dateFilter = string.IsNullOrWhiteSpace(asOf) ? "" : " AND biz_date <= $d";
        using var cmd = new SqlCmd(conn, $@"
            SELECT
              COALESCE(SUM(CASE WHEN kind='income' AND account_id=$a THEN amount_cents ELSE 0 END),0) AS inflow,
              COALESCE(SUM(CASE WHEN kind='transfer' AND to_account_id=$a THEN amount_cents ELSE 0 END),0) AS tin,
              COALESCE(SUM(CASE WHEN kind='expense' AND account_id=$a THEN amount_cents ELSE 0 END),0) AS outflow,
              COALESCE(SUM(CASE WHEN kind='transfer' AND account_id=$a THEN amount_cents ELSE 0 END),0) AS tout
            FROM vouchers WHERE status='audited' AND (account_id=$a OR to_account_id=$a){dateFilter}")
            .P("$a", a.Id);
        if (!string.IsNullOrWhiteSpace(asOf)) cmd.P("$d", asOf);
        var row = cmd.List(r => (inflow: R.L(r, "inflow"), tin: R.L(r, "tin"), outflow: R.L(r, "outflow"), tout: R.L(r, "tout"))).First();
        return checked(a.OpeningCents + row.inflow + row.tin - row.outflow - row.tout
            + OpeningBalanceService.Sum(conn,"account",a.Id,asOf));
    }

    public static long AccountBalance(long accountId, string? asOf = null)
    {
        using var conn = AppSession.Db.Open();
        var a = GetAccount(conn, accountId) ?? throw new AppException("账户不存在。");
        return AccountBalance(conn, a, asOf);
    }

    public static Account? GetAccount(SqliteConnection conn, long id, SqliteTransaction? tx = null) =>
        new SqlCmd(conn, "SELECT * FROM accounts WHERE id=$i",tx).P("$i", id).First(r => new Account
        {
            Id = R.L(r, "id"), Code = R.S(r, "code"), Name = R.S(r, "name"), Kind = R.S(r, "kind"),
            BankTail = R.SN(r, "bank_tail"), SubjectCode = R.SN(r, "subject_code"),
            OpeningCents = R.L(r, "opening_cents"), IsActive = R.B(r, "is_active"),
        });

    public static long SaveAccount(Account a)
    {
        AppSession.Require(Perm.MasterData);
        using var conn=AppSession.Db.Open();
        using var tx=conn.BeginTransaction(deferred:false);
        var id=SaveAccountInTransaction(a,conn,tx);
        tx.Commit();return id;
    }

    internal static long SaveAccountInTransaction(Account a,SqliteConnection conn,SqliteTransaction tx)
    {
        AppSession.Require(Perm.MasterData,conn,tx);
        if (string.IsNullOrWhiteSpace(a.Code)) throw new AppException("请填写账户编码。");
        if (string.IsNullOrWhiteSpace(a.Name)) throw new AppException("请填写账户名称。");
        try
        {
            if (a.Id == 0)
            {
                OpeningBalanceService.AssertOpeningChange(conn,tx,a.OpeningCents!=0);
                new SqlCmd(conn, @"INSERT INTO accounts(code,name,kind,bank_tail,subject_code,opening_cents,is_active)
                    VALUES($c,$n,$k,$b,$s,$o,1)",tx)
                    .P("$c", a.Code.Trim()).P("$n", a.Name.Trim()).P("$k", a.Kind)
                    .P("$b", a.BankTail).P("$s", a.SubjectCode).P("$o", a.OpeningCents).Exec();
                var id = new SqlCmd(conn, "SELECT last_insert_rowid()",tx).Long();
                AuditLogService.Write(conn, tx, "create", "account", id, a.Code, null, $"{a.Name}/{Money.Format(a.OpeningCents)}", null);
                return id;
            }
            var before = GetAccount(conn, a.Id,tx) ?? throw new AppException("账户不存在，请刷新。");
            OpeningBalanceService.AssertOpeningChange(conn,tx,a.OpeningCents!=before.OpeningCents);
            new SqlCmd(conn, @"UPDATE accounts SET code=$c,name=$n,kind=$k,bank_tail=$b,subject_code=$s,
                    opening_cents=$o,is_active=$ac WHERE id=$i",tx)
                .P("$c", a.Code.Trim()).P("$n", a.Name.Trim()).P("$k", a.Kind).P("$b", a.BankTail)
                .P("$s", a.SubjectCode).P("$o", a.OpeningCents).P("$ac", a.IsActive ? 1 : 0).P("$i", a.Id).Exec();
            AuditLogService.Write(conn, tx, "update", "account", a.Id, a.Code,
                before == null ? null : $"{before.Name}/期初{Money.Format(before.OpeningCents)}",
                $"{a.Name}/期初{Money.Format(a.OpeningCents)}", null);
            return a.Id;
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == 2067)
        {
            throw new AppException("账户编码已存在：" + a.Code);
        }
    
    }

    public static bool AccountInUse(long id)
    {
        using var conn = AppSession.Db.Open();
        return new SqlCmd(conn, "SELECT (SELECT COUNT(1) FROM vouchers WHERE account_id=$i OR to_account_id=$i)+(SELECT COUNT(1) FROM balance_adjustments WHERE account_id=$i)").P("$i", id).Long() > 0;
    }

    public static void DeleteAccount(long id)
    {
        AppSession.Require(Perm.MasterData);
        if (AccountInUse(id)) throw new AppException("该账户已被业务引用，不能删除，请改为停用。");
        using var conn = AppSession.Db.Open();
        new SqlCmd(conn, "DELETE FROM accounts WHERE id=$i").P("$i", id).Exec();
        AuditLogService.Write(conn, null, "delete", "account", id, null, null, null, null);
    }

    // ---------------- 科目 ----------------
    public static List<Subject> ListSubjects(string? direction = null, bool includeInactive = true)
    {
        using var conn = AppSession.Db.Open();
        var sql = "SELECT * FROM subjects WHERE 1=1";
        if (!includeInactive) sql += " AND is_active=1";
        if (!string.IsNullOrWhiteSpace(direction)) sql += " AND direction=$d";
        sql += " ORDER BY code";
        using var cmd = new SqlCmd(conn, sql);
        if (!string.IsNullOrWhiteSpace(direction)) cmd.P("$d", direction);
        return cmd.List(r => new Subject
        {
            Id = R.L(r, "id"), Code = R.S(r, "code"), Name = R.S(r, "name"),
            Direction = R.S(r, "direction"), IsActive = R.B(r, "is_active"),
        });
    }

    public static long SaveSubject(Subject s)
    {
        AppSession.Require(Perm.MasterData);
        using var conn=AppSession.Db.Open();
        using var tx=conn.BeginTransaction(deferred:false);
        var id=SaveSubjectInTransaction(s,conn,tx);
        tx.Commit();return id;
    }

    internal static long SaveSubjectInTransaction(Subject s,SqliteConnection conn,SqliteTransaction tx)
    {
        AppSession.Require(Perm.MasterData,conn,tx);
        if (string.IsNullOrWhiteSpace(s.Code) || string.IsNullOrWhiteSpace(s.Name)) throw new AppException("请填写科目编码与名称。");
        try
        {
            if (s.Id == 0)
            {
                new SqlCmd(conn, "INSERT INTO subjects(code,name,direction,is_active) VALUES($c,$n,$d,1)",tx)
                    .P("$c", s.Code.Trim()).P("$n", s.Name.Trim()).P("$d", s.Direction).Exec();
                return new SqlCmd(conn, "SELECT last_insert_rowid()",tx).Long();
            }
            new SqlCmd(conn, "UPDATE subjects SET code=$c,name=$n,direction=$d,is_active=$a WHERE id=$i",tx)
                .P("$c", s.Code.Trim()).P("$n", s.Name.Trim()).P("$d", s.Direction).P("$a", s.IsActive ? 1 : 0).P("$i", s.Id).Exec();
            return s.Id;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new AppException("科目编码已存在：" + s.Code);
        }
    
    }

    public static void DeleteSubject(long id)
    {
        AppSession.Require(Perm.MasterData);
        using var conn = AppSession.Db.Open();
        if (new SqlCmd(conn, "SELECT COUNT(1) FROM vouchers WHERE subject_id=$i").P("$i", id).Long() > 0)
            throw new AppException("该科目已被业务引用，不能删除，请改为停用。");
        new SqlCmd(conn, "DELETE FROM subjects WHERE id=$i").P("$i", id).Exec();
    }

    // ---------------- 往来单位 ----------------
    public static bool PartnerCodeChangesOnKindChange(Partner before, string kind) =>
        PartnerCodeService.RequiresNewCode(before, kind);

    public static List<Partner> ListPartners(string? kind = null, bool includeInactive = true, string? asOf = null)
    {
        using var conn = AppSession.Db.Open();
        var sql = "SELECT * FROM partners WHERE 1=1";
        if (!includeInactive) sql += " AND is_active=1";
        if (kind == "customer") sql += " AND kind IN ('customer','both')";
        if (kind == "supplier") sql += " AND kind IN ('supplier','both')";
        sql += " ORDER BY code";
        var list = new SqlCmd(conn, sql).List(MapPartner);
        foreach (var p in list) FillPartnerBalance(conn, p,asOf);
        return list;
    }

    public static Partner? GetPartner(long id)
    {
        using var conn = AppSession.Db.Open();
        var p = new SqlCmd(conn, "SELECT * FROM partners WHERE id=$i").P("$i", id).First(MapPartner);
        if (p != null) FillPartnerBalance(conn, p);
        return p;
    }

    public static Partner MapPartner(SqliteDataReader r) => new()
    {
        Id = R.L(r, "id"), Code = R.S(r, "code"), Name = R.S(r, "name"), Kind = R.S(r, "kind"),
        Contact = R.SN(r, "contact"), Phone = R.SN(r, "phone"), Address = R.SN(r, "address"),
        Terms = R.SN(r, "terms"), Level = R.SN(r, "level"),
        OpeningReceivableCents = R.L(r, "opening_receivable_cents"),
        OpeningPayableCents = R.L(r, "opening_payable_cents"),
        IsActive = R.B(r, "is_active"),
    };

    /// <summary>客户净应收 = 期初 + 应收单 − 回款 − 预收 + 退款；供应商净应付同理。</summary>
    public static void FillPartnerBalance(SqliteConnection conn, Partner p, string? asOf = null)
    {
        var df = string.IsNullOrWhiteSpace(asOf) ? "" : " AND biz_date <= $d";
        using var cmd = new SqlCmd(conn, $@"
            SELECT
              COALESCE(SUM(CASE WHEN kind='receivable' THEN amount_cents ELSE 0 END),0) AS recv,
              COALESCE(SUM(CASE WHEN kind='payable' THEN amount_cents ELSE 0 END),0) AS pay,
              COALESCE(SUM(CASE WHEN kind='income' AND nature='CustomerReceipt' THEN amount_cents ELSE 0 END),0) AS got,
              COALESCE(SUM(CASE WHEN kind='income' AND nature='CustomerPrepay' THEN amount_cents ELSE 0 END),0) AS prepayIn,
              COALESCE(SUM(CASE WHEN kind='expense' AND nature='CustomerRefund' THEN amount_cents ELSE 0 END),0) AS refundOut,
              COALESCE(SUM(CASE WHEN kind='expense' AND nature='SupplierPayment' THEN amount_cents ELSE 0 END),0) AS paid,
              COALESCE(SUM(CASE WHEN kind='expense' AND nature='SupplierPrepay' THEN amount_cents ELSE 0 END),0) AS prepayOut,
              COALESCE(SUM(CASE WHEN kind='income' AND nature='SupplierRefund' THEN amount_cents ELSE 0 END),0) AS refundIn
            FROM vouchers WHERE status='audited' AND partner_id=$p{df}")
            .P("$p", p.Id);
        if (!string.IsNullOrWhiteSpace(asOf)) cmd.P("$d", asOf);
        var x = cmd.List(r => new
        {
            recv = R.L(r, "recv"), pay = R.L(r, "pay"), got = R.L(r, "got"), prepayIn = R.L(r, "prepayIn"),
            refundOut = R.L(r, "refundOut"), paid = R.L(r, "paid"), prepayOut = R.L(r, "prepayOut"), refundIn = R.L(r, "refundIn"),
        }).First();
        p.ReceivableCents = x.recv;
        p.ReceivedCents = x.got;
        p.PayableCents = x.pay;
        p.PaidCents = x.paid;
        p.NetReceivableCents = checked(p.OpeningReceivableCents + x.recv - x.got - x.prepayIn + x.refundOut + OpeningBalanceService.Sum(conn,"receivable",p.Id,asOf));
        p.NetPayableCents = checked(p.OpeningPayableCents + x.pay - x.paid - x.prepayOut + x.refundIn + OpeningBalanceService.Sum(conn,"payable",p.Id,asOf));
        p.InvoicedCents = new SqlCmd(conn, @"SELECT COALESCE(SUM(gross_cents * sign),0) FROM invoices
            WHERE partner_id=$p AND status='valid'" + (string.IsNullOrWhiteSpace(asOf) ? "" : " AND reg_date <= $d"))
            .P("$p", p.Id).Let(c => { if (!string.IsNullOrWhiteSpace(asOf)) c.P("$d", asOf); return c; }).Long();
    }

    public static long SavePartner(Partner p)
    {
        AppSession.Require(Perm.MasterData);
        using var conn=AppSession.Db.Open();
        using var tx=conn.BeginTransaction(deferred:false);
        var id=SavePartnerInTransaction(p,conn,tx);
        tx.Commit();return id;
    }

    internal static long SavePartnerInTransaction(Partner p,SqliteConnection conn,SqliteTransaction tx)
    {
        AppSession.Require(Perm.MasterData,conn,tx);
        ValidatePartnerFields(p);
        try
        {
            if (p.Id == 0)
            {
                var id = InsertPartner(conn, tx, p);
                return id;
            }
            var before = new SqlCmd(conn, "SELECT * FROM partners WHERE id=$i", tx).P("$i", p.Id).First(MapPartner)
                ?? throw new AppException("该往来单位不存在，请刷新后重试。");
            OpeningBalanceService.AssertOpeningChange(conn,tx,p.OpeningReceivableCents!=before.OpeningReceivableCents || p.OpeningPayableCents!=before.OpeningPayableCents);
            if (p.Kind != "both")
            {
                var incompatible = new SqlCmd(conn, @"SELECT
                    (SELECT COUNT(*) FROM vouchers WHERE partner_id=$i AND status<>'void' AND
                      (($kind='customer' AND (kind='payable' OR nature IN ('SupplierPayment','SupplierPrepay','SupplierRefund')))
                       OR ($kind='supplier' AND (kind='receivable' OR nature IN ('CustomerReceipt','CustomerPrepay','CustomerRefund')))))
                    + (SELECT COUNT(*) FROM invoices WHERE partner_id=$i AND status='valid'
                       AND (($kind='customer' AND kind='in') OR ($kind='supplier' AND kind='out')))
                    + (SELECT COUNT(*) FROM balance_adjustments WHERE partner_id=$i
                       AND (($kind='customer' AND kind='payable') OR ($kind='supplier' AND kind='receivable')))", tx)
                    .P("$i", p.Id).P("$kind", p.Kind).Long();
                if (incompatible > 0) throw new AppException("已有业务使用该单位的另一种身份，不能移除该身份，请保留客户兼供应商。");
            }
            // The database value is authoritative; a caller cannot edit Code manually.
            // A change across the customer/supplier categories allocates a fresh code.
            p.Code = PartnerCodeService.RequiresNewCode(before, p.Kind)
                ? PartnerCodeService.Allocate(conn, tx, p.Kind) : before.Code;
            new SqlCmd(conn, @"UPDATE partners SET code=$c,name=$n,kind=$k,contact=$ct,phone=$ph,address=$ad,
                    terms=$tm,level=$lv,opening_receivable_cents=$or,opening_payable_cents=$op,is_active=$ac
                    WHERE id=$i", tx)
                .P("$c", p.Code.Trim()).P("$n", p.Name.Trim()).P("$k", p.Kind).P("$ct", p.Contact).P("$ph", p.Phone)
                .P("$ad", p.Address).P("$tm", p.Terms).P("$lv", p.Level)
                .P("$or", p.OpeningReceivableCents).P("$op", p.OpeningPayableCents)
                .P("$ac", p.IsActive ? 1 : 0).P("$i", p.Id).Exec();
            AuditLogService.Write(conn, tx, "update", "partner", p.Id, p.Code,
                $"{before.Code}/{before.Name}/期初应收{Money.Format(before.OpeningReceivableCents)}",
                $"{p.Code}/{p.Name}/期初应收{Money.Format(p.OpeningReceivableCents)}", null);
            return p.Id;
        }
        catch (PartnerCodeConflict)
        {
            throw new AppException("编码已存在：" + p.Code);
        }
        catch (SqliteException ex) when (IsPartnerCodeConflict(ex))
        {
            throw new AppException("编码已存在：" + p.Code);
        }
    
    }

    public static bool PartnerInUse(long id)
    {
        using var conn = AppSession.Db.Open();
        var a = new SqlCmd(conn, "SELECT COUNT(1) FROM vouchers WHERE partner_id=$i").P("$i", id).Long();
        var b = new SqlCmd(conn, "SELECT COUNT(1) FROM invoices WHERE partner_id=$i").P("$i", id).Long();
        var c = new SqlCmd(conn,"SELECT COUNT(*) FROM balance_adjustments WHERE partner_id=$i").P("$i",id).Long();
        return a + b + c > 0;
    }

    public static void DeletePartner(long id)
    {
        AppSession.Require(Perm.MasterData);
        if (PartnerInUse(id)) throw new AppException("该单位已被业务或发票引用，不能删除，请改为停用。");
        using var conn = AppSession.Db.Open();
        new SqlCmd(conn, "DELETE FROM partners WHERE id=$i").P("$i", id).Exec();
    }

    // ---------------- 项目 / 部门 / 经办人 ----------------
    public static List<Project> ListProjects(bool includeInactive = true)
    {
        using var conn = AppSession.Db.Open();
        var sql = @"SELECT p.*, COALESCE(d.name,'') AS dept_name FROM projects p
                    LEFT JOIN depts d ON d.id=p.dept_id" + (includeInactive ? "" : " WHERE p.is_active=1") + " ORDER BY p.code";
        return new SqlCmd(conn, sql).List(r => new Project
        {
            Id = R.L(r, "id"), Code = R.S(r, "code"), Name = R.S(r, "name"),
            DeptId = R.LN(r, "dept_id"), Owner = R.SN(r, "owner"),
            IsActive = R.B(r, "is_active"), DeptName = R.S(r, "dept_name"),
        });
    }

    public static long SaveProject(Project p)
    {
        AppSession.Require(Perm.MasterData);
        using var conn=AppSession.Db.Open();
        using var tx=conn.BeginTransaction(deferred:false);
        var id=SaveProjectInTransaction(p,conn,tx);
        tx.Commit();return id;
    }

    internal static long SaveProjectInTransaction(Project p,SqliteConnection conn,SqliteTransaction tx)
    {
        AppSession.Require(Perm.MasterData,conn,tx);
        if (string.IsNullOrWhiteSpace(p.Code) || string.IsNullOrWhiteSpace(p.Name)) throw new AppException("请填写项目编码与名称。");
        try
        {
            if (p.Id == 0)
            {
                new SqlCmd(conn, "INSERT INTO projects(code,name,dept_id,owner,is_active) VALUES($c,$n,$d,$o,1)",tx)
                    .P("$c", p.Code.Trim()).P("$n", p.Name.Trim()).P("$d", p.DeptId).P("$o", p.Owner).Exec();
                return new SqlCmd(conn, "SELECT last_insert_rowid()",tx).Long();
            }
            new SqlCmd(conn, "UPDATE projects SET code=$c,name=$n,dept_id=$d,owner=$o,is_active=$a WHERE id=$i",tx)
                .P("$c", p.Code.Trim()).P("$n", p.Name.Trim()).P("$d", p.DeptId).P("$o", p.Owner)
                .P("$a", p.IsActive ? 1 : 0).P("$i", p.Id).Exec();
            return p.Id;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new AppException("项目编码已存在：" + p.Code);
        }
    
    }

    public static void DeleteProject(long id)
    {
        AppSession.Require(Perm.MasterData);
        using var conn = AppSession.Db.Open();
        if (new SqlCmd(conn, "SELECT COUNT(1) FROM vouchers WHERE project_id=$i").P("$i", id).Long() > 0)
            throw new AppException("该项目已被业务引用，不能删除，请改为停用。");
        new SqlCmd(conn, "DELETE FROM projects WHERE id=$i").P("$i", id).Exec();
    }

    public static List<Dept> ListDepts(bool includeInactive = true)
    {
        using var conn = AppSession.Db.Open();
        return new SqlCmd(conn, "SELECT * FROM depts" + (includeInactive ? "" : " WHERE is_active=1") + " ORDER BY code")
            .List(r => new Dept { Id = R.L(r, "id"), Code = R.S(r, "code"), Name = R.S(r, "name"), IsActive = R.B(r, "is_active") });
    }

    public static long SaveDept(Dept d)
    {
        AppSession.Require(Perm.MasterData);
        using var conn=AppSession.Db.Open();
        using var tx=conn.BeginTransaction(deferred:false);
        var id=SaveDeptInTransaction(d,conn,tx);
        tx.Commit();return id;
    }

    internal static long SaveDeptInTransaction(Dept d,SqliteConnection conn,SqliteTransaction tx)
    {
        AppSession.Require(Perm.MasterData,conn,tx);
        if (string.IsNullOrWhiteSpace(d.Code) || string.IsNullOrWhiteSpace(d.Name)) throw new AppException("请填写部门编码与名称。");
        try
        {
            if (d.Id == 0)
            {
                new SqlCmd(conn, "INSERT INTO depts(code,name,is_active) VALUES($c,$n,1)",tx)
                    .P("$c", d.Code.Trim()).P("$n", d.Name.Trim()).Exec();
                return new SqlCmd(conn, "SELECT last_insert_rowid()",tx).Long();
            }
            new SqlCmd(conn, "UPDATE depts SET code=$c,name=$n,is_active=$a WHERE id=$i",tx)
                .P("$c", d.Code.Trim()).P("$n", d.Name.Trim()).P("$a", d.IsActive ? 1 : 0).P("$i", d.Id).Exec();
            return d.Id;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new AppException("部门编码已存在：" + d.Code);
        }
    
    }

    public static void DeleteDept(long id)
    {
        AppSession.Require(Perm.MasterData);
        using var conn = AppSession.Db.Open();
        if (new SqlCmd(conn, "SELECT COUNT(1) FROM vouchers WHERE dept_id=$i").P("$i", id).Long() > 0)
            throw new AppException("该部门已被业务引用，不能删除，请改为停用。");
        if (new SqlCmd(conn, "SELECT COUNT(1) FROM projects WHERE dept_id=$i").P("$i", id).Long() > 0)
            throw new AppException("该部门下还有项目，不能删除。");
        new SqlCmd(conn, "DELETE FROM depts WHERE id=$i").P("$i", id).Exec();
    }

    public static List<Handler> ListHandlers(bool includeInactive = true)
    {
        using var conn = AppSession.Db.Open();
        return new SqlCmd(conn, "SELECT * FROM handlers" + (includeInactive ? "" : " WHERE is_active=1") + " ORDER BY name")
            .List(r => new Handler { Id = R.L(r, "id"), Name = R.S(r, "name"), IsActive = R.B(r, "is_active") });
    }

    public static long SaveHandler(Handler h)
    {
        AppSession.Require(Perm.MasterData);
        using var conn=AppSession.Db.Open();
        using var tx=conn.BeginTransaction(deferred:false);
        var id=SaveHandlerInTransaction(h,conn,tx);
        tx.Commit();return id;
    }

    internal static long SaveHandlerInTransaction(Handler h,SqliteConnection conn,SqliteTransaction tx)
    {
        AppSession.Require(Perm.MasterData,conn,tx);
        if (string.IsNullOrWhiteSpace(h.Name)) throw new AppException("请填写经办人姓名。");
        try
        {
            if (h.Id == 0)
            {
                new SqlCmd(conn, "INSERT INTO handlers(name,is_active) VALUES($n,1)",tx).P("$n", h.Name.Trim()).Exec();
                return new SqlCmd(conn, "SELECT last_insert_rowid()",tx).Long();
            }
            new SqlCmd(conn, "UPDATE handlers SET name=$n,is_active=$a WHERE id=$i",tx)
                .P("$n", h.Name.Trim()).P("$a", h.IsActive ? 1 : 0).P("$i", h.Id).Exec();
            return h.Id;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new AppException("经办人已存在：" + h.Name);
        }
    
    }

    public static void DeleteHandler(long id)
    {
        AppSession.Require(Perm.MasterData);
        using var conn = AppSession.Db.Open();
        new SqlCmd(conn, "DELETE FROM handlers WHERE id=$i").P("$i", id).Exec();
    }
}

internal static class SqlCmdExtensions
{
    public static T Let<T>(this T self, Func<T, T> f) => f(self);
}
