namespace Cashier.Core.Services;

public partial class ReportService
{
    private ReportResult PayableDetail(ReportFilter f)
    {
        var r = new ReportResult
        {
            Id = "payable_detail", Title = "应付明细", Period = P(f),
            Basis = "按应付单逐笔列示：应付金额 − 已核销 = 未付金额。",
            Columns = new()
            {
                new() { Header = "日期", Width = 92 },
                new() { Header = "单据编号", Width = 122 },
                new() { Header = "供应商", Width = 160 },
                new() { Header = "项目", Width = 130 },
                new() { Header = "摘要", Width = 240 },
                new() { Header = "应付金额", Width = 120, Numeric = true, Format = "money" },
                new() { Header = "已核销", Width = 120, Numeric = true, Format = "money" },
                new() { Header = "未付金额", Width = 120, Numeric = true, Format = "money" },
            },
        };
        using var conn = Conn();
        using var cmd = new SqlCmd(conn, $@"
            SELECT v.*, COALESCE(p.name,'') pn, COALESCE(pr.name,'') prn,
              COALESCE((SELECT SUM(s.amount_cents) FROM settlements s
                JOIN vouchers pv ON pv.id=s.pay_voucher_id
                WHERE s.biz_voucher_id=v.id AND pv.biz_date <= $t AND s.effective_date <= $t),0)
                + COALESCE((SELECT SUM(h.amount_cents) FROM settlement_history h
                    WHERE h.biz_voucher_id=v.id AND h.effective_date <= $t AND h.ended_date > $t),0) settled
            FROM vouchers v
            LEFT JOIN partners p ON p.id=v.partner_id
            LEFT JOIN projects pr ON pr.id=v.project_id
            WHERE v.status='audited' AND v.kind='payable' AND v.biz_date BETWEEN $f AND $t
              {PartnerJoin(f.PartnerId)} {ProjectJoin(f.ProjectId)}
            ORDER BY v.biz_date, v.doc_no")
            .P("$f", f.From).P("$t", f.To);
        Bind(cmd, f, account: false);
        long a = 0, b = 0;
        foreach (var x in cmd.List(MapRow))
        {
            var unsettled = x.amount - x.settled;
            a += x.amount; b += unsettled;
            r.Rows.Add(new ReportRow
            {
                Cells = new object?[] { x.date, x.docNo, x.partner, x.project, x.summary, x.amount, x.settled, unsettled }
            });
        }
        r.TotalRow = new object?[] { "合计", "", "", "", $"{r.Rows.Count} 笔", a, a - b, b };
        return r;
    }

    private ReportResult ReceivableDetail(ReportFilter f)
    {
        var r = new ReportResult
        {
            Id = "receivable_detail", Title = "应收明细", Period = P(f),
            Basis = "按应收单逐笔列示：应收金额 − 已核销 = 未收金额；已开票金额来自有效发票关联。",
            Columns = new()
            {
                new() { Header = "日期", Width = 92 },
                new() { Header = "单据编号", Width = 122 },
                new() { Header = "客户", Width = 160 },
                new() { Header = "项目", Width = 130 },
                new() { Header = "摘要", Width = 220 },
                new() { Header = "应收金额", Width = 118, Numeric = true, Format = "money" },
                new() { Header = "已核销", Width = 112, Numeric = true, Format = "money" },
                new() { Header = "未收金额", Width = 118, Numeric = true, Format = "money" },
                new() { Header = "已开票", Width = 112, Numeric = true, Format = "money" },
            },
        };
        using var conn = Conn();
        using var cmd = new SqlCmd(conn, $@"
            SELECT v.*, COALESCE(p.name,'') pn, COALESCE(pr.name,'') prn,
              COALESCE((SELECT SUM(s.amount_cents) FROM settlements s
                JOIN vouchers pv ON pv.id=s.pay_voucher_id
                WHERE s.biz_voucher_id=v.id AND pv.biz_date <= $t AND s.effective_date <= $t),0)
                + COALESCE((SELECT SUM(h.amount_cents) FROM settlement_history h
                    WHERE h.biz_voucher_id=v.id AND h.effective_date <= $t AND h.ended_date > $t),0) settled,
              COALESCE((SELECT SUM(il.amount_cents * il.sign) FROM invoice_links il
                        JOIN invoices i ON i.id=il.invoice_id AND i.status='valid'
                        WHERE il.voucher_id=v.id AND i.reg_date <= $t AND il.effective_date <= $t),0)
                + COALESCE((SELECT SUM(h.amount_cents * h.sign) FROM invoice_link_history h
                    JOIN invoices hi ON hi.id=h.invoice_id
                    WHERE h.voucher_id=v.id AND h.effective_date <= $t AND h.ended_date > $t
                      AND (hi.status='valid' OR substr(hi.voided_at,1,10)>$t)),0) inv
            FROM vouchers v
            LEFT JOIN partners p ON p.id=v.partner_id
            LEFT JOIN projects pr ON pr.id=v.project_id
            WHERE v.status='audited' AND v.kind='receivable' AND v.biz_date BETWEEN $f AND $t
              {PartnerJoin(f.PartnerId)} {ProjectJoin(f.ProjectId)}
            ORDER BY v.biz_date, v.doc_no")
            .P("$f", f.From).P("$t", f.To);
        Bind(cmd, f, account: false);
        long a = 0, b = 0, c = 0;
        foreach (var x in cmd.List(MapRow))
        {
            var unsettled = x.amount - x.settled;
            a += x.amount; b += unsettled; c += x.inv;
            r.Rows.Add(new ReportRow
            {
                Cells = new object?[] { x.date, x.docNo, x.partner, x.project, x.summary, x.amount, x.settled, unsettled, x.inv }
            });
        }
        r.TotalRow = new object?[] { "合计", "", "", "", $"{r.Rows.Count} 笔", a, a - b, b, c };
        return r;
    }

    private ReportResult SupplierSummary(ReportFilter f)
    {
        var r = new ReportResult
        {
            Id = "supplier_summary", Title = "供应商应付汇总", Period = P(f),
            Basis = "净应付 = 期初 + 应付单 − 付款 − 预付 + 供应商退回 + 余额调整；负数为预付余额。",
            Columns = new()
            {
                new() { Header = "供应商", Width = 200 },
                new() { Header = "期初应付", Width = 120, Numeric = true, Format = "money" },
                new() { Header = "本期应付", Width = 120, Numeric = true, Format = "money" },
                new() { Header = "本期付款", Width = 120, Numeric = true, Format = "money" },
                new() { Header = "本期预付", Width = 120, Numeric = true, Format = "money" },
                new() { Header = "净应付", Width = 130, Numeric = true, Format = "money" },
                new() { Header = "本期余额调整", Width = 130, Numeric = true, Format = "money" },
            },
            DrillReportId = "payable_detail", DrillFilterKind = "partner",
        };
        using var conn = Conn();
        using var cmd = new SqlCmd(conn, @"
            SELECT p.id, p.name, p.opening_payable_cents + COALESCE(SUM(CASE WHEN v.biz_date < $f THEN
                 CASE WHEN v.kind='payable' OR (v.kind='income' AND v.nature='SupplierRefund') THEN v.amount_cents
                      WHEN v.kind='expense' AND v.nature IN ('SupplierPayment','SupplierPrepay') THEN -v.amount_cents ELSE 0 END
                 ELSE 0 END),0) op,
              COALESCE(SUM(CASE WHEN v.biz_date >= $f AND v.kind='payable' THEN v.amount_cents ELSE 0 END),0) pay,
              COALESCE(SUM(CASE WHEN v.biz_date >= $f AND v.kind='expense' AND v.nature='SupplierPayment' THEN v.amount_cents ELSE 0 END),0) paid,
              COALESCE(SUM(CASE WHEN v.biz_date >= $f AND v.kind='expense' AND v.nature='SupplierPrepay' THEN v.amount_cents ELSE 0 END),0) prepay,
              COALESCE(SUM(CASE WHEN v.biz_date >= $f AND v.kind='income' AND v.nature='SupplierRefund' THEN v.amount_cents ELSE 0 END),0) refund
            FROM partners p
            LEFT JOIN vouchers v ON v.partner_id=p.id AND v.status='audited' AND v.biz_date <= $t
            WHERE p.kind IN ('supplier','both') AND ($pid IS NULL OR p.id=$pid)
            GROUP BY p.id ORDER BY p.name")
            .P("$f", f.From).P("$t", f.To).P("$pid", f.PartnerId);
        long tp = 0, tn = 0, payable = 0, prepaid = 0,ta=0;
        foreach (var x in cmd.List(r2 => (id: R.L(r2, "id"), name: R.S(r2, "name"), op: R.L(r2, "op"),
                     pay: R.L(r2, "pay"), paid: R.L(r2, "paid"), prepay: R.L(r2, "prepay"), refund: R.L(r2, "refund"))))
        {
            var opening=checked(x.op+OpeningBalanceService.Sum(conn,"payable",x.id,DateTime.Parse(f.From).AddDays(-1).ToString("yyyy-MM-dd")));
            var adjustment=OpeningBalanceService.Sum(conn,"payable",x.id,f.To,f.From);
            var net = checked(opening + x.pay - x.paid - x.prepay + x.refund+adjustment);
            ta=checked(ta+adjustment);
            if (net >= 0) payable += net; else prepaid -= net;
            tp += x.pay; tn += net;
            r.Rows.Add(new ReportRow
            {
                Cells = new object?[] { x.name, opening, x.pay, x.paid, x.prepay, net,adjustment },
                DrillId = x.id, DrillKind = "partner",
            });
        }
        r.TotalRow = new object?[] { "合计", null, tp, null, null, tn,ta };
        r.Note = $"本期退款计入净应付。应付余额合计 {Money.Format(payable)}，预付余额合计 {Money.Format(prepaid)}；表格净额合计为二者之差。";
        return r;
    }

    private ReportResult CustomerSummary(ReportFilter f)
    {
        var r = new ReportResult
        {
            Id = "customer_summary", Title = "客户应收汇总", Period = P(f),
            Basis = "净应收 = 期初 + 应收单 − 回款 − 预收 + 退款 + 余额调整；调整不计入业务发生额与开票。",
            Columns = new()
            {
                new() { Header = "客户", Width = 190 },
                new() { Header = "期初应收", Width = 118, Numeric = true, Format = "money" },
                new() { Header = "本期应收", Width = 118, Numeric = true, Format = "money" },
                new() { Header = "本期回款", Width = 118, Numeric = true, Format = "money" },
                new() { Header = "本期预收", Width = 118, Numeric = true, Format = "money" },
                new() { Header = "净应收", Width = 128, Numeric = true, Format = "money" },
                new() { Header = "已开票", Width = 118, Numeric = true, Format = "money" },
                new() { Header = "未开票", Width = 118, Numeric = true, Format = "money" },
                new() { Header = "本期余额调整", Width = 128, Numeric = true, Format = "money" },
            },
            DrillReportId = "receivable_detail", DrillFilterKind = "partner",
        };
        using var conn = Conn();
        using var cmd = new SqlCmd(conn, @"
            SELECT p.id, p.name, p.opening_receivable_cents + COALESCE(SUM(CASE WHEN v.biz_date < $f THEN
                 CASE WHEN v.kind='receivable' OR (v.kind='expense' AND v.nature='CustomerRefund') THEN v.amount_cents
                      WHEN v.kind='income' AND v.nature IN ('CustomerReceipt','CustomerPrepay') THEN -v.amount_cents ELSE 0 END
                 ELSE 0 END),0) op,
              COALESCE(SUM(CASE WHEN v.biz_date >= $f AND v.kind='receivable' THEN v.amount_cents ELSE 0 END),0) recv,
              COALESCE(SUM(CASE WHEN v.biz_date >= $f AND v.kind='income' AND v.nature='CustomerReceipt' THEN v.amount_cents ELSE 0 END),0) got,
              COALESCE(SUM(CASE WHEN v.biz_date >= $f AND v.kind='income' AND v.nature='CustomerPrepay' THEN v.amount_cents ELSE 0 END),0) prepay,
              COALESCE(SUM(CASE WHEN v.biz_date >= $f AND v.kind='expense' AND v.nature='CustomerRefund' THEN v.amount_cents ELSE 0 END),0) refund
            FROM partners p
            LEFT JOIN vouchers v ON v.partner_id=p.id AND v.status='audited' AND v.biz_date <= $t
            WHERE p.kind IN ('customer','both') AND ($pid IS NULL OR p.id=$pid)
            GROUP BY p.id ORDER BY p.name")
            .P("$f", f.From).P("$t", f.To).P("$pid", f.PartnerId);
        var svc = new InvoiceService();
        long tr = 0, tn = 0, ti = 0, tu = 0, receivable = 0, advance = 0,ta=0;
        foreach (var x in cmd.List(r2 => (id: R.L(r2, "id"), name: R.S(r2, "name"), op: R.L(r2, "op"),
                     recv: R.L(r2, "recv"), got: R.L(r2, "got"), prepay: R.L(r2, "prepay"), refund: R.L(r2, "refund"))))
        {
            var opening=checked(x.op+OpeningBalanceService.Sum(conn,"receivable",x.id,DateTime.Parse(f.From).AddDays(-1).ToString("yyyy-MM-dd")));
            var adjustment=OpeningBalanceService.Sum(conn,"receivable",x.id,f.To,f.From);
            var net = checked(opening + x.recv - x.got - x.prepay + x.refund+adjustment);
            ta=checked(ta+adjustment);
            var (inv, inved, uninved) = svc.PartnerInvoiceSummary(x.id, InvoiceKind.Out, f.To);
            if (net >= 0) receivable += net; else advance -= net;
            tr += x.recv; tn += net; ti += inved; tu += uninved;
            r.Rows.Add(new ReportRow
            {
                Cells = new object?[] { x.name, opening, x.recv, x.got, x.prepay, net, inved, uninved,adjustment },
                DrillId = x.id, DrillKind = "partner",
            });
        }
        r.TotalRow = new object?[] { "合计", null, tr, null, null, tn, ti, tu,ta };
        r.Note = $"本期退款计入净应收。应收余额合计 {Money.Format(receivable)}，预收余额合计 {Money.Format(advance)}；表格净额合计为二者之差。开票金额为截至结束日的累计数。";
        return r;
    }
}
