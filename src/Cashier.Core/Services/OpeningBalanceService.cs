using Microsoft.Data.Sqlite;

namespace Cashier.Core.Services;

public sealed record OpeningState(string StartDate, string LockedAt, string LockedBy, string Reason)
{
    public bool IsLocked => LockedAt.Length > 0;
}

public sealed record BalanceAdjustment(long Id, string Kind, long TargetId, string TargetName,
    string EffectiveDate, long DeltaCents, string Reason, string CreatedAt, string CreatedBy,
    long? ReversalOf, bool HasReversal,long BalanceBefore,long BalanceAfter)
{
    public string Number => $"TZ{Id:D8}";
    public string KindText => Kind switch { "account" => "账户余额", "receivable" => "客户应收", _ => "供应商应付" };
    public string DeltaText => Money.FormatSigned(DeltaCents);
    public string BeforeText => Money.FormatSigned(BalanceBefore);
    public string AfterText => Money.FormatSigned(BalanceAfter);
    public string StatusText => ReversalOf.HasValue ? $"反向调整 TZ{ReversalOf:D8}" : HasReversal ? "已追加反向调整" : "有效";
}

public static class OpeningBalanceService
{
    public static OpeningState State()
    {
        using var conn = AppSession.Db.Open();
        AppSession.ValidateCurrent(conn);
        return State(conn);
    }

    internal static OpeningState State(SqliteConnection conn, SqliteTransaction? tx = null) =>
        new SqlCmd(conn, "SELECT * FROM opening_control WHERE id=1", tx).First(r =>
            new OpeningState(R.S(r,"start_date"),R.S(r,"locked_at"),R.S(r,"locked_by"),R.S(r,"reason")))
        ?? throw new AppException("账套缺少期初控制信息，请从有效备份恢复。");

    public static string SuggestedStartDate()
    {
        using var conn = AppSession.Db.Open();
        var first=new SqlCmd(conn, "SELECT MIN(d) FROM (SELECT biz_date d FROM vouchers UNION ALL SELECT reg_date FROM invoices)").Str();
        return string.IsNullOrEmpty(first)?SqlUtil.Today():first;
    }

    public static void Confirm(string startDate, string reason)
    {
        SqlUtil.ValidateDate(startDate,"启用日期");
        reason = ValidateReason(reason);
        if (string.CompareOrdinal(startDate,SqlUtil.Today())>0) throw new AppException("启用日期不能晚于今天。");
        using var conn = AppSession.Db.Open();
        using var tx = conn.BeginTransaction(deferred:false);
        AppSession.Require(Perm.OpeningLock,conn,tx);
        if (State(conn,tx).IsLocked) throw new AppException("期初已经锁定，请刷新查看。");
        var first = new SqlCmd(conn,"SELECT MIN(d) FROM (SELECT biz_date d FROM vouchers UNION ALL SELECT reg_date FROM invoices)",tx).Str();
        if (!string.IsNullOrEmpty(first) && string.CompareOrdinal(startDate,first)>0) throw new AppException("启用日期不能晚于已有最早业务日期："+first);
        new SqlCmd(conn,"UPDATE opening_control SET start_date=$d,locked_at=$t,locked_by=$u,reason=$r WHERE id=1 AND locked_at=''",tx)
            .P("$d",startDate).P("$t",SqlUtil.Now()).P("$u",AppSession.UserName).P("$r",reason).Exec();
        AuditLogService.Write(conn,tx,"opening-lock","opening",1,null,null,startDate,reason);
        tx.Commit();
    }

    internal static void AssertOpeningChange(SqliteConnection conn, SqliteTransaction? tx, bool changed)
    {
        if (changed && State(conn,tx).IsLocked) throw new AppException("期初已锁定，请到“期初与余额调整”登记更正。");
    }

    public static long Create(string kind, long targetId, string effectiveDate, long deltaCents, string reason, string requestId)
    {
        using var conn = AppSession.Db.Open();
        using var tx = conn.BeginTransaction(deferred:false);
        AppSession.Require(Perm.BalanceAdjust,conn,tx);
        Validate(kind,effectiveDate,deltaCents,ref reason,requestId);
        var existing = new SqlCmd(conn,"SELECT id,kind,COALESCE(account_id,partner_id) target,effective_date,delta_cents,reason,user_id FROM balance_adjustments WHERE request_id=$r",tx)
            .P("$r",requestId).First(r => (id:R.L(r,"id"),kind:R.S(r,"kind"),target:R.L(r,"target"),date:R.S(r,"effective_date"),amount:R.L(r,"delta_cents"),reason:R.S(r,"reason"),user:R.L(r,"user_id")));
        if (existing.id != 0)
        {
            if (existing.kind!=kind || existing.target!=targetId || existing.date!=effectiveDate || existing.amount!=deltaCents || existing.reason!=reason || existing.user!=AppSession.Current!.Id)
                throw new AppException("提交标识已用于其他调整，请刷新后重试。");
            tx.Commit(); return existing.id;
        }
        var id = Insert(conn,tx,kind,targetId,effectiveDate,deltaCents,reason,requestId,null);
        tx.Commit(); return id;
    }

