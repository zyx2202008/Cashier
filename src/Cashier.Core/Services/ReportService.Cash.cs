namespace Cashier.Core.Services;

public partial class ReportService
{
    private ReportResult IncomeDetail(ReportFilter f)
    {
        var r = new ReportResult
        {
            Id = "income_detail", Title = "收入明细", Period = P(f),
            Basis = "业务发生口径：现收收入 + 计入收入的应收单 + 其他收入；客户回款/预收不计收入。",
            Columns = new()
            {
                new() { Header = "日期", Width = 92 },
                new() { Header = "单据编号", Width = 122 },
                new() { Header = "来源", Width = 84 },
                new() { Header = "业务性质", Width = 92 },
                new() { Header = "往来单位", Width = 150 },
                new() { Header = "项目", Width = 130 },
                new() { Header = "科目", Width = 110 },
                new() { Header = "摘要", Width = 240 },
                new() { Header = "金额", Width = 120, Numeric = true, Format = "money" },
            },
        };
        using var conn = Conn();
        using var cmd = new SqlCmd(conn, $@"
            SELECT v.*, COALESCE(p.name,'') pn, COALESCE(pr.name,'') prn, COALESCE(s.name,'') sn
            FROM vouchers v
            LEFT JOIN partners p ON p.id=v.partner_id
            LEFT JOIN projects pr ON pr.id=v.project_id
            LEFT JOIN subjects s ON s.id=v.subject_id
            WHERE v.status='audited' AND v.biz_date BETWEEN $f AND $t AND {RevenueCond}
              {PartnerJoin(f.PartnerId)} {ProjectJoin(f.ProjectId)}
            ORDER BY v.biz_date, v.doc_no")
            .P("$f", f.From).P("$t", f.To);
        Bind(cmd, f, account: false);
        long total = 0;
        foreach (var x in cmd.List(MapRow))
        {
            total += x.amount;
            r.Rows.Add(new ReportRow
            {
                Cells = new object?[]
                {
                    x.date, x.docNo, x.kind == "receivable" ? "应收单" : "收入单",
                    x.nature, x.partner, x.project, x.subject, x.summary, x.amount,
                }
            });
        }
        r.TotalRow = new object?[] { "合计", "", "", "", "", "", "", $"{r.Rows.Count} 笔", total };
        r.Note = "应收单按“计入收入”标记统计；已作废、待审核单据不参与。";
        return r;
    }

    private ReportResult ExpenseDetail(ReportFilter f)
    {
        var r = new ReportResult
        {
            Id = "expense_detail", Title = "支出明细", Period = P(f),
            Basis = "业务发生口径：现付费用 + 计入成本的应付单 + 其他支出；供应商付款/预付不计成本。",
            Columns = new()
            {
                new() { Header = "日期", Width = 92 },
                new() { Header = "单据编号", Width = 122 },
                new() { Header = "来源", Width = 84 },
                new() { Header = "业务性质", Width = 92 },
                new() { Header = "往来单位", Width = 150 },
                new() { Header = "项目", Width = 130 },
                new() { Header = "科目", Width = 110 },
                new() { Header = "摘要", Width = 240 },
                new() { Header = "金额", Width = 120, Numeric = true, Format = "money" },
            },
        };
        using var conn = Conn();
        using var cmd = new SqlCmd(conn, $@"
            SELECT v.*, COALESCE(p.name,'') pn, COALESCE(pr.name,'') prn, COALESCE(s.name,'') sn
            FROM vouchers v
            LEFT JOIN partners p ON p.id=v.partner_id
            LEFT JOIN projects pr ON pr.id=v.project_id
            LEFT JOIN subjects s ON s.id=v.subject_id
            WHERE v.status='audited' AND v.biz_date BETWEEN $f AND $t AND {CostCond}
              {PartnerJoin(f.PartnerId)} {ProjectJoin(f.ProjectId)}
            ORDER BY v.biz_date, v.doc_no")
            .P("$f", f.From).P("$t", f.To);
        Bind(cmd, f, account: false);
        long total = 0;
        foreach (var x in cmd.List(MapRow))
        {
            total += x.amount;
            r.Rows.Add(new ReportRow
            {
                Cells = new object?[]
                {
                    x.date, x.docNo, x.kind == "payable" ? "应付单" : "支出单",
                    x.nature, x.partner, x.project, x.subject, x.summary, x.amount,
                }
            });
        }
        r.TotalRow = new object?[] { "合计", "", "", "", "", "", "", $"{r.Rows.Count} 笔", total };
        r.Note = "应付单按“计入成本”标记统计；已作废、待审核单据不参与。";
        return r;
    }

    private ReportResult CashDetail(ReportFilter f)
    {
        var r = new ReportResult
        {
            Id = "cash_detail", Title = "收支明细", Period = P(f),
            Basis = "资金口径（收付实现制）：所有收入、支出单据的实际资金收付，不含内部转账。",
            Columns = new()
            {
                new() { Header = "日期", Width = 92 },
                new() { Header = "单据编号", Width = 122 },
                new() { Header = "类型", Width = 64 },
                new() { Header = "业务性质", Width = 92 },
                new() { Header = "账户", Width = 130 },
                new() { Header = "往来单位", Width = 140 },
                new() { Header = "摘要", Width = 240 },
                new() { Header = "收入", Width = 116, Numeric = true, Format = "money" },
                new() { Header = "支出", Width = 116, Numeric = true, Format = "money" },
            },
        };
        using var conn = Conn();
        using var cmd = new SqlCmd(conn, $@"
            SELECT v.*, COALESCE(a.name,'') an, COALESCE(p.name,'') pn
            FROM vouchers v
            LEFT JOIN accounts a ON a.id=v.account_id
            LEFT JOIN partners p ON p.id=v.partner_id
            WHERE v.status='audited' AND v.biz_date BETWEEN $f AND $t AND v.kind IN ('income','expense')
              {PartnerJoin(f.PartnerId)} {AccountJoin(f.AccountId)}
            ORDER BY v.biz_date, v.doc_no")
            .P("$f", f.From).P("$t", f.To);
        Bind(cmd, f, project: false);
        long inflow = 0, outflow = 0;
        foreach (var x in cmd.List(MapRow))
        {
            var isIn = x.kind == "income";
            if (isIn) inflow += x.amount; else outflow += x.amount;
            r.Rows.Add(new ReportRow
            {
                Cells = new object?[]
                {
                    x.date, x.docNo, isIn ? "收入" : "支出", x.nature, x.account, x.partner, x.summary,
                    isIn ? x.amount : null, isIn ? null : x.amount,
                }
            });
        }
        r.TotalRow = new object?[] { "合计", "", "", "", "", "", $"{r.Rows.Count} 笔", inflow, outflow };
        r.Note = $"资金流入合计 {Money.Format(inflow)}，资金流出合计 {Money.Format(outflow)}，净额 {Money.FormatSigned(inflow - outflow)}。";
        return r;
    }

    private ReportResult TransferDetail(ReportFilter f)
    {
        var r = new ReportResult
        {
            Id = "transfer_detail", Title = "内部转账明细", Period = P(f),
            Basis = "内部资金调拨：不计入收支统计，不影响总资金；手续费单独作为支出单据登记。",
            Columns = new()
            {
                new() { Header = "日期", Width = 92 },
                new() { Header = "单据编号", Width = 122 },
                new() { Header = "转出账户", Width = 160 },
                new() { Header = "转入账户", Width = 160 },
                new() { Header = "摘要", Width = 240 },
                new() { Header = "转账金额", Width = 130, Numeric = true, Format = "money" },
                new() { Header = "手续费", Width = 100, Numeric = true, Format = "money" },
            },
        };
        using var conn = Conn();
        using var cmd = new SqlCmd(conn, $@"
            SELECT v.*, COALESCE(a.name,'') an, COALESCE(a2.name,'') an2
            FROM vouchers v
            LEFT JOIN accounts a ON a.id=v.account_id
            LEFT JOIN accounts a2 ON a2.id=v.to_account_id
            WHERE v.status='audited' AND v.kind='transfer' AND v.biz_date BETWEEN $f AND $t
              {AccountJoin(f.AccountId)}
            ORDER BY v.biz_date, v.doc_no")
            .P("$f", f.From).P("$t", f.To);
        Bind(cmd, f, partner: false, project: false);
        long a = 0, fee = 0;
        foreach (var x in cmd.List(MapRow))
        {
            a += x.amount; fee += x.fee;
            r.Rows.Add(new ReportRow { Cells = new object?[] { x.date, x.docNo, x.account, x.account2, x.summary, x.amount, x.fee } });
        }
        r.TotalRow = new object?[] { "合计", "", "", "", $"{r.Rows.Count} 笔", a, fee };
        return r;
    }

    private ReportResult CashByPeriod(ReportFilter f, bool byYear)
    {
        var keyLen = byYear ? 4 : 7;
        var r = new ReportResult
        {
            Id = byYear ? "cash_year" : "cash_month",
            Title = byYear ? "收支年报表" : "收支月报表",
            Period = P(f),
            Basis = "资金口径发生额：按月/年汇总实际资金流入与流出，排除内部转账。",
            Columns = new()
            {
                new() { Header = byYear ? "年度" : "月份", Width = 110 },
                new() { Header = "资金流入", Width = 150, Numeric = true, Format = "money" },
                new() { Header = "资金流出", Width = 150, Numeric = true, Format = "money" },
                new() { Header = "净额", Width = 150, Numeric = true, Format = "money" },
                new() { Header = "笔数", Width = 80, Numeric = true },
            },
        };
        using var conn = Conn();
        using var cmd = new SqlCmd(conn, $@"
            SELECT substr(biz_date,1,{keyLen}) k,
              COALESCE(SUM(CASE WHEN kind='income' THEN amount_cents ELSE 0 END),0) inflow,
              COALESCE(SUM(CASE WHEN kind='expense' THEN amount_cents ELSE 0 END),0) outflow,
              COUNT(1) cnt
            FROM vouchers
            WHERE status='audited' AND kind IN ('income','expense') AND biz_date BETWEEN $f AND $t
            GROUP BY k ORDER BY k")
            .P("$f", f.From).P("$t", f.To);
        long ti = 0, to = 0;
        foreach (var x in cmd.List(r2 => (k: R.S(r2, "k"), i: R.L(r2, "inflow"), o: R.L(r2, "outflow"), c: R.L(r2, "cnt"))))
        {
            ti += x.i; to += x.o;
            r.Rows.Add(new ReportRow { Cells = new object?[] { x.k, x.i, x.o, x.i - x.o, x.c } });
        }
        r.TotalRow = new object?[] { "合计", ti, to, ti - to, r.Rows.Sum(x => Convert.ToInt64(x.Cells[4])) };
        r.Note = "月/年发生额不可相加为期末余额；余额请查看账户报表。";
        return r;
    }

    private ReportResult SubjectSummary(ReportFilter f)
    {
        var r = new ReportResult
        {
            Id = "subject_summary", Title = "科目汇总", Period = P(f),
            Basis = "业务发生口径：按收入/支出科目汇总期间发生额（不含收付款单据）。",
            Columns = new()
            {
                new() { Header = "科目编码", Width = 100 },
                new() { Header = "科目名称", Width = 180 },
                new() { Header = "方向", Width = 70 },
                new() { Header = "发生额", Width = 160, Numeric = true, Format = "money" },
                new() { Header = "笔数", Width = 80, Numeric = true },
            },
        };
        using var conn = Conn();
        using var cmd = new SqlCmd(conn, $@"
            SELECT COALESCE(s.code,'(未指定)') code, COALESCE(s.name,'未指定科目') name,
                   COALESCE(s.direction,'') dir,
                   COALESCE(SUM(v.amount_cents),0) amt, COUNT(1) cnt
            FROM vouchers v LEFT JOIN subjects s ON s.id=v.subject_id
            WHERE v.status='audited' AND v.biz_date BETWEEN $f AND $t
              AND ({RevenueCond} OR {CostCond})
            GROUP BY s.id ORDER BY code")
            .P("$f", f.From).P("$t", f.To);
        long total = 0;
        foreach (var x in cmd.List(r2 => (code: R.S(r2, "code"), name: R.S(r2, "name"), dir: R.S(r2, "dir"), amt: R.L(r2, "amt"), cnt: R.L(r2, "cnt"))))
        {
            total += x.amt;
            r.Rows.Add(new ReportRow
            {
                Cells = new object?[] { x.code, x.name, x.dir == "in" ? "收入" : x.dir == "out" ? "支出" : "—", x.amt, x.cnt }
            });
        }
        r.TotalRow = new object?[] { "合计", "", "", total, r.Rows.Sum(x => Convert.ToInt64(x.Cells[4])) };
        r.Note = "未指定科目的业务计入“(未指定)”，建议补录科目以便分析。";
        return r;
    }
}
