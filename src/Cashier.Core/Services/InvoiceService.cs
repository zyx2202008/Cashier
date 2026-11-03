using Microsoft.Data.Sqlite;

namespace Cashier.Core.Services;

public class InvoiceService
{
    private const string SelectBase = @"
        SELECT i.*, COALESCE(p.name,'') AS partner_name,
          COALESCE((SELECT SUM(amount_cents * sign) FROM invoice_links il WHERE il.invoice_id=i.id),0) AS linked_cents
        FROM invoices i LEFT JOIN partners p ON p.id=i.partner_id";

    public List<Invoice> List(InvoiceKind? kind, string? keyword, long? partnerId, string? from, string? to, bool includeVoid = true)
    {
        using var conn = AppSession.Db.Open();
        var where = new List<string>();
        using var bag = new SqlCmd(conn, "");
        if (kind.HasValue) { where.Add("i.kind=$k"); bag.P("$k", kind.Value == InvoiceKind.Out ? "out" : "in"); }
        if (partnerId.HasValue) { where.Add("i.partner_id=$p"); bag.P("$p", partnerId); }
        if (!string.IsNullOrWhiteSpace(from)) { where.Add("i.reg_date >= $f"); bag.P("$f", from); }
        if (!string.IsNullOrWhiteSpace(to)) { where.Add("i.reg_date <= $t"); bag.P("$t", to); }
        if (!includeVoid) where.Add("i.status='valid'");
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            where.Add("(i.invoice_no LIKE $kw OR i.invoice_code LIKE $kw OR p.name LIKE $kw OR i.content LIKE $kw)");
            bag.P("$kw", "%" + keyword.Trim() + "%");
        }
        var sql = SelectBase + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") + " ORDER BY i.reg_date DESC, i.id DESC";
        using var cmd = new SqliteCommand(sql, conn);
        foreach (SqliteParameter p in bag.Parameters) cmd.Parameters.AddWithValue(p.ParameterName, p.Value ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        var list = new List<Invoice>();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    public Invoice? Get(long id)
    {
        using var conn = AppSession.Db.Open();
        return Get(conn, null, id);
    }

    private static Invoice? Get(SqliteConnection conn, SqliteTransaction? tx, long id) =>
        new SqlCmd(conn, SelectBase + " WHERE i.id=$i", tx).P("$i", id).First(Map);

    public static Invoice Map(SqliteDataReader r) => new()
    {
        Id = R.L(r, "id"),
        Revision = R.L(r, "revision"),
        Kind = R.S(r, "kind") == "out" ? InvoiceKind.Out : InvoiceKind.In,
        InvoiceType = R.S(r, "invoice_type"),
        InvoiceNo = R.S(r, "invoice_no"),
        InvoiceCode = R.SN(r, "invoice_code"),
        OriginalInvoiceId = R.LN(r, "original_invoice_id"),
        PartnerId = R.L(r, "partner_id"),
        RegDate = R.S(r, "reg_date"),
        DeliverDate = R.SN(r, "deliver_date"),
        GrossCents = R.L(r, "gross_cents") * R.L(r, "sign"),
        NetCents = R.L(r, "net_cents") * R.L(r, "sign"),
        TaxCents = R.L(r, "tax_cents") * R.L(r, "sign"),
        RateBp = R.I(r, "rate_bp"),
        Content = R.SN(r, "content"),
        Note = R.SN(r, "note"),
        Status = R.S(r, "status") == "void" ? InvoiceStatus.Void : InvoiceStatus.Valid,
        CreatedAt = R.S(r, "created_at"),
        CreatedBy = R.S(r, "created_by"),
        VoidedAt = R.SN(r, "voided_at"),
        VoidReason = R.SN(r, "void_reason"),
        PartnerName = R.S(r, "partner_name"),
        LinkedCents = R.L(r, "linked_cents"),
    };

    internal static void Validate(Invoice inv)
    {
        SqlUtil.ValidateDate(inv.RegDate, "登记日期");
        if (!string.IsNullOrWhiteSpace(inv.DeliverDate)) SqlUtil.ValidateDate(inv.DeliverDate, "交票日期");
        if (inv.PartnerId == 0) throw new AppException("请选择往来单位。");
        if (string.IsNullOrWhiteSpace(inv.InvoiceNo)) throw new AppException("请填写发票号码。");
        if (inv.InvoiceNo.Trim().Length > 32) throw new AppException("发票号码过长。");
        if (inv.GrossCents == 0) throw new AppException("含税金额不能为 0。");
        if (inv.GrossCents > Money.MaxCents || inv.GrossCents < -Money.MaxCents) throw new AppException("金额超出允许范围。");
        if (inv.NetCents > Money.MaxCents || inv.NetCents < -Money.MaxCents || inv.TaxCents > Money.MaxCents || inv.TaxCents < -Money.MaxCents)
            throw new AppException("票面金额超出允许范围。");
        if (inv.RateBp < 0 || inv.RateBp > 10000) throw new AppException("税率不正确。");
        if ((inv.GrossCents > 0 && (inv.NetCents <= 0 || inv.TaxCents < 0)) ||
            (inv.GrossCents < 0 && (inv.NetCents >= 0 || inv.TaxCents > 0)))
            throw new AppException("不含税金额和税额须与发票金额方向一致。");
        if (inv.GrossCents < 0 && inv.OriginalInvoiceId == null) throw new AppException("红字发票必须选择原有效蓝字发票。");
        if (inv.GrossCents > 0 && inv.OriginalInvoiceId != null) throw new AppException("蓝字发票不能选择原发票。");
        if (inv.NetCents + inv.TaxCents != inv.GrossCents)
            throw new AppException($"金额校验失败：不含税 {Money.Format(inv.NetCents)} + 税额 {Money.Format(inv.TaxCents)} ≠ 含税 {Money.Format(inv.GrossCents)}。");
        // 税率与票面税额必须自洽：按税率对不含税金额重算税额，允许整数分四舍五入产生的 1 分误差。
        // 防止“6% 税率 + 13% 税额”这类导入/手工改数造成的税务口径失真。
        var netAbs = Math.Abs(inv.NetCents);
        var taxAbs = Math.Abs(inv.TaxCents);
        var expectedTax = Money.GrossFromNet(netAbs, inv.RateBp).tax;
        if (Math.Abs(taxAbs - expectedTax) > 1)
            throw new AppException(
                $"税额与税率不一致：按 {InvoiceService.RateLabel(inv.RateBp)} 计算税额应为 {Money.Format(expectedTax)}，" +
                $"票面税额为 {Money.Format(taxAbs)}。请调整税率，或按税率重新计算不含税金额与税额。");
        if (string.IsNullOrWhiteSpace(inv.InvoiceType)) throw new AppException("请选择发票类型。");
    }

    public long Save(Invoice inv)
    {
        AppSession.Require(Perm.Invoice);
        using var conn = AppSession.Db.Open();
        using var tx = conn.BeginTransaction(deferred: false);
        try
        {
            var id = SaveInTransaction(inv, conn, tx);
            tx.Commit();
            if (inv.Id > 0) inv.Revision++;
            return id;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            tx.Rollback();
            throw new AppException($"发票无法保存：{ex.Message}");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    internal long SaveInTransaction(Invoice inv, SqliteConnection conn, SqliteTransaction tx, bool deferAutoLink = false)
    {
            AppSession.Require(Perm.Invoice, conn, tx);
            Validate(inv);
            var partnerKind = new SqlCmd(conn, "SELECT kind FROM partners WHERE id=$i", tx).P("$i", inv.PartnerId).Str();
            if (partnerKind != "both" && partnerKind != (inv.Kind == InvoiceKind.Out ? "customer" : "supplier"))
                throw new AppException(inv.Kind == InvoiceKind.Out ? "销项发票必须选择客户或客户兼供应商。" : "进项发票必须选择供应商或客户兼供应商。");
            var period = SqlUtil.PeriodOf(inv.RegDate);
            new SqlCmd(conn, "INSERT OR IGNORE INTO periods(period,status) VALUES($p,'open')", tx).P("$p", period).Exec();
            if (new SqlCmd(conn, "SELECT status FROM periods WHERE period=$p", tx).P("$p", period).Str() == "closed")
                throw new AppException($"{period} 期间已关闭，登记发票不允许操作。");
            if (inv.OriginalInvoiceId.HasValue)
            {
                var original = Get(conn, tx, inv.OriginalInvoiceId.Value) ?? throw new AppException("原发票不存在。");
                if (original.Id == inv.Id || original.Status != InvoiceStatus.Valid || original.GrossCents <= 0 ||
                    original.Kind != inv.Kind || original.PartnerId != inv.PartnerId)
                    throw new AppException("红字发票须关联同方向、同往来单位的有效蓝字发票。");
                if (string.CompareOrdinal(inv.RegDate, original.RegDate) < 0)
                    throw new AppException("红字发票登记日期不能早于原发票。");
                if (inv.RateBp != original.RateBp) throw new AppException("红字发票税率须与原单税率一致。");
                var redTotals = new SqlCmd(conn, @"SELECT COALESCE(SUM(gross_cents),0) g, COALESCE(SUM(net_cents),0) n, COALESCE(SUM(tax_cents),0) t FROM invoices
                    WHERE original_invoice_id=$o AND sign=-1 AND status='valid' AND id<>$id", tx)
                    .P("$o", original.Id).P("$id", inv.Id).List(r => (g:R.L(r,"g"),n:R.L(r,"n"),t:R.L(r,"t"))).First();
                if (redTotals.g + Math.Abs(inv.GrossCents) > original.GrossCents ||
                    redTotals.n + Math.Abs(inv.NetCents) > original.NetCents || redTotals.t + Math.Abs(inv.TaxCents) > original.TaxCents)
                    throw new AppException("红字发票累计含税、不含税金额或税额不能超过原发票。");
            }
            // 重复校验：同类型下有效发票号码唯一
            var dupSql = "SELECT COUNT(1) FROM invoices WHERE kind=$k AND invoice_no=$n AND status='valid'" + (inv.Id > 0 ? " AND id<>$i" : "");
            var dupCmd = new SqlCmd(conn, dupSql, tx).P("$k", inv.Kind == InvoiceKind.Out ? "out" : "in").P("$n", inv.InvoiceNo.Trim());
            if (inv.Id > 0) dupCmd.P("$i", inv.Id);
            if (dupCmd.Long() > 0)
                throw new AppException($"发票号码 {inv.InvoiceNo} 已登记，请勿重复录入。");

            if (inv.Id == 0)
            {
                new SqlCmd(conn, @"INSERT INTO invoices(kind,invoice_type,invoice_no,invoice_code,partner_id,reg_date,deliver_date,
                        gross_cents,net_cents,tax_cents,rate_bp,content,note,status,created_at,created_by,sign,original_invoice_id)
                    VALUES($k,$ty,$no,$code,$p,$d,$dd,$g,$n,$t,$r,$c,$nt,'valid',$at,$by,$sign,$original)", tx)
                    .P("$k", inv.Kind == InvoiceKind.Out ? "out" : "in").P("$ty", inv.InvoiceType)
                    .P("$no", inv.InvoiceNo.Trim()).P("$code", inv.InvoiceCode)
                    .P("$p", inv.PartnerId).P("$d", inv.RegDate).P("$dd", inv.DeliverDate)
                    .P("$g", Math.Abs(inv.GrossCents)).P("$n", Math.Abs(inv.NetCents)).P("$t", Math.Abs(inv.TaxCents)).P("$r", inv.RateBp)
                    .P("$sign", inv.GrossCents < 0 ? -1 : 1).P("$original", inv.OriginalInvoiceId)
                    .P("$c", inv.Content).P("$nt", inv.Note)
                    .P("$at", SqlUtil.Now()).P("$by", AppSession.UserName).Exec();
                var id = new SqlCmd(conn, "SELECT last_insert_rowid()", tx).Long();
                AuditLogService.Write(conn, tx, "create", "invoice", id, inv.InvoiceNo, null,
                    $"{inv.Kind}/{Money.Format(inv.GrossCents)}/{inv.RateLabel()}", null);
                if (inv.AutoLink)
                {
                    new SqlCmd(conn, "INSERT INTO meta(k,v) VALUES($k,'1')", tx).P("$k", "invoice.autoLink." + id).Exec();
                    if (!deferAutoLink) AutoLinkInTransaction(conn, tx, id);
                }
                return id;
            }
            else
            {
                var old = Get(conn, tx, inv.Id) ?? throw new AppException("发票不存在。");
                if (old.Status == InvoiceStatus.Void) throw new AppException("已作废发票不能修改。");
                if (old.Revision != inv.Revision) throw new AppException("发票已被其他窗口修改，请关闭编辑窗口，刷新后重新打开。");
                if (new SqlCmd(conn, "SELECT status FROM periods WHERE period=$p", tx).P("$p", SqlUtil.PeriodOf(old.RegDate)).Str() == "closed")
                    throw new AppException($"{SqlUtil.PeriodOf(old.RegDate)} 期间已关闭，修改原期间发票不允许操作。");
                if (old.LinkedCents != 0 && old.PartnerId != inv.PartnerId)
                    throw new AppException("该发票已关联业务，不能更改往来单位。请先解除关联。");
                if (old.LinkedCents != 0 && (Math.Sign(old.GrossCents) != Math.Sign(inv.GrossCents) || old.OriginalInvoiceId != inv.OriginalInvoiceId || old.RegDate != inv.RegDate))
                    throw new AppException("该发票已关联业务，不能修改发票方向、原发票或登记日期。请先解除关联。");
                if (old.LinkedCents != 0 && !AutoLinkEnabled(conn, tx, inv.Id) && old.GrossCents != inv.GrossCents)
                    throw new AppException("该发票已关联业务，请先解除关联后再修改金额。");
                if (old.Kind != inv.Kind) throw new AppException("发票类型不可更改。");
                if (old.GrossCents > 0 && new SqlCmd(conn, "SELECT COUNT(*) FROM invoices WHERE original_invoice_id=$i AND status='valid'", tx).P("$i", old.Id).Long() > 0)
                    throw new AppException("该发票存在有效红字发票，请先处理红字发票后再修改原票。");
                if (Math.Abs(old.LinkedCents) > Math.Abs(inv.GrossCents))
                    ReduceLinks(conn, tx, old, Math.Abs(old.LinkedCents) - Math.Abs(inv.GrossCents));
                var affected = new SqlCmd(conn, @"UPDATE invoices SET invoice_type=$ty,invoice_no=$no,invoice_code=$code,partner_id=$p,
                        reg_date=$d,deliver_date=$dd,gross_cents=$g,net_cents=$n,tax_cents=$t,rate_bp=$r,
                        content=$c,note=$nt,sign=$sign,original_invoice_id=$original,revision=revision+1 WHERE id=$i AND revision=$rev", tx)
                    .P("$ty", inv.InvoiceType).P("$no", inv.InvoiceNo.Trim()).P("$code", inv.InvoiceCode)
                    .P("$p", inv.PartnerId).P("$d", inv.RegDate).P("$dd", inv.DeliverDate)
                    .P("$g", Math.Abs(inv.GrossCents)).P("$n", Math.Abs(inv.NetCents)).P("$t", Math.Abs(inv.TaxCents)).P("$r", inv.RateBp)
                    .P("$sign", inv.GrossCents < 0 ? -1 : 1).P("$original", inv.OriginalInvoiceId)
                    .P("$c", inv.Content).P("$nt", inv.Note).P("$i", inv.Id).P("$rev", inv.Revision).Exec();
                if (affected != 1) throw new AppException("发票已被其他窗口修改，请刷新后重试。");
                AuditLogService.Write(conn, tx, "update", "invoice", inv.Id, inv.InvoiceNo,
                    $"{Money.Format(old.GrossCents)}", $"{Money.Format(inv.GrossCents)}", null);
                if (Math.Abs(inv.GrossCents) > Math.Abs(old.GrossCents) && AutoLinkEnabled(conn, tx, inv.Id)) AutoLinkInTransaction(conn, tx, inv.Id);
                return inv.Id;
            }
    }

    public void Void(long id, string reason)
    {
        AppSession.Require(Perm.Invoice);
        if (string.IsNullOrWhiteSpace(reason)) throw new AppException("作废必须填写原因。");
        using var conn = AppSession.Db.Open();
        using var tx = conn.BeginTransaction(deferred: false);
        var inv = Get(conn, tx, id) ?? throw new AppException("发票不存在。");
        if (inv.Status == InvoiceStatus.Void) throw new AppException("发票已作废。");
        if (new SqlCmd(conn, "SELECT COUNT(*) FROM invoices WHERE original_invoice_id=$i AND status='valid'", tx).P("$i", id).Long() > 0)
            throw new AppException("该发票存在有效红字发票，请先处理红字发票。");
        PeriodService.AssertOpen(SqlUtil.PeriodOf(inv.RegDate), "作废发票");
        if (inv.LinkedCents != 0 && !AutoLinkEnabled(conn, tx, inv.Id))
            throw new AppException("该发票已关联业务，请先解除关联后再作废。");
        ReduceLinks(conn, tx, inv, Math.Abs(inv.LinkedCents), "发票作废");
        new SqlCmd(conn, "UPDATE invoices SET status='void', voided_at=$t, voided_by=$u, void_reason=$r WHERE id=$i AND status='valid'", tx)
            .P("$t", SqlUtil.Now()).P("$u", AppSession.UserName).P("$r", reason).P("$i", id).Exec();
        AuditLogService.Write(conn, tx, "void", "invoice", id, inv.InvoiceNo, "valid", "void", reason);
        tx.Commit();
    }

    // Close the old association in history before reducing or deleting it.
    // Retain other associations so manually chosen allocations remain intact.
    private static void ReduceLinks(SqliteConnection conn, SqliteTransaction tx, Invoice invoice, long toRemove, string reason = "发票金额调整")
    {
        if (toRemove <= 0) return;
        var links = new SqlCmd(conn, @"SELECT l.id,l.voucher_id,l.amount_cents,l.sign,l.effective_date,
            EXISTS(SELECT 1 FROM meta m WHERE m.k='invoice.autoLinkRecord.' || l.id AND m.v='1') AS is_auto
            FROM invoice_links l WHERE l.invoice_id=$i ORDER BY is_auto DESC,l.id DESC", tx).P("$i",invoice.Id)
            .List(r => (id:R.L(r,"id"),voucher:R.L(r,"voucher_id"),amount:R.L(r,"amount_cents"),sign:R.L(r,"sign"),effective:R.S(r,"effective_date"), auto:R.B(r,"is_auto")));
        foreach (var link in links)
        {
            if (toRemove == 0) break;
            if (!link.auto) throw new AppException("发票存在人工关联，请先解除相关人工关联后再修改金额或作废。");
            var remove = Math.Min(toRemove, link.amount);
            var ended = string.CompareOrdinal(SqlUtil.Today(),link.effective) >= 0 ? SqlUtil.Today() : link.effective;
            PeriodService.AssertOpen(SqlUtil.PeriodOf(ended), reason);
            new SqlCmd(conn, @"INSERT INTO invoice_link_history(invoice_id,voucher_id,amount_cents,sign,effective_date,ended_date,ended_at,ended_by,reason)
                VALUES($i,$v,$a,$s,$e,$d,$t,$u,$r)", tx).P("$i",invoice.Id).P("$v",link.voucher).P("$a",link.amount)
                .P("$s",link.sign).P("$e",link.effective).P("$d",ended).P("$t",SqlUtil.Now()).P("$u",AppSession.UserName).P("$r",reason).Exec();
            if (remove == link.amount)
                new SqlCmd(conn,"DELETE FROM invoice_links WHERE id=$i",tx).P("$i",link.id).Exec();
            else
                new SqlCmd(conn,"UPDATE invoice_links SET amount_cents=$a,effective_date=$e WHERE id=$i",tx)
                    .P("$a",link.amount-remove).P("$e",ended).P("$i",link.id).Exec();
            AuditLogService.Write(conn,tx,"invoice-unlink","invoice",invoice.Id,invoice.InvoiceNo,Money.Format(remove),null,reason);
            toRemove -= remove;
        }
        if (toRemove != 0) throw new AppException("发票关联金额不一致，无法安全调整，请先核对账套。");
    }

    // ---------------- 发票与业务关联 ----------------
    public List<(long linkId, long voucherId, string docNo, string bizDate, string summary, long amount)> Links(long invoiceId)
    {
        using var conn = AppSession.Db.Open();
        return new SqlCmd(conn, @"SELECT il.id, il.voucher_id, il.amount_cents * il.sign AS amount_cents, v.doc_no, v.biz_date, v.summary
            FROM invoice_links il JOIN vouchers v ON v.id=il.voucher_id
            WHERE il.invoice_id=$i ORDER BY v.biz_date, v.doc_no").P("$i", invoiceId)
            .List(r => (R.L(r, "id"), R.L(r, "voucher_id"), R.S(r, "doc_no"), R.S(r, "biz_date"), R.S(r, "summary"), R.L(r, "amount_cents")));
    }

    /// <summary>可关联的业务单据（同往来单位、已审核、可开票、未开票金额 > 0）。</summary>
    public List<Voucher> InvoiceableVouchers(long partnerId, InvoiceKind kind, long? originalInvoiceId = null)
    {
        using var conn = AppSession.Db.Open();
        var kinds = kind == InvoiceKind.Out
            ? "((v.kind='receivable') OR (v.kind='income' AND v.nature='SaleCash'))"
            : "((v.kind='payable') OR (v.kind='expense' AND v.nature='PurchaseCash'))";
        var sql = $@"
            SELECT v.*,
              COALESCE((SELECT SUM(il.amount_cents * il.sign) FROM invoice_links il
                        JOIN invoices i ON i.id=il.invoice_id AND i.status='valid'
                        WHERE il.voucher_id=v.id),0) AS linked,
              COALESCE((SELECT SUM(il.amount_cents) FROM invoice_links il WHERE il.invoice_id=$orig AND il.voucher_id=v.id),0) AS original_linked,
              COALESCE((SELECT SUM(il.amount_cents) FROM invoice_links il JOIN invoices ri ON ri.id=il.invoice_id
                        WHERE ri.original_invoice_id=$orig AND ri.status='valid' AND il.voucher_id=v.id),0) AS already_red
            FROM vouchers v
            WHERE v.status='audited' AND v.invoiceable=1 AND v.partner_id=$p AND {kinds}
              AND ($orig IS NULL OR EXISTS(SELECT 1 FROM invoice_links ol WHERE ol.invoice_id=$orig AND ol.voucher_id=v.id))
            ORDER BY v.biz_date, v.doc_no";
        return new SqlCmd(conn, sql).P("$p", partnerId).P("$orig", originalInvoiceId)
            .List(r => new Voucher
            {
                Id = R.L(r, "id"), DocNo = R.S(r, "doc_no"), BizDate = R.S(r, "biz_date"),
                AmountCents = originalInvoiceId == null ? R.L(r, "amount_cents") : R.L(r, "original_linked"), Summary = R.S(r, "summary"),
                InvoiceLinkedCents = originalInvoiceId == null ? R.L(r, "linked") : R.L(r, "already_red"),
                Kind = Enum.TryParse<VoucherKind>(R.S(r, "kind"), true, out var k) ? k : VoucherKind.Receivable,
                Status = VoucherStatus.Audited,
            })
            .Where(v => v.UninvoicedCents > 0)
            .ToList();
    }

    public void Link(long invoiceId, long voucherId, long amountCents)
    {
        AppSession.Require(Perm.Invoice);
        using var conn = AppSession.Db.Open();
        using var tx = conn.BeginTransaction(deferred: false);
        LinkInTransaction(conn, tx, invoiceId, voucherId, amountCents);
        tx.Commit();
    }

    private static void LinkInTransaction(SqliteConnection conn, SqliteTransaction tx, long invoiceId, long voucherId, long amountCents, bool automatic = false)
    {
        if (amountCents <= 0) throw new AppException("关联金额必须大于 0。");
        var inv = Get(conn, tx, invoiceId) ?? throw new AppException("发票不存在。");
        if (inv.Status != InvoiceStatus.Valid) throw new AppException("已作废发票不能关联业务。");
        var v = new SqlCmd(conn, "SELECT * FROM vouchers WHERE id=$i", tx).P("$i", voucherId).First(VoucherService.Map)
            ?? throw new AppException("业务单据不存在。");
        if (v.Status != VoucherStatus.Audited) throw new AppException("只能关联已审核的业务单据。");
        if (!v.Invoiceable) throw new AppException("该单据标记为不可开票。");
        if (v.PartnerId != inv.PartnerId) throw new AppException("发票与业务单据的往来单位不一致。");
        var effective = string.CompareOrdinal(inv.RegDate, v.BizDate) >= 0 ? inv.RegDate : v.BizDate;
        var previous = new SqlCmd(conn, @"SELECT l.id,l.amount_cents,l.sign,l.effective_date,
            EXISTS(SELECT 1 FROM meta WHERE k='invoice.autoLinkRecord.' || l.id AND v='1') is_auto
            FROM invoice_links l WHERE l.invoice_id=$i AND l.voucher_id=$v", tx)
            .P("$i",invoiceId).P("$v",voucherId).First(r => (id:R.L(r,"id"),amount:R.L(r,"amount_cents"),sign:R.L(r,"sign"),effective:R.S(r,"effective_date"),auto:R.B(r,"is_auto")));
        if (previous.id != 0 && (!automatic || !previous.auto))
            throw new AppException("该发票与业务单据已建立关联，请先核对现有关联。");
        // 红票依赖当前蓝票与该业务的关联，不能在蓝票关联生效前冲减。
        if (inv.GrossCents < 0)
        {
            var originalEffective = new SqlCmd(conn, "SELECT effective_date FROM invoice_links WHERE invoice_id=$i AND voucher_id=$v", tx)
                .P("$i", inv.OriginalInvoiceId).P("$v", voucherId).Str();
            if (!string.IsNullOrEmpty(originalEffective) && string.CompareOrdinal(originalEffective, effective) > 0)
                effective = originalEffective;
        }
        var wasRemoved = new SqlCmd(conn, "SELECT COUNT(*) FROM invoice_link_history WHERE invoice_id=$i AND voucher_id=$v", tx)
            .P("$i", invoiceId).P("$v", voucherId).Long() > 0;
        if (previous.id != 0 && string.CompareOrdinal(previous.effective, effective) > 0) effective = previous.effective;
        if ((wasRemoved || previous.id != 0) && string.CompareOrdinal(SqlUtil.Today(), effective) > 0) effective = SqlUtil.Today();
        PeriodService.AssertOpen(SqlUtil.PeriodOf(effective), "发票关联生效期间");
        var allowed = inv.Kind == InvoiceKind.Out
            ? v.Kind == VoucherKind.Receivable || (v.Kind == VoucherKind.Income && v.Nature == VoucherNature.SaleCash)
            : v.Kind == VoucherKind.Payable || (v.Kind == VoucherKind.Expense && v.Nature == VoucherNature.PurchaseCash);
        if (!allowed) throw new AppException("发票方向与业务单据类型不匹配。");

        var invLeft = Math.Abs(inv.GrossCents - inv.LinkedCents);
        var usedOnVoucher = new SqlCmd(conn, @"SELECT COALESCE(SUM(il.amount_cents * il.sign),0) FROM invoice_links il
            JOIN invoices i ON i.id=il.invoice_id AND i.status='valid' WHERE il.voucher_id=$v", tx).P("$v", voucherId).Long();
        long vLeft;
        if (inv.GrossCents < 0)
        {
            var originalLinked = new SqlCmd(conn, "SELECT COALESCE(SUM(amount_cents),0) FROM invoice_links WHERE invoice_id=$i AND voucher_id=$v", tx)
                .P("$i", inv.OriginalInvoiceId).P("$v", voucherId).Long();
            var alreadyRed = new SqlCmd(conn, @"SELECT COALESCE(SUM(il.amount_cents),0) FROM invoice_links il
                JOIN invoices ri ON ri.id=il.invoice_id AND ri.status='valid'
                WHERE ri.original_invoice_id=$i AND il.voucher_id=$v", tx)
                .P("$i", inv.OriginalInvoiceId).P("$v", voucherId).Long();
            vLeft = Math.Min(usedOnVoucher, originalLinked - alreadyRed);
        }
        else vLeft = v.AmountCents - usedOnVoucher;
        if (amountCents > invLeft) throw new AppException($"关联金额超过发票未关联金额（剩余 {Money.Format(invLeft)}）。");
        if (amountCents > vLeft) throw new AppException($"关联金额超过业务未开票金额（剩余 {Money.Format(vLeft)}）。");

        var added = amountCents;
        if (previous.id != 0)
        {
            new SqlCmd(conn, @"INSERT INTO invoice_link_history(invoice_id,voucher_id,amount_cents,sign,effective_date,ended_date,ended_at,ended_by,reason)
                VALUES($i,$v,$a,$s,$e,$d,$t,$u,'自动关联追加')", tx)
                .P("$i",invoiceId).P("$v",voucherId).P("$a",previous.amount).P("$s",previous.sign)
                .P("$e",previous.effective).P("$d",effective).P("$t",SqlUtil.Now()).P("$u",AppSession.UserName).Exec();
            new SqlCmd(conn,"DELETE FROM invoice_links WHERE id=$i",tx).P("$i",previous.id).Exec();
            amountCents = checked(amountCents + previous.amount);
        }

        try
        {
            new SqlCmd(conn, "INSERT INTO invoice_links(invoice_id,voucher_id,amount_cents,sign,effective_date) VALUES($i,$v,$a,$s,$e)", tx)
                .P("$i", invoiceId).P("$v", voucherId).P("$a", amountCents).P("$s", inv.GrossCents < 0 ? -1 : 1).P("$e", effective).Exec();
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == 2067 && ex.Message.Contains("invoice_links.invoice_id", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException("该发票与业务单据已建立关联，请勿重复提交。");
        }
        if (automatic)
        {
            var linkId = new SqlCmd(conn, "SELECT last_insert_rowid()", tx).Long();
            new SqlCmd(conn, "INSERT INTO meta(k,v) VALUES($k,'1')", tx).P("$k", "invoice.autoLinkRecord." + linkId).Exec();
        }
        AuditLogService.Write(conn, tx, "invoice-link", "invoice", invoiceId, inv.InvoiceNo, null,
            $"{v.DocNo} {Money.Format(added)}", automatic ? "系统自动关联" : null);
    }

    private static bool AutoLinkEnabled(SqliteConnection conn, SqliteTransaction tx, long id) =>
        new SqlCmd(conn, "SELECT COUNT(*) FROM meta WHERE k=$k AND v='1'", tx).P("$k", "invoice.autoLink." + id).Long() == 1;

    internal static void TryAutoLinkInvoice(SqliteConnection conn, SqliteTransaction tx, long id) => AutoLinkInTransaction(conn, tx, id);

    // Only allocate if both sides have exactly one eligible candidate. Historical
    // manual invoices/links are never silently converted into auto links.
    private static void AutoLinkInTransaction(SqliteConnection conn, SqliteTransaction tx, long invoiceId)
    {
        var inv = Get(conn, tx, invoiceId)!;
        if (!AutoLinkEnabled(conn, tx, invoiceId)) return;
        if (new SqlCmd(conn, @"SELECT COUNT(*) FROM invoice_links l WHERE l.invoice_id=$i
            AND NOT EXISTS(SELECT 1 FROM meta WHERE k='invoice.autoLinkRecord.' || l.id AND v='1')",tx).P("$i",invoiceId).Long()>0)
            return; // A user-selected allocation makes this invoice manually managed.
        var competitors = new SqlCmd(conn, @"SELECT COUNT(*) FROM invoices i WHERE i.partner_id=$p AND i.kind=$k AND i.sign=$s AND i.status='valid'
            AND i.gross_cents > COALESCE((SELECT SUM(l.amount_cents) FROM invoice_links l WHERE l.invoice_id=i.id),0)", tx)
            .P("$p", inv.PartnerId).P("$k", inv.Kind == InvoiceKind.Out ? "out" : "in").P("$s", inv.GrossCents < 0 ? -1 : 1).Long();
        if (competitors != 1) return;
        var voucherKind = inv.Kind == InvoiceKind.Out ? "receivable" : "payable";
        var candidates = new SqlCmd(conn, @"SELECT v.id, v.amount_cents, v.biz_date,
            COALESCE((SELECT SUM(il.amount_cents * il.sign) FROM invoice_links il
              JOIN invoices i ON i.id=il.invoice_id AND i.status='valid' WHERE il.voucher_id=v.id),0) AS linked
            FROM vouchers v WHERE v.partner_id=$p AND v.kind=$k AND v.status='audited' AND v.invoiceable=1
            AND (EXISTS(SELECT 1 FROM invoice_links x JOIN meta m ON m.k='invoice.autoLinkRecord.' || x.id AND m.v='1'
                        WHERE x.invoice_id=$invoice AND x.voucher_id=v.id)
                 OR (NOT EXISTS(SELECT 1 FROM invoice_links x WHERE x.invoice_id=$invoice AND x.voucher_id=v.id)
                     AND NOT EXISTS(SELECT 1 FROM invoice_link_history h WHERE h.invoice_id=$invoice AND h.voucher_id=v.id)))
            AND ($orig IS NULL OR EXISTS(SELECT 1 FROM invoice_links ol WHERE ol.invoice_id=$orig AND ol.voucher_id=v.id))
            ORDER BY v.biz_date,v.id", tx).P("$p", inv.PartnerId).P("$k", voucherKind).P("$orig", inv.OriginalInvoiceId).P("$invoice", invoiceId)
            .List(r => (id:R.L(r,"id"), amount:R.L(r,"amount_cents"), date:R.S(r,"biz_date"), linked:R.L(r,"linked")));
        long left = Math.Abs(inv.GrossCents) - Math.Abs(inv.LinkedCents);
        var availableCandidates = new List<(long id, long amount, string effective)>();
        foreach (var v in candidates)
        {
            var effective = string.CompareOrdinal(inv.RegDate,v.date) >= 0 ? inv.RegDate : v.date;
            long available = v.amount - v.linked;
            if (inv.GrossCents < 0)
            {
                var original = new SqlCmd(conn,"SELECT COALESCE(SUM(amount_cents),0) FROM invoice_links WHERE invoice_id=$i AND voucher_id=$v",tx)
                    .P("$i",inv.OriginalInvoiceId).P("$v",v.id).Long();
                var red = new SqlCmd(conn,@"SELECT COALESCE(SUM(il.amount_cents),0) FROM invoice_links il JOIN invoices ri ON ri.id=il.invoice_id
                    WHERE ri.original_invoice_id=$i AND ri.status='valid' AND il.voucher_id=$v",tx).P("$i",inv.OriginalInvoiceId).P("$v",v.id).Long();
                available = Math.Min(v.linked, original-red);
            }
            if (available <= 0) continue;
            availableCandidates.Add((v.id, available, effective));
        }
        if (left > 0 && availableCandidates.Count == 1 &&
            new SqlCmd(conn,"SELECT status FROM periods WHERE period=$p",tx).P("$p",SqlUtil.PeriodOf(availableCandidates[0].effective)).Str() != "closed")
            LinkInTransaction(conn,tx,invoiceId,availableCandidates[0].id,Math.Min(left,availableCandidates[0].amount), automatic:true);
    }

    // Called after a voucher is audited, in the same transaction, to handle
    // invoices entered before their underlying business.
    internal static void AutoLinkForAuditedVoucher(SqliteConnection conn, SqliteTransaction tx, long voucherId)
    {
        var voucher = new SqlCmd(conn, "SELECT partner_id,kind,invoiceable FROM vouchers WHERE id=$i AND status='audited'", tx)
            .P("$i", voucherId).First(r => (partner:R.LN(r,"partner_id"), kind:R.S(r,"kind"), invoiceable:R.B(r,"invoiceable")));
        if (voucher.partner == null || !voucher.invoiceable || voucher.kind is not ("receivable" or "payable")) return;
        var kind = voucher.kind == "receivable" ? "out" : "in";
        var ids = new SqlCmd(conn, @"SELECT i.id FROM invoices i WHERE i.partner_id=$p AND i.kind=$k AND i.status='valid'
            AND i.gross_cents > COALESCE((SELECT SUM(l.amount_cents) FROM invoice_links l WHERE l.invoice_id=i.id),0)
            ORDER BY i.sign DESC,i.reg_date,i.id", tx).P("$p",voucher.partner).P("$k",kind).List(r => R.L(r,"id"));
        foreach (var id in ids) AutoLinkInTransaction(conn,tx,id);
    }

    public void Unlink(long linkId, string reason)
    {
        AppSession.Require(Perm.Invoice);
        if (string.IsNullOrWhiteSpace(reason)) throw new AppException("解除关联必须填写原因。");
        using var conn = AppSession.Db.Open();
        using var tx = conn.BeginTransaction();
        var row = new SqlCmd(conn, "SELECT * FROM invoice_links WHERE id=$i", tx).P("$i", linkId)
            .First(r => (inv: R.L(r, "invoice_id"), v: R.L(r, "voucher_id"), amt: R.L(r, "amount_cents"), sign: R.L(r, "sign"), effective: R.S(r, "effective_date")));
        if (row.inv == 0) throw new AppException("关联记录不存在。");
        var inv = Get(conn, tx, row.inv)!;
        var voucher = new VoucherService().Get(row.v) ?? throw new AppException("关联业务不存在。");
        var effective = row.effective;
        var ended = string.CompareOrdinal(SqlUtil.Today(), effective) >= 0 ? SqlUtil.Today() : effective;
        PeriodService.AssertOpen(SqlUtil.PeriodOf(ended), "解除关联生效期间");
        if (row.sign > 0 && new SqlCmd(conn, @"SELECT COUNT(*) FROM invoice_links il JOIN invoices ri ON ri.id=il.invoice_id
            WHERE ri.status='valid' AND ri.original_invoice_id=$i AND il.voucher_id=$v", tx).P("$i", inv.Id).P("$v", row.v).Long() > 0)
            throw new AppException("该业务关联已被红字发票冲减，请先解除红字关联。");
        new SqlCmd(conn, @"INSERT INTO invoice_link_history(invoice_id,voucher_id,amount_cents,sign,effective_date,ended_date,ended_at,ended_by,reason)
            VALUES($i,$v,$a,$s,$e,$d,$t,$u,$r)", tx)
            .P("$i", row.inv).P("$v", row.v).P("$a", row.amt).P("$s", row.sign).P("$e", effective)
            .P("$d", ended).P("$t", SqlUtil.Now()).P("$u", AppSession.UserName).P("$r", reason).Exec();
        new SqlCmd(conn, "DELETE FROM invoice_links WHERE id=$i", tx).P("$i", linkId).Exec();
        AuditLogService.Write(conn, tx, "invoice-unlink", "invoice", row.inv, inv.InvoiceNo, Money.Format(row.amt), null, reason);
        tx.Commit();
    }

    /// <summary>往来单位开票汇总：可开票业务金额、已关联有效发票金额、未开票金额。</summary>
    public (long invoiceable, long invoiced, long uninvoiced) PartnerInvoiceSummary(long partnerId, InvoiceKind kind, string? asOf = null)
    {
        if (asOf != null) SqlUtil.ValidateDate(asOf, "统计截止日期");
        using var conn = AppSession.Db.Open();
        var kinds = kind == InvoiceKind.Out
            ? "((v.kind='receivable') OR (v.kind='income' AND v.nature='SaleCash'))"
            : "((v.kind='payable') OR (v.kind='expense' AND v.nature='PurchaseCash'))";
        var invoiceable = new SqlCmd(conn, $@"SELECT COALESCE(SUM(amount_cents),0) FROM vouchers v
            WHERE v.status='audited' AND v.invoiceable=1 AND v.partner_id=$p AND {kinds}
              AND ($t IS NULL OR v.biz_date <= $t)").P("$p", partnerId).P("$t", asOf).Long();
        var invoiced = new SqlCmd(conn, @"SELECT COALESCE(SUM(il.amount_cents * il.sign),0) FROM invoice_links il
            JOIN invoices i ON i.id=il.invoice_id AND i.status='valid'
            JOIN vouchers v ON v.id=il.voucher_id AND v.status='audited'
            WHERE i.partner_id=$p AND i.kind=$k
              AND ($t IS NULL OR (i.reg_date <= $t AND v.biz_date <= $t AND il.effective_date <= $t))")
            .P("$p", partnerId).P("$k", kind == InvoiceKind.Out ? "out" : "in").P("$t", asOf).Long();
        if (asOf != null)
            invoiced += new SqlCmd(conn, @"SELECT COALESCE(SUM(h.amount_cents * h.sign),0) FROM invoice_link_history h
                JOIN invoices i ON i.id=h.invoice_id JOIN vouchers v ON v.id=h.voucher_id
                WHERE i.partner_id=$p AND i.kind=$k AND h.effective_date <= $t AND h.ended_date > $t
                  AND (i.status='valid' OR substr(i.voided_at,1,10)>$t) AND v.status='audited'", null)
                .P("$p", partnerId).P("$k", kind == InvoiceKind.Out ? "out" : "in").P("$t", asOf).Long();
        return (invoiceable, invoiced, Math.Max(0, invoiceable - invoiced));
    }

    public static string RateLabel(int bp) => (bp / 100m).ToString("0.##") + "%";

    public static int ParseRate(string text)
    {
        var t = (text ?? "").Replace("%", "").Trim();
        if (!decimal.TryParse(t, out var d)) throw new AppException("税率格式不正确。");
        if (d < 0 || d > 100) throw new AppException("税率必须在 0% ~ 100% 之间。");
        if (decimal.Round(d, 2) != d) throw new AppException("税率最多保留两位小数。");
        return (int)(d * 100m);
    }
}

internal static class InvoiceExt
{
    public static string RateLabel(this Invoice i) => InvoiceService.RateLabel(i.RateBp);
}
