using System.Globalization;

namespace Cashier.Core.Services;

public partial class ReportService
{
    private ReportResult MonthlyDebt(ReportFilter f, bool customer)
    {
        var start = DateTime.ParseExact(f.From, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var end = DateTime.ParseExact(f.To, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (start.Day != 1 || end != start.AddMonths(1).AddDays(-1))
            throw new AppException("月报必须选择完整的一个自然月。");
        var kind = customer ? "receivable" : "payable";
        var direction = customer ? "out" : "in";
        var title = customer ? "客户应收汇总统计月报表" : "供应商应付汇总统计月报表";
        var result = new ReportResult
        {
            Id = customer ? "customer_monthly_receivable" : "supplier_monthly_payable",
            Title = title, Period = start.ToString("yyyy-MM"),
            FileName = (customer ? "客户应收汇总统计_" : "供应商应付汇总统计_") + start.ToString("yyyy_MM"),
            Basis = "按已审核凭证统计；期末余额包含期初、历史收付款、预收预付、退款及生效的余额调整。已开票为截至月末实际关联净额（含可开票现收/现付业务的关联）。",
            Note = "未开票 = 可开票业务（应收/应付单 + 可开票现收/现付）− 已关联有效发票；期初余额缺少开票明细，不计入未开票。作废或未审核凭证不计入。",
            Columns = new()
            {
                new() { Header = "年度", Width = 75 }, new() { Header = "月", Width = 55 },
                new() { Header = customer ? "客户名称" : "供应商名称", Width = 190 },
                new() { Header = customer ? "本月应收" : "本月应付", Width = 120, Numeric = true, Format = "money" },
                new() { Header = customer ? "本月实收" : "本月实付", Width = 120, Numeric = true, Format = "money" },
                new() { Header = customer ? "期末未收余额" : "期末未付余额", Width = 145, Numeric = true, Format = "money" },
                new() { Header = "已开票金额", Width = 125, Numeric = true, Format = "money" },
                new() { Header = "未开票金额", Width = 125, Numeric = true, Format = "money" },
                new() { Header = "本月余额调整", Width = 125, Numeric = true, Format = "money" },
            }
        };
        using var conn = Conn();
        using var tx = conn.BeginTransaction(deferred: true);
        var people = new SqlCmd(conn, $@"
            SELECT p.id,p.name,p.{(customer ? "opening_receivable_cents" : "opening_payable_cents")} opening,
              COALESCE(SUM(CASE WHEN v.kind=$kind AND v.biz_date >= $f THEN v.amount_cents ELSE 0 END),0) month_debt,
              COALESCE(SUM(CASE WHEN v.kind=$cashkind AND v.nature IN ($payment,$prepay) AND v.biz_date >= $f THEN v.amount_cents ELSE 0 END),0) month_cash,
              COALESCE(SUM(CASE WHEN v.kind=$kind THEN v.amount_cents
                   WHEN v.kind=$cashkind AND v.nature IN ($payment,$prepay) THEN -v.amount_cents
                   WHEN v.kind=$refundkind AND v.nature=$refund THEN v.amount_cents ELSE 0 END),0) change_net,
              COALESCE(SUM(CASE WHEN v.kind=$kind THEN v.amount_cents
                   WHEN v.kind=$cashkind AND v.nature=$salecash AND v.invoiceable=1 THEN v.amount_cents ELSE 0 END),0) invoiceable_total
            FROM partners p LEFT JOIN vouchers v ON v.partner_id=p.id AND v.status='audited' AND v.biz_date <= $t
            WHERE p.kind IN ($partnerkind,'both') AND ($pid IS NULL OR p.id=$pid)
            GROUP BY p.id ORDER BY p.name,p.id", tx)
            .P("$kind",kind).P("$cashkind",customer ? "income" : "expense")
            .P("$salecash",customer ? "SaleCash" : "PurchaseCash")
            .P("$payment",customer ? "CustomerReceipt" : "SupplierPayment")
            .P("$prepay",customer ? "CustomerPrepay" : "SupplierPrepay")
            .P("$refundkind",customer ? "expense" : "income")
            .P("$refund",customer ? "CustomerRefund" : "SupplierRefund")
            .P("$partnerkind",customer ? "customer" : "supplier").P("$pid",f.PartnerId)
            .P("$f",f.From).P("$t",f.To)
            .List(r => (id:R.L(r,"id"), name:R.S(r,"name"), opening:R.L(r,"opening"),
                month:R.L(r,"month_debt"), cash:R.L(r,"month_cash"), net:R.L(r,"change_net"), invoiceable:R.L(r,"invoiceable_total")));
        var linksByPartner = new SqlCmd(conn, @"
                WITH movements AS (
                    SELECT il.voucher_id, il.amount_cents * il.sign AS amount
                    FROM invoice_links il JOIN invoices i ON i.id=il.invoice_id
                    WHERE i.status='valid' AND i.kind=$direction AND il.effective_date <= $t AND i.reg_date <= $t
                    UNION ALL
                    SELECT h.voucher_id, h.amount_cents * h.sign AS amount
                    FROM invoice_link_history h JOIN invoices i ON i.id=h.invoice_id
                    WHERE i.kind=$direction AND h.effective_date <= $t AND h.ended_date > $t
                      AND (i.status='valid' OR substr(i.voided_at,1,10)>$t)
                )
                SELECT v.id,v.partner_id,v.amount_cents,COALESCE(SUM(m.amount),0) invoiced
                FROM vouchers v LEFT JOIN movements m ON m.voucher_id=v.id
                WHERE ($pid IS NULL OR v.partner_id=$pid) AND v.status='audited' AND v.biz_date <= $t
                  AND (v.kind=$kind OR (v.kind=$cashkind AND v.nature=$salecash AND v.invoiceable=1))
                GROUP BY v.id", tx).P("$direction",direction).P("$t",f.To).P("$pid",f.PartnerId).P("$kind",kind)
                .P("$cashkind",customer ? "income" : "expense").P("$salecash",customer ? "SaleCash" : "PurchaseCash")
                .List(r => (partner:R.L(r,"partner_id"), amount:R.L(r,"amount_cents"), invoiced:R.L(r,"invoiced")))
                .ToLookup(x => x.partner);
        long monthTotal=0, cashTotal=0, balanceTotal=0, invoiceTotal=0, uninvoiceTotal=0,adjustmentTotal=0;
        foreach (var p in people)
        {
            var links = linksByPartner[p.id];
            if (links.Any(x => x.invoiced < 0 || x.invoiced > x.amount))
                throw new AppException($"{p.name} 的发票关联金额超出业务金额或出现负数，请检查关联后重试。");
            var inv = links.Sum(x => x.invoiced);
            var uninvoiced = Math.Max(0, p.invoiceable - inv);
            var adjustment=OpeningBalanceService.Sum(conn,customer?"receivable":"payable",p.id,f.To,f.From,tx);
            var balance = checked(p.opening + p.net+OpeningBalanceService.Sum(conn,customer?"receivable":"payable",p.id,f.To,tx:tx));
            adjustmentTotal=checked(adjustmentTotal+adjustment);
            monthTotal=checked(monthTotal+p.month); cashTotal=checked(cashTotal+p.cash);
            balanceTotal=checked(balanceTotal+balance); invoiceTotal=checked(invoiceTotal+inv); uninvoiceTotal=checked(uninvoiceTotal+uninvoiced);
            result.Rows.Add(new ReportRow { Cells = new object?[] { start.Year.ToString(), start.Month.ToString(), p.name, p.month, p.cash, balance, inv, uninvoiced,adjustment }, DrillId = p.id, DrillKind = "partner" });
        }
        result.TotalRow = new object?[] { "合计", "", "", monthTotal, cashTotal, balanceTotal, invoiceTotal, uninvoiceTotal,adjustmentTotal };
        tx.Commit();
        return result;
    }
}
