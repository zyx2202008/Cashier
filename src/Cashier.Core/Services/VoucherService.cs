using Microsoft.Data.Sqlite;

namespace Cashier.Core.Services;

public class VoucherQuery
{
    public string? From { get; set; }
    public string? To { get; set; }
    public VoucherKind? Kind { get; set; }
    public VoucherStatus? Status { get; set; }
    public VoucherNature? Nature { get; set; }
    public long? AccountId { get; set; }
    public long? PartnerId { get; set; }
    public string? PartnerKind { get; set; }
    public long? ProjectId { get; set; }
    public long? DeptId { get; set; }
    public long? SubjectId { get; set; }
    public string? Keyword { get; set; }
    public bool OnlyAudited { get; set; }
    public bool IncludeVoid { get; set; } = true;
    public bool OnlyUnsettled { get; set; }
    public int Limit { get; set; } = 5000;
}

public class VoucherService
{
    private const string SelectBase = @"
        SELECT v.*,
          COALESCE(a.name,'') AS account_name,
          COALESCE(a2.name,'') AS to_account_name,
          COALESCE(p.name,'') AS partner_name,
          COALESCE(pr.name,'') AS project_name,
          COALESCE(d.name,'') AS dept_name,
          COALESCE(s.name,'') AS subject_name,
          COALESCE((SELECT SUM(amount_cents) FROM settlements st WHERE st.biz_voucher_id=v.id),0) AS settled_cents,
          COALESCE((SELECT SUM(il.amount_cents * il.sign) FROM invoice_links il
                    JOIN invoices i ON i.id=il.invoice_id AND i.status='valid'
                    WHERE il.voucher_id=v.id),0) AS invoice_linked_cents
        FROM vouchers v
        LEFT JOIN accounts a ON a.id=v.account_id
        LEFT JOIN accounts a2 ON a2.id=v.to_account_id
        LEFT JOIN partners p ON p.id=v.partner_id
        LEFT JOIN projects pr ON pr.id=v.project_id
        LEFT JOIN depts d ON d.id=v.dept_id
        LEFT JOIN subjects s ON s.id=v.subject_id";

    public List<Voucher> Query(VoucherQuery q)
    {
        using var conn = AppSession.Db.Open();
        var where = new List<string>();
        using var cmd = new SqlCmd(conn, "");
        void Add(string cond, string name, object? val)
        {
            where.Add(cond);
            cmd.P(name, val);
        }
        if (!string.IsNullOrWhiteSpace(q.From)) Add("v.biz_date >= $from", "$from", q.From);
        if (!string.IsNullOrWhiteSpace(q.To)) Add("v.biz_date <= $to", "$to", q.To);
        if (q.Kind.HasValue) Add("v.kind = $kind", "$kind", q.Kind.Value.ToString().ToLower());
        if (q.Status.HasValue) Add("v.status = $st", "$st", q.Status.Value.ToString().ToLower());
        if (q.Nature.HasValue) Add("v.nature = $nat", "$nat", q.Nature.Value.ToString());
        if (q.AccountId.HasValue) Add("(v.account_id=$acc OR (v.kind='transfer' AND v.to_account_id=$acc))", "$acc", q.AccountId);
        if (q.PartnerId.HasValue) Add("v.partner_id = $pid", "$pid", q.PartnerId);
        if (q.ProjectId.HasValue) Add("v.project_id = $prj", "$prj", q.ProjectId);
        if (q.DeptId.HasValue) Add("v.dept_id = $dep", "$dep", q.DeptId);
        if (q.SubjectId.HasValue) Add("v.subject_id = $sub", "$sub", q.SubjectId);
        if (q.OnlyAudited) where.Add("v.status = 'audited'");
        if (!q.IncludeVoid) where.Add("v.status <> 'void'");
        if (q.OnlyUnsettled) where.Add("v.kind IN ('receivable','payable') AND v.amount_cents > COALESCE((SELECT SUM(amount_cents) FROM settlements st WHERE st.biz_voucher_id=v.id),0)");
        if (!string.IsNullOrWhiteSpace(q.PartnerKind))
        {
            var k = q.PartnerKind == "customer" ? "('customer','both')" : "('supplier','both')";
            where.Add($"p.kind IN {k}");
        }
        if (!string.IsNullOrWhiteSpace(q.Keyword))
            Add("(v.doc_no LIKE $kw OR v.summary LIKE $kw OR p.name LIKE $kw OR v.handler LIKE $kw OR v.note LIKE $kw)", "$kw", "%" + q.Keyword.Trim() + "%");

        var sql = SelectBase + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
                  + " ORDER BY v.biz_date DESC, v.doc_no DESC LIMIT " + Math.Clamp(q.Limit, 1, 20000);

        // 复用同一个命令的参数
        var result = new List<Voucher>();
        using var final = new SqliteCommand(sql, conn);
        foreach (SqliteParameter p in cmd.Parameters)
            final.Parameters.AddWithValue(p.ParameterName, p.Value ?? DBNull.Value);
        using var r = final.ExecuteReader();
        while (r.Read()) result.Add(Map(r));
        return result;
    }

