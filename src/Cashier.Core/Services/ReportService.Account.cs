using Microsoft.Data.Sqlite;

namespace Cashier.Core.Services;

public partial class ReportService
{
    // Account drilldown includes both sides of transfers, unlike the cash-flow report.
    private ReportResult AccountDetail(ReportFilter f)
    {
        var r = new ReportResult
        {
            Id = "account_detail", Title = "账户资金明细", Period = P(f),
            Basis = "账户资金流入和流出，包含内部转账及单独登记的手续费；余额调整单列，不计入流入流出。",
            Columns = new()
            {
                new() { Header = "日期", Width = 92 },
                new() { Header = "单据编号", Width = 122 },
                new() { Header = "类型", Width = 64 },
                new() { Header = "业务性质", Width = 92 },
                new() { Header = "账户", Width = 180 },
                new() { Header = "往来单位", Width = 140 },
                new() { Header = "摘要", Width = 240 },
                new() { Header = "流入", Width = 116, Numeric = true, Format = "money" },
                new() { Header = "流出", Width = 116, Numeric = true, Format = "money" },
                new() { Header = "余额调整", Width = 116, Numeric = true, Format = "money" },
            },
        };
        using var conn = Conn();
        using var cmd = new SqlCmd(conn, $@"
            SELECT v.*, COALESCE(a.name,'') an, COALESCE(a2.name,'') an2, COALESCE(p.name,'') pn,
                   a.id AS valid_from, a2.id AS valid_to
            FROM vouchers v
            LEFT JOIN accounts a ON a.id=v.account_id
            LEFT JOIN accounts a2 ON a2.id=v.to_account_id
            LEFT JOIN partners p ON p.id=v.partner_id
            WHERE v.status='audited' AND v.biz_date BETWEEN $f AND $t
              AND ((v.kind IN ('income','expense') AND a.id IS NOT NULL)
                OR (v.kind='transfer' AND (a.id IS NOT NULL OR a2.id IS NOT NULL)))
              {AccountJoin(f.AccountId)}
            ORDER BY v.biz_date, v.doc_no")
            .P("$f", f.From).P("$t", f.To);
        Bind(cmd, f, partner: false, project: false);
        long inflow = 0, outflow = 0;
        foreach (var item in cmd.List(reader => (row: MapRow(reader),
                     from: R.LN(reader, "valid_from"), to: R.LN(reader, "valid_to"))))
        {
            var x = item.row;
            var transfer = x.kind == "transfer";
            long? incoming = x.kind == "income" || (transfer && item.to.HasValue && (!f.AccountId.HasValue || item.to == f.AccountId)) ? x.amount : null;
            long? outgoing = x.kind == "expense" || (transfer && item.from.HasValue && (!f.AccountId.HasValue || item.from == f.AccountId)) ? x.amount : null;
            inflow += incoming ?? 0;
            outflow += outgoing ?? 0;
            r.Rows.Add(new ReportRow { Cells = new object?[]
            {
                x.date, x.docNo, transfer ? "转账" : x.kind == "income" ? "收入" : "支出", x.nature,
                transfer ? $"{x.account} → {x.account2}" : x.account, x.partner, x.summary, incoming, outgoing, null,
            }});
        }
        long adjustment=0;
        foreach(var a in OpeningBalanceService.Query(conn,f.From,f.To,"account",f.AccountId))
        {
            adjustment=checked(adjustment+a.DeltaCents);
            r.Rows.Add(new ReportRow { Cells=new object?[] {a.EffectiveDate,a.Number,"余额调整",a.StatusText,a.TargetName,"",a.Reason,null,null,a.DeltaCents} });
        }
        r.Rows=r.Rows.OrderBy(x=>x.Cells[0]?.ToString()).ThenBy(x=>x.Cells[1]?.ToString()).ToList();
        r.TotalRow = new object?[] { "合计", "", "", "", "", "", $"{r.Rows.Count} 笔", inflow, outflow,adjustment };
        r.Note = $"资金流入合计 {Money.Format(inflow)}，资金流出合计 {Money.Format(outflow)}，资金净额 {Money.FormatSigned(inflow - outflow)}；余额调整 {Money.FormatSigned(adjustment)}，合计余额变动 {Money.FormatSigned(checked(inflow-outflow+adjustment))}。";
        return r;
    }

    private ReportResult AccountReport(ReportFilter f)
    {
        var r = new ReportResult
        {
            Id = "account_report", Title = "账户报表", Period = "截至 " + f.To,
            Basis = "期末余额 = 期初余额 + 本期资金流入 − 本期资金流出 + 本期余额调整（含转账）。",
            Columns = new()
            {
                new() { Header = "账户", Width = 200 },
                new() { Header = "期初余额", Width = 150, Numeric = true, Format = "money" },
                new() { Header = "本期流入", Width = 150, Numeric = true, Format = "money" },
                new() { Header = "本期流出", Width = 150, Numeric = true, Format = "money" },
                new() { Header = "期末余额", Width = 150, Numeric = true, Format = "money" },
                new() { Header = "本期余额调整", Width = 150, Numeric = true, Format = "money" },
            },
            DrillReportId = "account_detail", DrillFilterKind = "account",
        };
        using var conn = Conn();
        var accounts = new SqlCmd(conn,"SELECT id,name,opening_cents FROM accounts ORDER BY code").List(x=>new Account {Id=R.L(x,"id"),Name=R.S(x,"name"),OpeningCents=R.L(x,"opening_cents")});
        if (f.AccountId.HasValue) accounts = accounts.Where(a => a.Id == f.AccountId).ToList();
        long tb = 0, ti = 0, to = 0, te = 0, ta=0;
        foreach (var a in accounts)
        {
            var opening = checked(a.OpeningCents + FlowBefore(conn, a.Id, f.From)+OpeningBalanceService.Sum(conn,"account",a.Id,DateTime.Parse(f.From).AddDays(-1).ToString("yyyy-MM-dd")));
            var (inflow, outflow) = FlowBetween(conn, a.Id, f.From, f.To);
            var adjustment=OpeningBalanceService.Sum(conn,"account",a.Id,f.To,f.From);
            var end = checked(opening + inflow - outflow+adjustment);
            ta=checked(ta+adjustment);
            tb += opening; ti += inflow; to += outflow; te += end;
            r.Rows.Add(new ReportRow
            {
                Cells = new object?[] { a.Name, opening, inflow, outflow, end,adjustment },
                DrillId = a.Id, DrillKind = "account",
            });
        }
        r.TotalRow = new object?[] { "合计", tb, ti, to, te,ta };
        r.Note = "内部转账只在账户之间转移，不改变全部账户合计。";
        return r;
    }

    private static long FlowBefore(SqliteConnection conn, long accountId, string from)
    {
        var d = DateTime.Parse(from).AddDays(-1).ToString("yyyy-MM-dd");
        var (i, o) = FlowBetween(conn, accountId, "1990-01-01", d);
        return i - o;
    }

    private static (long inflow, long outflow) FlowBetween(SqliteConnection conn, long accountId, string from, string to)
    {
        var x = new SqlCmd(conn, @"
            SELECT
              COALESCE(SUM(CASE WHEN kind='income' AND account_id=$a THEN amount_cents ELSE 0 END),0) in1,
              COALESCE(SUM(CASE WHEN kind='transfer' AND to_account_id=$a THEN amount_cents ELSE 0 END),0) in2,
              COALESCE(SUM(CASE WHEN kind='expense' AND account_id=$a THEN amount_cents ELSE 0 END),0) out1,
              COALESCE(SUM(CASE WHEN kind='transfer' AND account_id=$a THEN amount_cents ELSE 0 END),0) out2
            FROM vouchers
            WHERE status='audited' AND biz_date BETWEEN $f AND $t AND (account_id=$a OR (kind='transfer' AND to_account_id=$a))")
            .P("$a", accountId).P("$f", from).P("$t", to)
            .List(r => (i1: R.L(r, "in1"), i2: R.L(r, "in2"), o1: R.L(r, "out1"), o2: R.L(r, "out2"))).First();
        return (x.i1 + x.i2, x.o1 + x.o2);
    }

    private ReportResult AccountByPeriod(ReportFilter f, bool byYear)
    {
        var keyLen = byYear ? 4 : 7;
        var r = new ReportResult
        {
            Id = byYear ? "account_year" : "account_month",
            Title = byYear ? "账户年报表" : "账户月报表",
            Period = P(f),
            Basis = "按账户 × 期间汇总；期末 = 期初 + 流入 − 流出 + 余额调整，调整独立列示。",
            Columns = new()
            {
                new() { Header = "账户", Width = 180 },
                new() { Header = byYear ? "年度" : "月份", Width = 96 },
                new() { Header = "期初余额", Width = 140, Numeric = true, Format = "money" },
                new() { Header = "流入", Width = 130, Numeric = true, Format = "money" },
                new() { Header = "流出", Width = 130, Numeric = true, Format = "money" },
                new() { Header = "期末余额", Width = 140, Numeric = true, Format = "money" },
                new() { Header = "余额调整", Width = 140, Numeric = true, Format = "money" },
            },
        };
        using var conn = Conn();
        var accounts = new SqlCmd(conn,"SELECT id,name,opening_cents FROM accounts ORDER BY code").List(x=>new Account {Id=R.L(x,"id"),Name=R.S(x,"name"),OpeningCents=R.L(x,"opening_cents")});
        if (f.AccountId.HasValue) accounts = accounts.Where(a => a.Id == f.AccountId).ToList();
        foreach (var a in accounts)
        {
            var running = checked(a.OpeningCents + FlowBefore(conn, a.Id, f.From)+OpeningBalanceService.Sum(conn,"account",a.Id,DateTime.Parse(f.From).AddDays(-1).ToString("yyyy-MM-dd")));
            var rows = new SqlCmd(conn, $@"
                WITH movements AS (SELECT biz_date,
                  CASE WHEN kind='income' AND account_id=$a THEN amount_cents WHEN kind='transfer' AND to_account_id=$a THEN amount_cents ELSE 0 END i,
                  CASE WHEN kind='expense' AND account_id=$a THEN amount_cents WHEN kind='transfer' AND account_id=$a THEN amount_cents ELSE 0 END o,0 adjustment
                FROM vouchers
                WHERE status='audited' AND biz_date BETWEEN $f AND $t AND (account_id=$a OR (kind='transfer' AND to_account_id=$a))
                UNION ALL SELECT effective_date,0,0,delta_cents FROM balance_adjustments
                    WHERE kind='account' AND account_id=$a AND effective_date BETWEEN $f AND $t)
                SELECT substr(biz_date,1,{keyLen}) k,SUM(i) i,SUM(o) o,SUM(adjustment) adjustment FROM movements GROUP BY k ORDER BY k")
                .P("$a", a.Id).P("$f", f.From).P("$t", f.To)
                .List(x => (k: R.S(x, "k"), i: R.L(x, "i"), o: R.L(x, "o"),adjustment:R.L(x,"adjustment")));
            foreach (var x in rows)
            {
                var opening = running;
                running = checked(opening + x.i - x.o+x.adjustment);
                r.Rows.Add(new ReportRow { Cells = new object?[] { a.Name, x.k, opening, x.i, x.o, running,x.adjustment } });
            }
        }
        r.Note = "期末余额按期初 + 逐期净流入 + 余额调整滚动计算，不能由各月期末余额相加得到。";
        return r;
    }
}
