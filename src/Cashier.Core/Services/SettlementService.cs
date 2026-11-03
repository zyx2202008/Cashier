using Microsoft.Data.Sqlite;

namespace Cashier.Core.Services;

public class SettlementService
{
    public long UnallocatedOf(long payVoucherId)
    {
        using var conn = AppSession.Db.Open();
        var amount = new SqlCmd(conn, "SELECT amount_cents FROM vouchers WHERE id=$i").P("$i", payVoucherId).Long();
        var used = new SqlCmd(conn, "SELECT COALESCE(SUM(amount_cents),0) FROM settlements WHERE pay_voucher_id=$i")
            .P("$i", payVoucherId).Long();
        return amount - used;
    }

    public long UnsettledOf(long bizVoucherId)
    {
        using var conn = AppSession.Db.Open();
        var amount = new SqlCmd(conn, "SELECT amount_cents FROM vouchers WHERE id=$i").P("$i", bizVoucherId).Long();
        var used = new SqlCmd(conn, "SELECT COALESCE(SUM(amount_cents),0) FROM settlements WHERE biz_voucher_id=$i")
            .P("$i", bizVoucherId).Long();
        return amount - used;
    }

    public List<Settlement> ListByPay(long payId)
    {
        using var conn = AppSession.Db.Open();
        return new SqlCmd(conn, @"SELECT s.*, pv.doc_no AS pay_doc_no, bv.doc_no AS biz_doc_no, bv.biz_date AS biz_date, bv.summary AS biz_summary
            FROM settlements s
            JOIN vouchers pv ON pv.id=s.pay_voucher_id
            JOIN vouchers bv ON bv.id=s.biz_voucher_id
            WHERE s.pay_voucher_id=$i ORDER BY bv.biz_date, bv.doc_no").P("$i", payId)
            .List(r => new Settlement
            {
                Id = R.L(r, "id"), PayVoucherId = R.L(r, "pay_voucher_id"), BizVoucherId = R.L(r, "biz_voucher_id"),
                AmountCents = R.L(r, "amount_cents"), CreatedAt = R.S(r, "created_at"), CreatedBy = R.S(r, "created_by"),
                PayDocNo = R.S(r, "pay_doc_no"), BizDocNo = R.S(r, "biz_doc_no"),
                BizDate = R.S(r, "biz_date"), BizSummary = R.S(r, "biz_summary"),
            });
    }

    public List<Settlement> ListByBiz(long bizId)
    {
        using var conn = AppSession.Db.Open();
        return new SqlCmd(conn, @"SELECT s.*, pv.doc_no AS pay_doc_no, bv.doc_no AS biz_doc_no, bv.biz_date AS biz_date, bv.summary AS biz_summary
            FROM settlements s
            JOIN vouchers pv ON pv.id=s.pay_voucher_id
            JOIN vouchers bv ON bv.id=s.biz_voucher_id
            WHERE s.biz_voucher_id=$i ORDER BY pv.biz_date, pv.doc_no").P("$i", bizId)
            .List(r => new Settlement
            {
                Id = R.L(r, "id"), PayVoucherId = R.L(r, "pay_voucher_id"), BizVoucherId = R.L(r, "biz_voucher_id"),
                AmountCents = R.L(r, "amount_cents"), CreatedAt = R.S(r, "created_at"), CreatedBy = R.S(r, "created_by"),
                PayDocNo = R.S(r, "pay_doc_no"), BizDocNo = R.S(r, "biz_doc_no"),
                BizDate = R.S(r, "biz_date"), BizSummary = R.S(r, "biz_summary"),
            });
    }

    /// <summary>核销：只建立分配关系，不改变往来净额。</summary>
    public long Allocate(long payId, long bizId, long amountCents)
    {
        AppSession.Require(Perm.Settle);
        using var conn = AppSession.Db.Open();
        using var tx = conn.BeginTransaction();
        try
        {
            var id = AllocateInTransaction(conn, tx, payId, bizId, amountCents);
            tx.Commit();
            return id;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private static long AllocateInTransaction(SqliteConnection conn, SqliteTransaction tx, long payId, long bizId, long amountCents, bool extendExisting = false)
    {
            if (amountCents <= 0) throw new AppException("核销金额必须大于 0。");
            var pay = GetInTx(conn, tx, payId) ?? throw new AppException("收付款单据不存在。");
            var biz = GetInTx(conn, tx, bizId) ?? throw new AppException("业务单据不存在。");
            if (pay.Status != VoucherStatus.Audited) throw new AppException("只有已审核的收付款单据可以核销。");
            if (biz.Status != VoucherStatus.Audited) throw new AppException("只有已审核的应收/应付单可以核销。");
            var effective = string.CompareOrdinal(pay.BizDate, biz.BizDate) >= 0 ? pay.BizDate : biz.BizDate;
            var previous = new SqlCmd(conn, "SELECT id, amount_cents, effective_date FROM settlements WHERE pay_voucher_id=$p AND biz_voucher_id=$b", tx)
                .P("$p", payId).P("$b", bizId).First(r => (id: R.L(r, "id"), amount: R.L(r, "amount_cents"), effective: R.S(r, "effective_date")));
            if (previous.id != 0 && !extendExisting) throw new AppException("该笔收付款与业务单据已存在核销记录，请勿重复提交。");
            var wasRemoved = previous.id != 0 || new SqlCmd(conn, "SELECT COUNT(*) FROM settlement_history WHERE pay_voucher_id=$p AND biz_voucher_id=$b", tx)
                .P("$p", payId).P("$b", bizId).Long() > 0;
            if (wasRemoved && string.CompareOrdinal(SqlUtil.Today(), effective) > 0) effective = SqlUtil.Today();
            PeriodService.AssertOpen(SqlUtil.PeriodOf(effective), "核销生效期间");
            if (!NatureInfo.IsSettleSource(pay.Nature)) throw new AppException("该单据类型不支持核销。");
            if (biz.Kind is not (VoucherKind.Receivable or VoucherKind.Payable)) throw new AppException("只有应收/应付单可以核销。");

            var isCustomer = biz.Kind == VoucherKind.Receivable;
            if (isCustomer && pay.Nature is not (VoucherNature.CustomerReceipt or VoucherNature.CustomerPrepay))
                throw new AppException("应收单只能用客户回款或预收核销。");
            if (!isCustomer && pay.Nature is not (VoucherNature.SupplierPayment or VoucherNature.SupplierPrepay))
                throw new AppException("应付单只能用供应商付款或预付核销。");
            if (pay.PartnerId != biz.PartnerId)
                throw new AppException("收付款单据与业务单据的往来单位不一致，不能核销。");

            var payLeft = pay.AmountCents - Sum(conn, tx, "pay_voucher_id", payId);
            var bizLeft = biz.AmountCents - Sum(conn, tx, "biz_voucher_id", bizId);
            if (amountCents > payLeft) throw new AppException($"核销金额超过该收付款的可分配余额（剩余 {Money.Format(payLeft)}）。");
            if (amountCents > bizLeft) throw new AppException($"核销金额超过该业务单据的未结金额（剩余 {Money.Format(bizLeft)}）。");

            var addedAmount = amountCents;
            // Preserve the earlier allocation as history when adding to it.
            if (previous.id != 0)
            {
                new SqlCmd(conn, @"INSERT INTO settlement_history(pay_voucher_id,biz_voucher_id,amount_cents,effective_date,ended_date,ended_at,ended_by,reason)
                    VALUES($p,$b,$a,$e,$d,$t,$u,$r)", tx)
                    .P("$p", payId).P("$b", bizId).P("$a", previous.amount).P("$e", previous.effective)
                    .P("$d", effective).P("$t", SqlUtil.Now()).P("$u", AppSession.UserName).P("$r", "自动核销追加").Exec();
                new SqlCmd(conn, "DELETE FROM settlements WHERE id=$i", tx).P("$i", previous.id).Exec();
                amountCents += previous.amount;
            }
            try
            {
                new SqlCmd(conn, @"INSERT INTO settlements(pay_voucher_id,biz_voucher_id,amount_cents,created_at,created_by,effective_date)
                    VALUES($p,$b,$a,$t,$u,$e)", tx)
                    .P("$p", payId).P("$b", bizId).P("$a", amountCents)
                    .P("$t", SqlUtil.Now()).P("$u", AppSession.UserName).P("$e", effective).Exec();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                throw new AppException("该笔收付款与业务单据已存在核销记录，请勿重复提交。");
            }
            var id = new SqlCmd(conn, "SELECT last_insert_rowid()", tx).Long();
            AuditLogService.Write(conn, tx, "settle", "voucher", payId, pay.DocNo, null,
                $"核销 {biz.DocNo} {Money.Format(addedAmount)}", null);
            return id;
    }

    public void Unallocate(long settlementId, string reason)
    {
        AppSession.Require(Perm.Settle);
        if (string.IsNullOrWhiteSpace(reason)) throw new AppException("取消核销必须填写原因。");
        using var conn = AppSession.Db.Open();
        using var tx = conn.BeginTransaction();
        var row = new SqlCmd(conn, "SELECT * FROM settlements WHERE id=$i", tx).P("$i", settlementId)
            .First(r => (pay: R.L(r, "pay_voucher_id"), biz: R.L(r, "biz_voucher_id"), amt: R.L(r, "amount_cents"), effective: R.S(r, "effective_date")));
        if (row.pay == 0) throw new AppException("核销记录不存在。");
        var pay = GetInTx(conn, tx, row.pay)!;
        var biz = GetInTx(conn, tx, row.biz)!;
        var effective = row.effective;
        // 历史收口日一律取今天：过去生效的核销今天解除（与原行为一致）；
        // 尚未生效（生效日晚于今天）的核销还没有进入任何历史区间，也按今天解除，
        // 避免其生效期间被关闭后“必须先重开未来期间才能撤销”的死结。
        var ended = SqlUtil.Today();
        PeriodService.AssertOpen(SqlUtil.PeriodOf(ended), "取消核销生效期间");
        new SqlCmd(conn, @"INSERT INTO settlement_history(pay_voucher_id,biz_voucher_id,amount_cents,effective_date,ended_date,ended_at,ended_by,reason)
            VALUES($p,$b,$a,$e,$d,$t,$u,$r)", tx)
            .P("$p", row.pay).P("$b", row.biz).P("$a", row.amt).P("$e", effective)
            .P("$d", ended).P("$t", SqlUtil.Now()).P("$u", AppSession.UserName).P("$r", reason).Exec();
        new SqlCmd(conn, "DELETE FROM settlements WHERE id=$i", tx).P("$i", settlementId).Exec();
        AuditLogService.Write(conn, tx, "unsettle", "voucher", row.pay, pay.DocNo, Money.Format(row.amt), null, reason);
        tx.Commit();
    }

    /// <summary>按业务日期先后自动分配，返回分配明细。</summary>
    public List<(long bizId, string docNo, long amount)> AutoAllocate(long payId)
    {
        AppSession.Require(Perm.Settle);
        using var conn = AppSession.Db.Open();
        using var tx = conn.BeginTransaction();
        var pay = GetInTx(conn, tx, payId) ?? throw new AppException("收付款单据不存在。");
        if (pay.Status != VoucherStatus.Audited) throw new AppException("只有已审核的收付款单据可以核销。");
        if (pay.PartnerId == null) throw new AppException("该单据没有往来单位，无法自动核销。");
        if (!NatureInfo.IsSettleSource(pay.Nature)) throw new AppException("该单据类型不支持核销。");

        var isCustomer = pay.Nature is VoucherNature.CustomerReceipt or VoucherNature.CustomerPrepay;
        // A UI query limit must not truncate the accounting allocation plan.
        var bizList = new SqlCmd(conn, @"SELECT v.*,
            COALESCE((SELECT SUM(s.amount_cents) FROM settlements s WHERE s.biz_voucher_id=v.id),0) AS settled_cents
            FROM vouchers v WHERE v.kind=$kind AND v.partner_id=$p AND v.status='audited'
            AND v.amount_cents > COALESCE((SELECT SUM(s.amount_cents) FROM settlements s WHERE s.biz_voucher_id=v.id),0)
            ORDER BY v.biz_date, length(v.doc_no), v.doc_no, v.id", tx)
            .P("$kind", isCustomer ? "receivable" : "payable").P("$p", pay.PartnerId).List(VoucherService.Map);
        var left = pay.AmountCents - Sum(conn, tx, "pay_voucher_id", payId);
        var planLeft = left;
        foreach (var b in bizList)
        {
            if (planLeft <= 0) break;
            if (b.UnsettledCents <= 0) continue;
            var effective = string.CompareOrdinal(pay.BizDate, b.BizDate) >= 0 ? pay.BizDate : b.BizDate;
            if (new SqlCmd(conn, "SELECT (SELECT COUNT(*) FROM settlement_history WHERE pay_voucher_id=$p AND biz_voucher_id=$b) + (SELECT COUNT(*) FROM settlements WHERE pay_voucher_id=$p AND biz_voucher_id=$b)", tx)
                .P("$p", payId).P("$b", b.Id).Long() > 0 && string.CompareOrdinal(SqlUtil.Today(), effective) > 0) effective = SqlUtil.Today();
            PeriodService.AssertOpen(SqlUtil.PeriodOf(effective), "自动核销生效期间");
            planLeft -= Math.Min(planLeft, b.UnsettledCents);
        }
        var result = new List<(long, string, long)>();
        foreach (var b in bizList)
        {
            if (left <= 0) break;
            var alloc = Math.Min(left, b.UnsettledCents);
            if (alloc <= 0) continue;
            AllocateInTransaction(conn, tx, payId, b.Id, alloc, extendExisting: true);
            result.Add((b.Id, b.DocNo, alloc));
            left -= alloc;
        }
        tx.Commit();
        return result;
    }

    private static long Sum(SqliteConnection conn, SqliteTransaction tx, string col, long id) =>
        new SqlCmd(conn, $"SELECT COALESCE(SUM(amount_cents),0) FROM settlements WHERE {col}=$i", tx).P("$i", id).Long();

    /// <summary>列出某往来单位可用的核销来源（已审核回款/预收或付款/预付，且有可分配余额），按业务日期先后。</summary>
    public List<Voucher> ListPaySources(long partnerId, bool isCustomer)
    {
        using var conn = AppSession.Db.Open();
        var kind = isCustomer ? "income" : "expense";
        var natures = isCustomer ? "('CustomerReceipt','CustomerPrepay')" : "('SupplierPayment','SupplierPrepay')";
        return new SqlCmd(conn, $@"
            SELECT v.*,
              COALESCE((SELECT SUM(s.amount_cents) FROM settlements s WHERE s.pay_voucher_id=v.id),0) AS settled_cents
            FROM vouchers v
            WHERE v.status='audited' AND v.kind='{kind}' AND v.nature IN {natures} AND v.partner_id=$p
              AND v.amount_cents > COALESCE((SELECT SUM(s.amount_cents) FROM settlements s WHERE s.pay_voucher_id=v.id),0)
            ORDER BY v.biz_date, length(v.doc_no), v.doc_no, v.id")
            .P("$p", partnerId)
            .List(VoucherService.Map);
    }

    private static Voucher? GetInTx(SqliteConnection conn, SqliteTransaction tx, long id) =>
        new SqlCmd(conn, "SELECT * FROM vouchers WHERE id=$i", tx).P("$i", id).First(VoucherService.Map);
}