    public Voucher? Get(long id)
    {
        using var conn = AppSession.Db.Open();
        return Get(conn, null, id);
    }

    private static Voucher? Get(SqliteConnection conn, SqliteTransaction? tx, long id)
    {
        var cmd = new SqlCmd(conn, SelectBase + " WHERE v.id=$i", tx).P("$i", id);
        using (cmd)
        {
            return cmd.First(Map);
        }
    }

    public static Voucher Map(SqliteDataReader r) => new()
    {
        Id = R.L(r, "id"),
        Revision = SafeLong(r, "revision"),
        DocNo = R.S(r, "doc_no"),
        Kind = Enum.TryParse<VoucherKind>(R.S(r, "kind"), true, out var k) ? k : VoucherKind.Income,
        Nature = NatureInfo.Parse(R.S(r, "nature")),
        BizDate = R.S(r, "biz_date"),
        Period = R.S(r, "period"),
        AmountCents = R.L(r, "amount_cents"),
        AccountId = R.LN(r, "account_id"),
        ToAccountId = R.LN(r, "to_account_id"),
        FeeCents = R.L(r, "fee_cents"),
        PartnerId = R.LN(r, "partner_id"),
        ProjectId = R.LN(r, "project_id"),
        DeptId = R.LN(r, "dept_id"),
        Handler = R.SN(r, "handler"),
        SubjectId = R.LN(r, "subject_id"),
        Summary = R.S(r, "summary"),
        Note = R.SN(r, "note"),
        Status = Enum.TryParse<VoucherStatus>(R.S(r, "status"), true, out var st) ? st : VoucherStatus.Draft,
        CountAsRevenue = R.B(r, "count_as_revenue"),
        CountAsCost = R.B(r, "count_as_cost"),
        Invoiceable = R.B(r, "invoiceable"),
        CreatedAt = R.S(r, "created_at"),
        CreatedBy = R.S(r, "created_by"),
        UpdatedAt = R.SN(r, "updated_at"),
        UpdatedBy = R.SN(r, "updated_by"),
        AuditedAt = R.SN(r, "audited_at"),
        AuditedBy = R.SN(r, "audited_by"),
        VoidedAt = R.SN(r, "voided_at"),
        VoidedBy = R.SN(r, "voided_by"),
        VoidReason = R.SN(r, "void_reason"),
        RefId = R.LN(r, "ref_id"),
        AccountName = SafeStr(r, "account_name"),
        ToAccountName = SafeStr(r, "to_account_name"),
        PartnerName = SafeStr(r, "partner_name"),
        ProjectName = SafeStr(r, "project_name"),
        DeptName = SafeStr(r, "dept_name"),
        SubjectName = SafeStr(r, "subject_name"),
        SettledCents = SafeLong(r, "settled_cents"),
        InvoiceLinkedCents = SafeLong(r, "invoice_linked_cents"),
    };

