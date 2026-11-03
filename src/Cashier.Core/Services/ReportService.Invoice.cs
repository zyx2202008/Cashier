namespace Cashier.Core.Services;

public partial class ReportService
{
    private ReportResult PeriodInvoiceCash(ReportFilter f)
    {
        var isOut = f.InvoiceDirection == InvoiceKind.Out;
        var partnerKinds = isOut ? "('customer','both')" : "('supplier','both')";
        var cashLabel = isOut ? "期间收款金额" : "期间付款金额";
        var diffLabel = isOut ? "票款差额（含税开票-收款）" : "票款差额（含税收票-付款）";
        var r = new ReportResult
        {
            Id = "period_invoice_cash", Title = "期间开票与收款统计",
            Period = P(f) + " · " + (isOut ? "销项" : "进项"),
            Basis = isOut
                ? "有效销项发票按登记日期、已审核收入单据按业务日期归入期间，按客户汇总；票款差额 = 含税开票 − 收款。"
                : "有效进项发票按登记日期、已审核支出单据按业务日期归入期间，按供应商汇总；票款差额 = 含税收票 − 付款。",
            FileName = $"期间开票与收款统计_{(isOut ? "销项" : "进项")}_{f.From}_{f.To}",
            Columns = new()
            {
                new() { Header = "单位编码", Width = 92 },
                new() { Header = "单位名称", Width = 190 },
                new() { Header = "蓝票张数", Width = 78, Numeric = true, Format = "int" },
                new() { Header = "红票张数", Width = 78, Numeric = true, Format = "int" },
                new() { Header = "净张数", Width = 72, Numeric = true, Format = "int" },
                new() { Header = "含税金额合计", Width = 122, Numeric = true, Format = "money" },
                new() { Header = "不含税金额合计", Width = 122, Numeric = true, Format = "money" },
                new() { Header = "税额合计", Width = 110, Numeric = true, Format = "money" },
                new() { Header = cashLabel, Width = 120, Numeric = true, Format = "money" },
                new() { Header = diffLabel, Width = 176, Numeric = true, Format = "money" },
            },
        };
        using var conn = Conn();
        using var cmd = new SqlCmd(conn, $@"
            WITH inv AS (
              SELECT i.partner_id pid,
                SUM(CASE WHEN i.sign=1 THEN 1 ELSE 0 END) blue_cnt,
                SUM(CASE WHEN i.sign=-1 THEN 1 ELSE 0 END) red_cnt,
                COALESCE(SUM(i.gross_cents * i.sign),0) gross,
                COALESCE(SUM(i.net_cents * i.sign),0) net,
                COALESCE(SUM(i.tax_cents * i.sign),0) tax
              FROM invoices i
              WHERE i.kind=$k AND i.status='valid' AND i.reg_date BETWEEN $f AND $t
                AND ($pid IS NULL OR i.partner_id=$pid)
              GROUP BY i.partner_id
            ), cash AS (
              SELECT v.partner_id pid, COALESCE(SUM(v.amount_cents),0) amt
              FROM vouchers v
              WHERE v.status='audited' AND v.kind=$ck AND v.biz_date BETWEEN $f AND $t
                AND ($pid IS NULL OR v.partner_id=$pid)
              GROUP BY v.partner_id
            )
            SELECT p.id, p.code, p.name,
              COALESCE(inv.blue_cnt,0) blue_cnt, COALESCE(inv.red_cnt,0) red_cnt,
              COALESCE(inv.gross,0) gross, COALESCE(inv.net,0) net, COALESCE(inv.tax,0) tax,
              COALESCE(cash.amt,0) cash
            FROM partners p
            LEFT JOIN inv ON inv.pid=p.id
            LEFT JOIN cash ON cash.pid=p.id
            WHERE p.kind IN {partnerKinds}
              AND ($pid IS NULL OR p.id=$pid)
              AND (inv.pid IS NOT NULL OR cash.pid IS NOT NULL)
            ORDER BY p.code, p.name")
            .P("$k", isOut ? "out" : "in").P("$ck", isOut ? "income" : "expense")
            .P("$f", f.From).P("$t", f.To).P("$pid", f.PartnerId);
        long tb = 0, tr = 0, tg = 0, tn = 0, ttax = 0, tcash = 0;
        foreach (var x in cmd.List(r2 => (id: R.L(r2, "id"), code: R.S(r2, "code"), name: R.S(r2, "name"),
                     blue: R.L(r2, "blue_cnt"), red: R.L(r2, "red_cnt"), gross: R.L(r2, "gross"),
                     net: R.L(r2, "net"), tax: R.L(r2, "tax"), cash: R.L(r2, "cash"))))
        {
            tb += x.blue; tr += x.red; tg += x.gross; tn += x.net; ttax += x.tax; tcash += x.cash;
            r.Rows.Add(new ReportRow
            {
                Cells = new object?[] { x.code, x.name, x.blue, x.red, x.blue - x.red, x.gross, x.net, x.tax, x.cash, x.gross - x.cash },
                DrillId = x.id, DrillKind = "partner",
            });
        }
        r.TotalRow = new object?[] { "合计", "", tb, tr, tb - tr, tg, tn, ttax, tcash, tg - tcash };
        r.Note = "红字发票按负数冲减，净张数 = 蓝票张数 − 红票张数；票款差额仅为本期间含税开票与收付款的差额，不受核销、关联状态影响，不代表期末余额。只读报表不做关账校验，已关账期间按现有数据呈现。";
        return r;
    }

    public PeriodInvoiceCashDetail GetPeriodInvoiceCashDetail(long partnerId, string from, string to, InvoiceKind direction)
    {
        SqlUtil.ValidateDate(from, "报表开始日期");
        SqlUtil.ValidateDate(to, "报表结束日期");
        if (string.CompareOrdinal(from, to) > 0) throw new AppException("报表开始日期不能晚于结束日期。");
        if (partnerId <= 0) throw new AppException("请选择往来单位。");
        var isOut = direction == InvoiceKind.Out;
        using var conn = Conn();
        var partner = new SqlCmd(conn, "SELECT code,name FROM partners WHERE id=$i").P("$i", partnerId)
            .First(x => (code: R.S(x, "code"), name: R.S(x, "name")));
        if (string.IsNullOrEmpty(partner.name)) throw new AppException("往来单位不存在。");
        var invoices = new InvoiceService().List(direction, null, partnerId, from, to, false)
            .OrderBy(i => i.RegDate, StringComparer.Ordinal)
            .ThenBy(i => i.InvoiceNo, StringComparer.Ordinal)
            .ToList();
        var vouchers = new SqlCmd(conn, @"
            SELECT id,doc_no,biz_date,nature,amount_cents,summary FROM vouchers
            WHERE status='audited' AND kind=$k AND partner_id=$p AND biz_date BETWEEN $f AND $t
            ORDER BY biz_date, doc_no")
            .P("$k", isOut ? "income" : "expense").P("$p", partnerId).P("$f", from).P("$t", to)
            .List(r => new Voucher
            {
                Id = R.L(r, "id"), DocNo = R.S(r, "doc_no"), BizDate = R.S(r, "biz_date"),
                Nature = NatureInfo.Parse(R.S(r, "nature")), AmountCents = R.L(r, "amount_cents"),
                Summary = R.S(r, "summary"), Status = VoucherStatus.Audited,
            });
        return new PeriodInvoiceCashDetail
        {
            Direction = direction, PartnerCode = partner.code, PartnerName = partner.name,
            From = from, To = to, Invoices = invoices, Vouchers = vouchers,
        };
    }

    public List<ReportResult> BuildPeriodInvoiceCashDetailReports(PeriodInvoiceCashDetail d)
    {
        var isOut = d.IsOut;
        var inv = new ReportResult
        {
            Id = "period_invoice_cash_invoices",
            Title = isOut ? "销项发票明细" : "进项发票明细",
            Period = $"{d.PartnerName} · {d.From} 至 {d.To}",
            Basis = "仅列示期间有效发票；红字发票金额以负数参与合计，与统计表口径一致。",
            Columns = new()
            {
                new() { Header = "登记日期", Width = 96 },
                new() { Header = "发票号码", Width = 190 },
                new() { Header = "发票类型", Width = 150 },
                new() { Header = "含税金额", Width = 130, Numeric = true, Format = "money" },
                new() { Header = "不含税金额", Width = 130, Numeric = true, Format = "money" },
                new() { Header = "税额", Width = 110, Numeric = true, Format = "money" },
            },
        };
        foreach (var x in d.Invoices)
            inv.Rows.Add(new ReportRow
            {
                Cells = new object?[] { x.RegDate, x.InvoiceNo, x.InvoiceType, x.GrossCents, x.NetCents, x.TaxCents }
            });
        inv.TotalRow = new object?[] { "合计", $"{d.Invoices.Count} 张（蓝 {d.BlueCount} / 红 {d.RedCount}）", "", d.GrossCents, d.NetCents, d.TaxCents };

        var cash = new ReportResult
        {
            Id = "period_invoice_cash_cash",
            Title = isOut ? "收款明细" : "付款明细",
            Period = $"{d.PartnerName} · {d.From} 至 {d.To}",
            Basis = isOut
                ? "按单据业务日期列示期间已审核的收入单据（资金口径），不含作废、待审核单据。"
                : "按单据业务日期列示期间已审核的支出单据（资金口径），不含作废、待审核单据。",
            Columns = new()
            {
                new() { Header = "业务日期", Width = 96 },
                new() { Header = "单据编号", Width = 130 },
                new() { Header = "业务性质", Width = 110 },
                new() { Header = "金额", Width = 130, Numeric = true, Format = "money" },
                new() { Header = "摘要", Width = 280 },
            },
        };
        foreach (var x in d.Vouchers)
            cash.Rows.Add(new ReportRow { Cells = new object?[] { x.BizDate, x.DocNo, x.NatureText, x.AmountCents, x.Summary } });
        cash.TotalRow = new object?[] { "合计", $"{d.Vouchers.Count} 笔", "", d.CashCents, "" };
        return new List<ReportResult> { inv, cash };
    }

    private ReportResult InvoiceDetail(ReportFilter f, InvoiceKind kind)
    {
        var isOut = kind == InvoiceKind.Out;
        var r = new ReportResult
        {
            Id = isOut ? "invoice_out_detail" : "invoice_in_detail",
            Title = isOut ? "销项发票明细" : "进项发票明细",
            Period = P(f),
            Basis = "发票登记不改变资金余额；金额为票面价税合计，税额按票面记录。",
            Columns = new()
            {
                new() { Header = "登记日期", Width = 96 },
                new() { Header = "发票号码", Width = 190 },
                new() { Header = "发票代码", Width = 150 },
                new() { Header = isOut ? "客户" : "供应商", Width = 170 },
                new() { Header = "发票类型", Width = 140 },
                new() { Header = "税率", Width = 64, Numeric = true },
                new() { Header = "含税金额", Width = 130, Numeric = true, Format = "money" },
                new() { Header = "不含税金额", Width = 130, Numeric = true, Format = "money" },
                new() { Header = "税额", Width = 110, Numeric = true, Format = "money" },
                new() { Header = "已关联业务", Width = 120, Numeric = true, Format = "money" },
                new() { Header = "状态", Width = 76 },
            },
        };
        using var conn = Conn();
        using var cmd = new SqlCmd(conn, @"
            SELECT i.*, COALESCE(p.name,'') AS partner_name,
              COALESCE((SELECT SUM(il.amount_cents * il.sign) FROM invoice_links il
                        WHERE il.invoice_id=i.id AND il.effective_date <= $t),0)
                + COALESCE((SELECT SUM(h.amount_cents * h.sign) FROM invoice_link_history h
                        WHERE h.invoice_id=i.id AND h.effective_date <= $t AND h.ended_date > $t),0) AS linked_cents
            FROM invoices i LEFT JOIN partners p ON p.id=i.partner_id
            WHERE i.kind=$k AND i.reg_date BETWEEN $f AND $t" + (f.PartnerId.HasValue ? " AND i.partner_id=$pid" : "") + @"
            ORDER BY i.reg_date, i.invoice_no")
            .P("$k", isOut ? "out" : "in").P("$f", f.From).P("$t", f.To);
        if (f.PartnerId.HasValue) cmd.P("$pid", f.PartnerId);
        long g = 0, n = 0, t = 0;
        foreach (var x in cmd.List(InvoiceService.Map))
        {
            if (x.Status == InvoiceStatus.Valid) { g += x.GrossCents; n += x.NetCents; t += x.TaxCents; }
            r.Rows.Add(new ReportRow
            {
                Cells = new object?[]
                {
                    x.RegDate, x.InvoiceNo, x.InvoiceCode ?? "", x.PartnerName, x.InvoiceType,
                    x.RateBp / 100m, x.GrossCents, x.NetCents, x.TaxCents, x.LinkedCents, x.StatusLabel,
                }
            });
        }
        r.TotalRow = new object?[] { "合计", "", "", "", "", null, g, n, t, null, $"{r.Rows.Count} 张" };
        r.Note = "已关联业务按截止日统计；发票状态为当前状态，已作废发票不计入有效发票金额。";
        return r;
    }
}
