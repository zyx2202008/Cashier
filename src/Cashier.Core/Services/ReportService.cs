using Microsoft.Data.Sqlite;

namespace Cashier.Core.Services;

public partial class ReportService
{
    private const string RevenueCond =
        "((v.kind='income' AND v.nature IN ('SaleCash','OtherIn')) OR (v.kind='receivable' AND v.count_as_revenue=1))";
    private const string CostCond =
        "((v.kind='expense' AND v.nature IN ('PurchaseCash','OtherOut')) OR (v.kind='payable' AND v.count_as_cost=1))";

    public static readonly (string id, string title, string group)[] Catalog =
    {
        ("income_detail", "收入明细", "收支类"),
        ("cash_detail", "收支明细", "收支类"),
        ("expense_detail", "支出明细", "收支类"),
        ("payable_detail", "应付明细", "收支类"),
        ("receivable_detail", "应收明细", "收支类"),
        ("transfer_detail", "内部转账明细", "收支类"),
        ("cash_month", "收支月报表", "期间汇总"),
        ("cash_year", "收支年报表", "期间汇总"),
        ("subject_summary", "科目汇总", "期间汇总"),
        ("project_month", "项目月利润", "项目"),
        ("project_year", "项目年利润", "项目"),
        ("supplier_summary", "供应商应付汇总", "往来"),
        ("customer_summary", "客户应收汇总", "往来"),
        ("supplier_monthly_payable", "供应商应付汇总统计月报表", "往来"),
        ("customer_monthly_receivable", "客户应收汇总统计月报表", "往来"),
        ("period_invoice_cash", "期间开票与收款统计", "往来"),
        ("account_report", "账户报表", "账户"),
        ("account_month", "账户月报表", "账户"),
        ("account_year", "账户年报表", "账户"),
        ("invoice_out_detail", "销项发票明细", "发票"),
        ("invoice_in_detail", "进项发票明细", "发票"),
    };

    private static SqliteConnection Conn() => AppSession.Db.Open();

    private static string PartnerJoin(long? partnerId) => partnerId.HasValue ? " AND v.partner_id=$pid" : "";
    private static string ProjectJoin(long? projectId) => projectId.HasValue ? " AND v.project_id=$prj" : "";
    private static string AccountJoin(long? accountId) => accountId.HasValue ? " AND (v.account_id=$acc OR (v.kind='transfer' AND v.to_account_id=$acc))" : "";

    private static void Bind(SqlCmd cmd, ReportFilter f, bool partner = true, bool project = true, bool account = true, bool subject = false)
    {
        if (partner && f.PartnerId.HasValue) cmd.P("$pid", f.PartnerId);
        if (project && f.ProjectId.HasValue) cmd.P("$prj", f.ProjectId);
        if (account && f.AccountId.HasValue) cmd.P("$acc", f.AccountId);
        if (subject && f.SubjectId.HasValue) cmd.P("$sub", f.SubjectId);
    }

    private static string P(ReportFilter f) => $"{f.From} 至 {f.To}";

    public ReportResult Build(string id, ReportFilter f)
    {
        SqlUtil.ValidateDate(f.From, "报表开始日期");
        SqlUtil.ValidateDate(f.To, "报表结束日期");
        if (string.CompareOrdinal(f.From, f.To) > 0) throw new AppException("报表开始日期不能晚于结束日期。");
        return id switch
        {
            "income_detail" => IncomeDetail(f),
            "cash_detail" => CashDetail(f),
            "account_detail" => AccountDetail(f),
            "expense_detail" => ExpenseDetail(f),
            "payable_detail" => PayableDetail(f),
            "receivable_detail" => ReceivableDetail(f),
            "transfer_detail" => TransferDetail(f),
            "cash_month" => CashByPeriod(f, false),
            "cash_year" => CashByPeriod(f, true),
            "subject_summary" => SubjectSummary(f),
            "project_month" => ProjectProfit(f, false),
            "project_year" => ProjectProfit(f, true),
            "supplier_summary" => SupplierSummary(f),
            "customer_summary" => CustomerSummary(f),
            "supplier_monthly_payable" => MonthlyDebt(f, false),
            "customer_monthly_receivable" => MonthlyDebt(f, true),
            "period_invoice_cash" => PeriodInvoiceCash(f),
            "account_report" => AccountReport(f),
            "account_month" => AccountByPeriod(f, false),
            "account_year" => AccountByPeriod(f, true),
            "invoice_out_detail" => InvoiceDetail(f, InvoiceKind.Out),
            "invoice_in_detail" => InvoiceDetail(f, InvoiceKind.In),
            _ => throw new AppException("未知报表：" + id),
        };
    }

    // ---------- 行映射 ----------
    private record FlatRow(string date, string docNo, string kind, string nature, string account, string account2,
        string partner, string project, string subject, string summary, long amount, long fee, long settled, long inv);

    private static string GS(SqliteDataReader r, string name) => r.HasColumn(name) ? R.S(r, name) : "";
    private static long GL(SqliteDataReader r, string name) => r.HasColumn(name) ? R.L(r, name) : 0;

    private static FlatRow MapRow(SqliteDataReader r) => new(
        R.S(r, "biz_date"),
        R.S(r, "doc_no"),
        R.S(r, "kind"),
        NatureInfo.Label(NatureInfo.Parse(R.S(r, "nature"))),
        GS(r, "an"),
        GS(r, "an2"),
        GS(r, "pn"),
        GS(r, "prn"),
        GS(r, "sn"),
        R.S(r, "summary"),
        R.L(r, "amount_cents"),
        R.L(r, "fee_cents"),
        GL(r, "settled"),
        GL(r, "inv"));

}

internal static class ReaderExt
{
    public static bool HasColumn(this SqliteDataReader r, string name)
    {
        for (var i = 0; i < r.FieldCount; i++)
            if (string.Equals(r.GetName(i), name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