    private static bool HasCol(SqliteDataReader r, string name)
    {
        for (var i = 0; i < r.FieldCount; i++)
            if (string.Equals(r.GetName(i), name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string SafeStr(SqliteDataReader r, string name) => HasCol(r, name) ? R.S(r, name) : "";
    private static long SafeLong(SqliteDataReader r, string name) => HasCol(r, name) ? R.L(r, name) : 0;

    // ---------------- 校验 ----------------
    internal static void Validate(Voucher v)
    {
        SqlUtil.ValidateDate(v.BizDate, "业务日期");
        if (v.AmountCents <= 0) throw new AppException("金额必须大于 0。");
        if (v.AmountCents > Money.MaxCents) throw new AppException("金额超出允许范围。");
        if (string.IsNullOrWhiteSpace(v.Summary)) throw new AppException("请填写摘要。");
        if (v.Summary.Length > 200) throw new AppException("摘要不能超过 200 个字符。");
        if (v.Kind != VoucherKind.Transfer && v.ToAccountId != null) throw new AppException("非转账单据不能指定转入账户。");
        if (v.Kind != VoucherKind.Transfer && v.FeeCents != 0) throw new AppException("非转账单据不能填写转账手续费。");

        switch (v.Kind)
        {
            case VoucherKind.Income:
                if (!NatureInfo.IsInflow(v.Nature)) throw new AppException("收入单据的业务性质不正确。");
                if (v.AccountId == null) throw new AppException("请选择入账账户。");
                if (v.Nature is VoucherNature.CustomerReceipt or VoucherNature.CustomerPrepay && v.PartnerId == null)
                    throw new AppException("客户回款/预收必须选择客户。");
                break;
            case VoucherKind.Expense:
                if (!NatureInfo.IsOutflow(v.Nature)) throw new AppException("支出单据的业务性质不正确。");
                if (v.AccountId == null) throw new AppException("请选择付款账户。");
                if (v.Nature is VoucherNature.SupplierPayment or VoucherNature.SupplierPrepay && v.PartnerId == null)
                    throw new AppException("供应商付款/预付必须选择供应商。");
                break;
            case VoucherKind.Receivable:
                if (v.PartnerId == null) throw new AppException("应收单必须选择客户。");
                if (v.AccountId != null) throw new AppException("应收单不涉及资金账户。");
                break;
            case VoucherKind.Payable:
                if (v.PartnerId == null) throw new AppException("应付单必须选择供应商。");
                if (v.AccountId != null) throw new AppException("应付单不涉及资金账户。");
                break;
            case VoucherKind.Transfer:
                if (v.AccountId == null || v.ToAccountId == null) throw new AppException("请选择转出与转入账户。");
                if (v.AccountId == v.ToAccountId) throw new AppException("转出账户与转入账户不能相同。");
                if (v.FeeCents < 0) throw new AppException("手续费不能为负数。");
                if (v.FeeCents > Money.MaxCents) throw new AppException("手续费超出允许范围。");
                break;
        }
    }

    private static string NextDocNo(SqliteConnection conn, SqliteTransaction tx, VoucherKind kind, string date)
    {
        var prefix = KindInfo.Prefix(kind) + date.Replace("-", "");
        var last = new SqlCmd(conn, "SELECT COALESCE(MAX(CAST(substr(doc_no,$start) AS INTEGER)),0) FROM vouchers WHERE doc_no LIKE $p", tx)
            .P("$start", prefix.Length + 1).P("$p", prefix + "%").Long();
        // 高水位持久化到 meta：删除单据后新单号不复用已删除的序号，保证审计日志中单号唯一。
        var key = "doc_no_seq." + prefix;
        var storedRaw = new SqlCmd(conn, "SELECT v FROM meta WHERE k=$k", tx).P("$k", key).Str();
        var stored = long.TryParse(storedRaw, out var n) && n > 0 ? n : 0;
        var seq = checked(Math.Max(last, stored) + 1);
        new SqlCmd(conn, "INSERT INTO meta(k,v) VALUES($k,$v) ON CONFLICT(k) DO UPDATE SET v=$v", tx)
            .P("$k", key).P("$v", seq.ToString()).Exec();
        return prefix + seq.ToString("000");
    }

    private static void ValidatePartner(Voucher v, SqliteConnection conn, SqliteTransaction? tx)
    {
        var customer = v.Kind == VoucherKind.Receivable || NatureInfo.AffectsReceivable(v.Nature);
        var supplier = v.Kind == VoucherKind.Payable || NatureInfo.AffectsPayable(v.Nature);
        if (!customer && !supplier) return;
        if (v.PartnerId == null) throw new AppException(customer ? "该业务必须选择客户。" : "该业务必须选择供应商。");
        var kind = new SqlCmd(conn, "SELECT kind FROM partners WHERE id=$i", tx).P("$i", v.PartnerId).Str();
        if (kind != "both" && kind != (customer ? "customer" : "supplier"))
            throw new AppException(customer ? "该业务的往来单位必须是客户或客户兼供应商。" : "该业务的往来单位必须是供应商或客户兼供应商。");
    }

    internal static void ValidateReferences(Voucher v, SqliteConnection conn, SqliteTransaction? tx, Voucher? old = null)
    {
        ValidatePartner(v, conn, tx);
        void Enabled(string table, long? id, long? previous, string label)
        {
            if (!id.HasValue) return;
            var active = new SqlCmd(conn, $"SELECT is_active FROM {table} WHERE id=$i", tx).P("$i", id)
                .First(r => (exists: true, active: r.GetInt64(0) == 1));
            if (!active.exists) throw new AppException(label + "不存在，请重新选择或预览。");
            if (!active.active && (old == null || previous != id))
                throw new AppException(label + "已停用，请重新选择或预览。");
        }
        Enabled("accounts", v.AccountId, old?.AccountId, "资金账户");
        Enabled("accounts", v.ToAccountId, old?.ToAccountId, "转入账户");
        Enabled("partners", v.PartnerId, old?.PartnerId, "往来单位");
        Enabled("projects", v.ProjectId, old?.ProjectId, "项目");
        Enabled("depts", v.DeptId, old?.DeptId, "部门");
        Enabled("subjects", v.SubjectId, old?.SubjectId, "科目");
    }

    private static void AssertPeriodOpen(SqliteConnection conn, SqliteTransaction tx, string period, string action)
    {
        if (new SqlCmd(conn, "SELECT status FROM periods WHERE period=$p", tx).P("$p", period).Str() == "closed")
            throw new AppException($"{period} 期间已关闭，{action}不允许操作。请先由管理员重新开启期间。");
    }

    // ---------------- 新增 / 修改 ----------------
    public long Create(Voucher v, bool submit = true)
    {
        AppSession.Require(Perm.CreateVoucher);
        using var conn = AppSession.Db.Open();
        using var tx = conn.BeginTransaction();
        try
        {
            var id = CreateInTransaction(v, submit, conn, tx);
            tx.Commit();
            return id;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    internal long CreateInTransaction(Voucher v, bool submit, SqliteConnection conn, SqliteTransaction tx)
    {
            AppSession.Require(Perm.CreateVoucher, conn, tx);
            Validate(v);
            ValidateReferences(v, conn, tx);
            var period = SqlUtil.PeriodOf(v.BizDate);
            new SqlCmd(conn, "INSERT OR IGNORE INTO periods(period,status) VALUES($p,'open')", tx).P("$p", period).Exec();
            if (new SqlCmd(conn, "SELECT status FROM periods WHERE period=$p", tx).P("$p", period).Str() == "closed")
                throw new AppException($"{period} 期间已关闭，新增单据不允许操作。请先由管理员重新开启期间。");
            var docNo = NextDocNo(conn, tx, v.Kind, v.BizDate);
            new SqlCmd(conn, @"INSERT INTO vouchers(doc_no,kind,nature,biz_date,period,amount_cents,account_id,to_account_id,
                    fee_cents,partner_id,project_id,dept_id,handler,subject_id,summary,note,status,count_as_revenue,count_as_cost,
                    invoiceable,created_at,created_by)
                VALUES($no,$kind,$nat,$date,$per,$amt,$acc,$acc2,$fee,$pid,$prj,$dep,$h,$sub,$sum,$note,$st,$cr,$cc,$inv,$t,$u)", tx)
                .P("$no", docNo).P("$kind", v.Kind.ToString().ToLower()).P("$nat", v.Nature.ToString())
                .P("$date", v.BizDate).P("$per", period).P("$amt", v.AmountCents)
                .P("$acc", v.AccountId).P("$acc2", v.ToAccountId).P("$fee", v.FeeCents)
                .P("$pid", v.PartnerId).P("$prj", v.ProjectId).P("$dep", v.DeptId).P("$h", v.Handler)
                .P("$sub", v.SubjectId).P("$sum", v.Summary.Trim()).P("$note", v.Note)
                .P("$st", submit ? "pending" : "draft")
                .P("$cr", v.CountAsRevenue ? 1 : 0).P("$cc", v.CountAsCost ? 1 : 0).P("$inv", v.Invoiceable ? 1 : 0)
                .P("$t", SqlUtil.Now()).P("$u", AppSession.UserName)
                .Exec();
            var id = new SqlCmd(conn, "SELECT last_insert_rowid()", tx).Long();
            v.Id = id;
            v.DocNo = docNo;
            v.Status = submit ? VoucherStatus.Pending : VoucherStatus.Draft;

            // 转账手续费：单独生成一笔支出单据（同一事务）
            if (v.Kind == VoucherKind.Transfer) SyncTransferFee(conn, tx, v);

            AuditLogService.Write(conn, tx, "create", "voucher", id, docNo, null,
                $"{KindInfo.Label(v.Kind)}/{NatureInfo.Label(v.Nature)}/{Money.Format(v.AmountCents)}/{v.Summary}", null);
            return id;
    }

    public void Update(Voucher v)
    {
        AppSession.Require(Perm.CreateVoucher);
        Validate(v);
        var period = SqlUtil.PeriodOf(v.BizDate);

        using var conn = AppSession.Db.Open();
        using var tx = conn.BeginTransaction();
        var old = Get(conn, tx, v.Id) ?? throw new AppException("单据不存在。");
        AppSession.Require(Perm.CreateVoucher, conn, tx);
        if (old.RefId != null) throw new AppException("转账手续费由关联转账单管理，请修改转账单。");
        if (old.Kind != v.Kind) throw new AppException("不能更改单据类型。");
        if (old.Status is VoucherStatus.Audited or VoucherStatus.Void)
            throw new AppException($"单据 {old.DocNo} 已{(old.Status == VoucherStatus.Audited ? "审核" : "作废")}，不能修改。请先反审核或使用冲销。");
        if (old.Revision != v.Revision) throw new AppException("单据已被其他窗口修改，请关闭编辑窗口，刷新后重新打开。");
        ValidateReferences(v, conn, tx, old);
        AssertPeriodOpen(conn, tx, old.Period, "修改单据");
        AssertPeriodOpen(conn, tx, period, "修改单据");
        new SqlCmd(conn, "INSERT OR IGNORE INTO periods(period,status) VALUES($p,'open')", tx).P("$p", period).Exec();

        var affected = new SqlCmd(conn, @"UPDATE vouchers SET nature=$nat,biz_date=$date,period=$per,amount_cents=$amt,
                account_id=$acc,to_account_id=$acc2,fee_cents=$fee,partner_id=$pid,project_id=$prj,dept_id=$dep,
                handler=$h,subject_id=$sub,summary=$sum,note=$note,count_as_revenue=$cr,count_as_cost=$cc,
                invoiceable=$inv,updated_at=$t,updated_by=$u,revision=revision+1
            WHERE id=$i AND revision=$rev AND status IN ('draft','pending')", tx)
            .P("$nat", v.Nature.ToString()).P("$date", v.BizDate).P("$per", period).P("$amt", v.AmountCents)
            .P("$acc", v.AccountId).P("$acc2", v.ToAccountId).P("$fee", v.FeeCents).P("$pid", v.PartnerId)
            .P("$prj", v.ProjectId).P("$dep", v.DeptId).P("$h", v.Handler).P("$sub", v.SubjectId)
            .P("$sum", v.Summary.Trim()).P("$note", v.Note).P("$cr", v.CountAsRevenue ? 1 : 0)
            .P("$cc", v.CountAsCost ? 1 : 0).P("$inv", v.Invoiceable ? 1 : 0)
            .P("$t", SqlUtil.Now()).P("$u", AppSession.UserName).P("$i", v.Id).P("$rev", v.Revision)
            .Exec();
        if (affected != 1) throw new AppException("单据状态已变化，请刷新后重试。");

        v.DocNo = old.DocNo;
        v.Status = old.Status;
        if (v.Kind == VoucherKind.Transfer) SyncTransferFee(conn, tx, v);

        AuditLogService.Write(conn, tx, "update", "voucher", v.Id, old.DocNo,
            $"{Money.Format(old.AmountCents)}/{old.BizDate}/{old.Summary}",
            $"{Money.Format(v.AmountCents)}/{v.BizDate}/{v.Summary}", null);
        tx.Commit();
        v.Revision = old.Revision + 1;
    }

    // ---------------- 状态流转 ----------------
    public void Submit(long id)
    {
        AppSession.Require(Perm.CreateVoucher);
        using var conn = AppSession.Db.Open();
        using var tx = conn.BeginTransaction();
        var v = Get(conn, tx, id) ?? throw new AppException("单据不存在。");
        if (v.RefId != null) throw new AppException("转账手续费随转账单处理。");
        if (v.Status != VoucherStatus.Draft) throw new AppException("只有草稿可以提交。");
        AppSession.Require(Perm.CreateVoucher, conn, tx);
        AssertPeriodOpen(conn, tx, v.Period, "提交草稿");
        var n = new SqlCmd(conn, "UPDATE vouchers SET status='pending' WHERE id=$i AND status='draft'", tx).P("$i", id).Exec();
        if (n != 1) throw new AppException("单据状态已变化，请刷新。");
        AuditLogService.Write(conn, tx, "submit", "voucher", id, v.DocNo, "draft", "pending", null);
        tx.Commit();
    }

    public void Audit(long id)
    {
        AppSession.Require(Perm.AuditVoucher);
        using var conn = AppSession.Db.Open();
        using var tx = conn.BeginTransaction();
        var v = Get(conn, tx, id) ?? throw new AppException("单据不存在。");
        if (v.RefId != null) throw new AppException("转账手续费随转账单审核，请审核关联转账单。");
        if (v.Status == VoucherStatus.Audited) throw new AppException($"单据 {v.DocNo} 已审核，请勿重复操作。");
        if (v.Status == VoucherStatus.Void) throw new AppException($"单据 {v.DocNo} 已作废，不能审核。");
        ValidatePartner(v, conn, tx);
        PeriodService.AssertOpen(v.Period, "审核单据");
        if (v.Kind == VoucherKind.Transfer)
        {
            SyncTransferFee(conn, tx, v);
            var fee = ActiveFee(conn, tx, id);
            if (fee != null && fee.Status != VoucherStatus.Audited)
            {
                var updated = new SqlCmd(conn, "UPDATE vouchers SET status='audited',audited_at=$t,audited_by=$u WHERE id=$i AND status IN ('draft','pending')", tx)
                    .P("$t", SqlUtil.Now()).P("$u", AppSession.UserName).P("$i", fee.Id).Exec();
                if (updated != 1) throw new AppException("手续费状态已变化，请刷新后重试。");
                AuditLogService.Write(conn, tx, "audit", "voucher", fee.Id, fee.DocNo, fee.Status.ToString(), "audited", "随转账审核");
            }
        }
        if (v.Kind == VoucherKind.Receivable || v.Kind == VoucherKind.Payable)
        {
            if (v.PartnerId == null) throw new AppException("单据缺少往来单位，无法审核。");
        }
        var n = new SqlCmd(conn, @"UPDATE vouchers SET status='audited', audited_at=$t, audited_by=$u
            WHERE id=$i AND status IN ('draft','pending')", tx)
            .P("$t", SqlUtil.Now()).P("$u", AppSession.UserName).P("$i", id).Exec();
        if (n != 1) throw new AppException("单据状态已变化（可能已被审核），请刷新后重试。");
        if (v.Kind is VoucherKind.Receivable or VoucherKind.Payable)
            InvoiceService.AutoLinkForAuditedVoucher(conn, tx, id);
        AuditLogService.Write(conn, tx, "audit", "voucher", id, v.DocNo, v.Status.ToString(), "audited", null);
        tx.Commit();
    }

    public void Unaudit(long id, string reason)
    {
        AppSession.Require(Perm.AuditVoucher);
        if (string.IsNullOrWhiteSpace(reason)) throw new AppException("反审核必须填写原因。");
        using var conn = AppSession.Db.Open();
        using var tx = conn.BeginTransaction();
        var v = Get(conn, tx, id) ?? throw new AppException("单据不存在。");
        if (v.RefId != null) throw new AppException("转账手续费随转账单反审核，请操作关联转账单。");
        if (v.Status != VoucherStatus.Audited) throw new AppException("只有已审核单据可以反审核。");
        PeriodService.AssertOpen(v.Period, "反审核单据");
        EnsureNoDownstream(conn, tx, id, v.DocNo, "反审核", v.Kind == VoucherKind.Transfer);
        if (v.Kind == VoucherKind.Transfer)
        {
            var fee = ActiveFee(conn, tx, id);
            if (fee != null && fee.Status == VoucherStatus.Audited)
            {
                EnsureNoDownstream(conn, tx, fee.Id, fee.DocNo, "反审核");
                new SqlCmd(conn, "UPDATE vouchers SET status='pending',audited_at=NULL,audited_by=NULL WHERE id=$i AND status='audited'", tx).P("$i", fee.Id).Exec();
                AuditLogService.Write(conn, tx, "unaudit", "voucher", fee.Id, fee.DocNo, "audited", "pending", reason);
            }
        }

        var n = new SqlCmd(conn, @"UPDATE vouchers SET status='pending', audited_at=NULL, audited_by=NULL
            WHERE id=$i AND status='audited'", tx).P("$i", id).Exec();
        if (n != 1) throw new AppException("单据状态已变化，请刷新后重试。");
        AuditLogService.Write(conn, tx, "unaudit", "voucher", id, v.DocNo, "audited", "pending", reason);
        tx.Commit();
    }

    public void Void(long id, string reason)
    {
        AppSession.Require(Perm.VoidVoucher);
        if (string.IsNullOrWhiteSpace(reason)) throw new AppException("作废必须填写原因。");
        using var conn = AppSession.Db.Open();
        using var tx = conn.BeginTransaction();
        var v = Get(conn, tx, id) ?? throw new AppException("单据不存在。");
        if (v.RefId != null) throw new AppException("转账手续费随转账单作废，请操作关联转账单。");
        if (v.Status == VoucherStatus.Void) throw new AppException("单据已作废。");
        PeriodService.AssertOpen(v.Period, "作废单据");
        EnsureNoDownstream(conn, tx, id, v.DocNo, "作废", v.Kind == VoucherKind.Transfer);
        if (v.Kind == VoucherKind.Transfer)
        {
            var fee = ActiveFee(conn, tx, id);
            if (fee != null)
            {
                EnsureNoDownstream(conn, tx, fee.Id, fee.DocNo, "作废");
                new SqlCmd(conn, "UPDATE vouchers SET status='void',voided_at=$t,voided_by=$u,void_reason=$r WHERE id=$i AND status<>'void'", tx)
                    .P("$t", SqlUtil.Now()).P("$u", AppSession.UserName).P("$r", reason).P("$i", fee.Id).Exec();
                AuditLogService.Write(conn, tx, "void", "voucher", fee.Id, fee.DocNo, fee.Status.ToString(), "void", reason);
            }
        }

        var n = new SqlCmd(conn, @"UPDATE vouchers SET status='void', voided_at=$t, voided_by=$u, void_reason=$r
            WHERE id=$i AND status <> 'void'", tx)
            .P("$t", SqlUtil.Now()).P("$u", AppSession.UserName).P("$r", reason).P("$i", id).Exec();
        if (n != 1) throw new AppException("单据状态已变化，请刷新后重试。");
        AuditLogService.Write(conn, tx, "void", "voucher", id, v.DocNo, v.Status.ToString(), "void", reason);
        tx.Commit();
    }

    public void DeleteDraft(long id)
    {
        AppSession.Require(Perm.CreateVoucher);
        using var conn = AppSession.Db.Open();
        using var tx = conn.BeginTransaction();
        var v = Get(conn, tx, id) ?? throw new AppException("单据不存在。");
        if (v.RefId != null) throw new AppException("转账手续费随转账单处理。");
        if (v.Status is VoucherStatus.Audited) throw new AppException("已审核单据不能删除，请先反审核或作废。");
        if (v.Status is VoucherStatus.Void) throw new AppException("单据已作废，无需删除。");
        EnsureNoDownstream(conn, tx, id, v.DocNo, "删除", v.Kind == VoucherKind.Transfer);
        var fee = v.Kind == VoucherKind.Transfer ? ActiveFee(conn, tx, id) : null;
        if (fee?.Status == VoucherStatus.Audited) throw new AppException("手续费已审核，请先核对转账单。");
        if (v.Kind == VoucherKind.Transfer && new SqlCmd(conn, "SELECT COUNT(*) FROM vouchers WHERE ref_id=$i", tx).P("$i", id).Long() > 0)
        {
            new SqlCmd(conn, "UPDATE vouchers SET status='void',voided_at=$t,voided_by=$u,void_reason='删除未审核转账' WHERE ref_id=$i AND status<>'void'", tx)
                .P("$t", SqlUtil.Now()).P("$u", AppSession.UserName).P("$i", id).Exec();
            new SqlCmd(conn, "UPDATE vouchers SET status='void',voided_at=$t,voided_by=$u,void_reason='删除未审核转账' WHERE id=$i AND status IN ('draft','pending')", tx)
                .P("$t", SqlUtil.Now()).P("$u", AppSession.UserName).P("$i", id).Exec();
        }
        else new SqlCmd(conn, "DELETE FROM vouchers WHERE id=$i AND status IN ('draft','pending')", tx).P("$i", id).Exec();
        AuditLogService.Write(conn, tx, "delete", "voucher", id, v.DocNo, v.Status.ToString(), null, null);
        tx.Commit();
    }

    /// <summary>下游依赖检查：核销、发票关联、手续费关联单。</summary>
    private static void EnsureNoDownstream(SqliteConnection conn, SqliteTransaction tx, long id, string docNo, string action, bool includeManagedFee = false)
    {
        var st = new SqlCmd(conn, "SELECT COUNT(1) FROM settlements WHERE pay_voucher_id=$i OR biz_voucher_id=$i", tx)
            .P("$i", id).Long();
        if (st > 0) throw new AppException($"单据 {docNo} 存在 {st} 条核销记录，请先取消核销后再{action}。");

        var il = new SqlCmd(conn, "SELECT COUNT(1) FROM invoice_links WHERE voucher_id=$i", tx).P("$i", id).Long();
        if (il > 0) throw new AppException($"单据 {docNo} 已关联发票，请先解除发票关联后再{action}。");

        var fee = new SqlCmd(conn, "SELECT COUNT(1) FROM vouchers WHERE ref_id=$i AND status <> 'void'", tx).P("$i", id).Long();
        if (fee > 0 && !includeManagedFee) throw new AppException($"单据 {docNo} 存在关联的手续费单据，请先处理后再{action}。");
    }

    private static Voucher? ActiveFee(SqliteConnection conn, SqliteTransaction tx, long transferId)
    {
        var fees = new SqlCmd(conn, "SELECT * FROM vouchers WHERE ref_id=$i AND status<>'void'", tx)
            .P("$i", transferId).List(Map);
        if (fees.Count > 1) throw new AppException("转账存在多笔有效手续费，请先核对旧账数据。");
        return fees.FirstOrDefault();
    }

    private static void SyncTransferFee(SqliteConnection conn, SqliteTransaction tx, Voucher transfer)
    {
        var fee = ActiveFee(conn, tx, transfer.Id);
        if (fee != null && fee.Status == VoucherStatus.Audited)
        {
            if (transfer.FeeCents != fee.AmountCents || transfer.AccountId != fee.AccountId || transfer.BizDate != fee.BizDate)
                throw new AppException("已审核的转账手续费与转账不一致，请先核对旧账数据。");
            return;
        }
        if (transfer.FeeCents == 0)
        {
            if (fee != null)
            {
                new SqlCmd(conn, "UPDATE vouchers SET status='void',voided_at=$t,voided_by=$u,void_reason='转账手续费调整为零' WHERE id=$i", tx)
                    .P("$t", SqlUtil.Now()).P("$u", AppSession.UserName).P("$i", fee.Id).Exec();
                AuditLogService.Write(conn, tx, "void", "voucher", fee.Id, fee.DocNo, fee.Status.ToString(), "void", "转账手续费调整为零");
            }
            return;
        }
        var period = SqlUtil.PeriodOf(transfer.BizDate);
        if (fee == null)
        {
            var feeNo = NextDocNo(conn, tx, VoucherKind.Expense, transfer.BizDate);
            new SqlCmd(conn, @"INSERT INTO vouchers(doc_no,kind,nature,biz_date,period,amount_cents,account_id,
                    fee_cents,summary,status,created_at,created_by,ref_id,count_as_revenue,count_as_cost,invoiceable)
                VALUES($no,'expense','OtherOut',$date,$per,$amt,$acc,0,$sum,$st,$t,$u,$ref,0,1,0)", tx)
                .P("$no", feeNo).P("$date", transfer.BizDate).P("$per", period).P("$amt", transfer.FeeCents)
                .P("$acc", transfer.AccountId).P("$sum", $"转账手续费（{transfer.DocNo}）")
                .P("$st", transfer.Status == VoucherStatus.Draft ? "draft" : "pending")
                .P("$t", SqlUtil.Now()).P("$u", AppSession.UserName).P("$ref", transfer.Id).Exec();
            var feeId = new SqlCmd(conn, "SELECT last_insert_rowid()", tx).Long();
            AuditLogService.Write(conn, tx, "create", "voucher", feeId, feeNo, null, Money.Format(transfer.FeeCents), "随转账生成手续费");
        }
        else
        {
            new SqlCmd(conn, @"UPDATE vouchers SET biz_date=$d,period=$p,amount_cents=$a,account_id=$acc,
                    summary=$s,updated_at=$t,updated_by=$u WHERE id=$i AND status IN ('draft','pending')", tx)
                .P("$d", transfer.BizDate).P("$p", period).P("$a", transfer.FeeCents).P("$acc", transfer.AccountId)
                .P("$s", $"转账手续费（{transfer.DocNo}）").P("$t", SqlUtil.Now()).P("$u", AppSession.UserName).P("$i", fee.Id).Exec();
            AuditLogService.Write(conn, tx, "update", "voucher", fee.Id, fee.DocNo, Money.Format(fee.AmountCents), Money.Format(transfer.FeeCents), "同步转账手续费");
        }
    }

    // ---------------- 统计 ----------------
    public static (long inflow, long outflow) CashFlow(string from, string to, VoucherKind? kind = null)
    {
        using var conn = AppSession.Db.Open();
        var extra = kind.HasValue ? " AND kind=$k" : "";
        using var cmd = new SqlCmd(conn, $@"
            SELECT COALESCE(SUM(CASE WHEN kind='income' THEN amount_cents ELSE 0 END),0) AS inflow,
                   COALESCE(SUM(CASE WHEN kind='expense' THEN amount_cents ELSE 0 END),0) AS outflow
            FROM vouchers WHERE status='audited' AND biz_date BETWEEN $f AND $t AND kind IN ('income','expense'){extra}")
            .P("$f", from).P("$t", to);
        if (kind.HasValue) cmd.P("$k", kind.Value.ToString().ToLower());
        var x = cmd.List(r => (i: R.L(r, "inflow"), o: R.L(r, "outflow"))).First();
        return (x.i, x.o);
    }

    public static long TotalFunds(string? asOf = null)
    {
        var accounts = MasterDataService.ListAccounts(true, asOf);
        return accounts.Sum(a => a.BalanceCents);
    }
}