    public static long Reverse(long id, string effectiveDate, string reason)
    {
        using var conn = AppSession.Db.Open();
        using var tx = conn.BeginTransaction(deferred:false);
        AppSession.Require(Perm.BalanceAdjust,conn,tx);
        var original = new SqlCmd(conn,"SELECT *,COALESCE(account_id,partner_id) target FROM balance_adjustments WHERE id=$i",tx).P("$i",id)
            .First(r => (kind:R.S(r,"kind"),target:R.L(r,"target"),date:R.S(r,"effective_date"),amount:R.L(r,"delta_cents"),reverse:R.LN(r,"reversal_of")));
        if (original.kind==null) throw new AppException("调整记录不存在。");
        if (original.reverse.HasValue) throw new AppException("反向调整不能再次反向，请登记一笔新的调整。");
        if (new SqlCmd(conn,"SELECT COUNT(*) FROM balance_adjustments WHERE reversal_of=$i",tx).P("$i",id).Long()>0)
            throw new AppException("该记录已经追加反向调整，请刷新查看。");
        if (string.CompareOrdinal(effectiveDate,original.date)<0) throw new AppException("反向调整生效日不能早于原调整。");
        var amount = checked(-original.amount);
        Validate(original.kind,effectiveDate,amount,ref reason,"reverse-"+id);
        var created = Insert(conn,tx,original.kind,original.target,effectiveDate,amount,reason,"reverse-"+id,id);
        tx.Commit(); return created;
    }

    private static void Validate(string kind,string date,long amount,ref string reason,string request)
    {
        if (kind is not ("account" or "receivable" or "payable")) throw new AppException("调整类型无效。");
        SqlUtil.ValidateDate(date,"生效日期");
        if (string.CompareOrdinal(date,SqlUtil.Today())<0) throw new AppException("生效日期不得早于登记当天，以免改写历史余额。");
        if (amount==0 || amount < -Money.MaxCents || amount>Money.MaxCents) throw new AppException("调整金额必须非零，且绝对值不超过 "+Money.Format(Money.MaxCents)+" 元。");
        reason=ValidateReason(reason);
        if (string.IsNullOrWhiteSpace(request) || request.Length>100) throw new AppException("提交标识无效，请重新打开调整窗口。");
    }

    private static string ValidateReason(string? reason)
    {
        var value=reason?.Trim() ?? "";
        if(value.Length is <1 or >500) throw new AppException("请填写调整或确认原因，最多500字。");
        return value;
    }

