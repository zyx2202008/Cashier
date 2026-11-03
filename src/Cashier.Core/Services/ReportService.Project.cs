namespace Cashier.Core.Services;

public partial class ReportService
{
    private ReportResult ProjectProfit(ReportFilter f, bool byYear)
    {
        var keyLen = byYear ? 4 : 7;
        var r = new ReportResult
        {
            Id = byYear ? "project_year" : "project_month",
            Title = byYear ? "项目年利润" : "项目月利润",
            Period = P(f),
            Basis = "业务发生口径（含税）：项目收入 − 项目成本费用；公共费用未分摊。",
            Columns = new()
            {
                new() { Header = "项目", Width = 200 },
                new() { Header = byYear ? "年度" : "月份", Width = 96 },
                new() { Header = "项目收入", Width = 140, Numeric = true, Format = "money" },
                new() { Header = "项目成本费用", Width = 140, Numeric = true, Format = "money" },
                new() { Header = "项目利润", Width = 140, Numeric = true, Format = "money" },
                new() { Header = "毛利率", Width = 90, Numeric = true, Format = "percent" },
            },
        };
        using var conn = Conn();
        using var cmd = new SqlCmd(conn, $@"
            SELECT COALESCE(pr.name,'(未指定项目)') pname, substr(v.biz_date,1,{keyLen}) k,
              COALESCE(SUM(CASE WHEN {RevenueCond} THEN v.amount_cents ELSE 0 END),0) rev,
              COALESCE(SUM(CASE WHEN {CostCond} THEN v.amount_cents ELSE 0 END),0) cost
            FROM vouchers v LEFT JOIN projects pr ON pr.id=v.project_id
            WHERE v.status='audited' AND v.biz_date BETWEEN $f AND $t AND ({RevenueCond} OR {CostCond})
              {ProjectJoin(f.ProjectId)}
            GROUP BY v.project_id, k ORDER BY pname, k")
            .P("$f", f.From).P("$t", f.To);
        Bind(cmd, f, partner: false, account: false);
        long tr = 0, tc = 0;
        foreach (var x in cmd.List(r2 => (name: R.S(r2, "pname"), k: R.S(r2, "k"), rev: R.L(r2, "rev"), cost: R.L(r2, "cost"))))
        {
            tr += x.rev; tc += x.cost;
            var profit = x.rev - x.cost;
            r.Rows.Add(new ReportRow
            {
                Cells = new object?[] { x.name, x.k, x.rev, x.cost, profit, x.rev == 0 ? (decimal?)null : (decimal)profit / x.rev }
            });
        }
        r.TotalRow = new object?[] { "合计", "", tr, tc, tr - tc, tr == 0 ? null : (decimal)(tr - tc) / tr };
        r.Note = "不含内部转账与借款等资金往来；未分摊公共费用，不等同于企业最终会计利润。";
        return r;
    }
}