    private static long Insert(SqliteConnection conn,SqliteTransaction tx,string kind,long target,string date,long amount,string reason,string request,long? reverse)
    {
        var state=State(conn,tx);
        if(!state.IsLocked) throw new AppException("请先由管理员确认并锁定期初。");
        if(string.CompareOrdinal(date,state.StartDate)<0) throw new AppException("调整生效日不能早于账套启用日期。");
        var period=SqlUtil.PeriodOf(date);
        if(new SqlCmd(conn,"SELECT COUNT(*) FROM periods WHERE period=$p AND status='closed'",tx).P("$p",period).Long()>0)
            throw new AppException("调整生效期间已关闭，请选择开放期间。");
        var table=kind=="account"?"accounts":"partners";
        var entity=new SqlCmd(conn,$"SELECT kind,is_active FROM {table} WHERE id=$i",tx).P("$i",target)
            .First(r=>(kind:R.S(r,"kind"),active:R.B(r,"is_active")));
        if(entity.kind==null) throw new AppException("调整对象不存在。");
        // Reversing an existing adjustment must remain possible after the object is disabled.
        if(!reverse.HasValue && !entity.active) throw new AppException("调整对象已停用，请先启用。");
        if((kind=="receivable" && entity.kind is not ("customer" or "both")) || (kind=="payable" && entity.kind is not ("supplier" or "both")))
            throw new AppException("调整类型与往来单位身份不匹配。");
        var opening=kind=="account"?"opening_cents":kind=="receivable"?"opening_receivable_cents":"opening_payable_cents";
        var movement=kind=="account"
            ? "CASE WHEN (kind='income' AND account_id=$i) OR (kind='transfer' AND to_account_id=$i) THEN amount_cents WHEN (kind='expense' OR kind='transfer') AND account_id=$i THEN -amount_cents ELSE 0 END"
            : kind=="receivable"
                ? "CASE WHEN partner_id=$i AND (kind='receivable' OR nature='CustomerRefund') THEN amount_cents WHEN partner_id=$i AND nature IN ('CustomerReceipt','CustomerPrepay') THEN -amount_cents ELSE 0 END"
                : "CASE WHEN partner_id=$i AND (kind='payable' OR nature='SupplierRefund') THEN amount_cents WHEN partner_id=$i AND nature IN ('SupplierPayment','SupplierPrepay') THEN -amount_cents ELSE 0 END";
        var before=checked(new SqlCmd(conn,$"SELECT {opening}+(SELECT COALESCE(SUM({movement}),0) FROM vouchers WHERE status='audited' AND biz_date<=$d) FROM {table} WHERE id=$i",tx)
            .P("$i",target).P("$d",date).Long()+Sum(conn,kind,target,date,tx:tx));
        var after=checked(before+amount);
        new SqlCmd(conn,"""
            INSERT INTO balance_adjustments(request_id,kind,account_id,partner_id,effective_date,delta_cents,balance_before,balance_after,reason,created_at,created_by,user_id,reversal_of)
            VALUES($r,$k,$a,$p,$d,$v,$before,$after,$why,$t,$u,$uid,$reverse)
            """,tx).P("$r",request).P("$k",kind).P("$a",kind=="account"?target:null).P("$p",kind=="account"?null:target)
            .P("$d",date).P("$v",amount).P("$before",before).P("$after",after).P("$why",reason).P("$t",SqlUtil.Now()).P("$u",AppSession.UserName).P("$uid",AppSession.Current!.Id).P("$reverse",reverse).Exec();
        var id=new SqlCmd(conn,"SELECT last_insert_rowid()",tx).Long();
        AuditLogService.Write(conn,tx,reverse.HasValue?"balance-reverse":"balance-adjust","balance_adjustment",id,$"TZ{id:D8}",before.ToString(),
            $"{kind}/{target}/{date}/调整:{amount}/余额:{after}/反向原记录:{reverse}",reason);
        return id;
    }

    public static List<BalanceAdjustment> Query(string? from=null,string? to=null,string? kind=null,long? targetId=null)
    {
        if(from!=null) SqlUtil.ValidateDate(from);
        if(to!=null) SqlUtil.ValidateDate(to);
        using var conn=AppSession.Db.Open();
        AppSession.ValidateCurrent(conn);
        return Query(conn,from,to,kind,targetId);
    }

    internal static List<BalanceAdjustment> Query(SqliteConnection conn,string? from,string? to,string? kind,long? targetId)
    {
        return new SqlCmd(conn,"""
            SELECT a.*,COALESCE(a.account_id,a.partner_id) target,COALESCE(ac.name,p.name,'') target_name,
                EXISTS(SELECT 1 FROM balance_adjustments b WHERE b.reversal_of=a.id AND ($t IS NULL OR b.effective_date<=$t)) reversed
            FROM balance_adjustments a LEFT JOIN accounts ac ON ac.id=a.account_id LEFT JOIN partners p ON p.id=a.partner_id
            WHERE ($f IS NULL OR a.effective_date>=$f) AND ($t IS NULL OR a.effective_date<=$t)
              AND ($k IS NULL OR a.kind=$k) AND ($id IS NULL OR COALESCE(a.account_id,a.partner_id)=$id)
            ORDER BY a.effective_date DESC,a.id DESC
            """).P("$f",from).P("$t",to).P("$k",kind).P("$id",targetId).List(r=>new BalanceAdjustment(
                R.L(r,"id"),R.S(r,"kind"),R.L(r,"target"),R.S(r,"target_name"),R.S(r,"effective_date"),R.L(r,"delta_cents"),
                R.S(r,"reason"),R.S(r,"created_at"),R.S(r,"created_by"),R.LN(r,"reversal_of"),R.B(r,"reversed"),R.L(r,"balance_before"),R.L(r,"balance_after")));
    }

    public static long Sum(SqliteConnection conn,string kind,long targetId,string? through=null,string? from=null,SqliteTransaction? tx=null)
    {
        return new SqlCmd(conn,"SELECT COALESCE(SUM(delta_cents),0) FROM balance_adjustments WHERE kind=$k AND COALESCE(account_id,partner_id)=$i AND ($t IS NULL OR effective_date<=$t) AND ($f IS NULL OR effective_date>=$f)",tx)
            .P("$k",kind).P("$i",targetId).P("$t",through??SqlUtil.Today()).P("$f",from).Long();
    }
}
