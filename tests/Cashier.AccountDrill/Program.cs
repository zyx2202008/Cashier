using Cashier.Core;
using Cashier.Core.Services;

var dir = Path.GetFullPath(args[0]);
Directory.CreateDirectory(dir);
AppSession.Db = new Database(dir);
AppSession.Db.EnsureCreated();
AppSession.Current = new AuthService().Login("admin", "admin888");
var accounts = MasterDataService.ListAccounts(false);
var bank = accounts.First(a => a.Kind == "bank").Id;
var cash = accounts.First(a => a.Kind == "cash").Id;
var service = new VoucherService();
var id = service.Create(new Voucher { Kind = VoucherKind.Transfer, BizDate = "2026-04-10", AmountCents = 10000,
    FeeCents = 200, AccountId = bank, ToAccountId = cash, Summary = "转账及手续费" });
service.Audit(id);
var reports = new ReportService();
ReportFilter Filter(long? account) => new() { From = "2026-04-01", To = "2026-04-30", AccountId = account };
void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
foreach (var account in new long?[] { bank, cash, null })
{
    var summary = reports.Build("account_report", Filter(account));
    var detail = reports.Build(summary.DrillReportId!, Filter(account));
    Check(summary.DrillReportId == "account_detail", "账户报表下钻至账户资金明细");
    Check(Convert.ToInt64(detail.TotalRow![7]) == Convert.ToInt64(summary.TotalRow![2]) &&
          Convert.ToInt64(detail.TotalRow[8]) == Convert.ToInt64(summary.TotalRow[3]),
          $"账户 {account?.ToString() ?? "全部"} 明细流入流出与汇总一致");
    var transfer = detail.Rows.Single(r => (string?)r.Cells[2] == "转账");
    Check((account == bank ? transfer.Cells[7] is null && Convert.ToInt64(transfer.Cells[8]) == 10000 :
          account == cash ? Convert.ToInt64(transfer.Cells[7]) == 10000 && transfer.Cells[8] is null :
          Convert.ToInt64(transfer.Cells[7]) == 10000 && Convert.ToInt64(transfer.Cells[8]) == 10000),
          $"账户 {account?.ToString() ?? "全部"} 转账方向正确");
}
var cashFlow = reports.Build("cash_detail", Filter(bank));
Check(cashFlow.Rows.All(r => (string?)r.Cells[2] != "转账") && Convert.ToInt64(cashFlow.TotalRow![8]) == 200,
      "收支明细保持不含转账、包含手续费");
void CorruptTransfer(long? from, long? to)
{
    using var conn = AppSession.Db.Open();
    new SqlCmd(conn, "UPDATE vouchers SET account_id=$from,to_account_id=$to WHERE id=$id")
        .P("$from", from).P("$to", to).P("$id", id).Exec();
}
foreach (var bad in new (long? from, long? to, string description)[]
         { (null, cash, "转出账户缺失"), (bank, null, "转入账户缺失"), (null, null, "两端账户缺失") })
{
    CorruptTransfer(bad.from, bad.to);
    foreach (var account in new long?[] { bank, cash, null })
    {
        var summary = reports.Build("account_report", Filter(account));
        var detail = reports.Build("account_detail", Filter(account));
        Check(Convert.ToInt64(detail.TotalRow![7]) == Convert.ToInt64(summary.TotalRow![2]) &&
              Convert.ToInt64(detail.TotalRow[8]) == Convert.ToInt64(summary.TotalRow[3]),
              $"异常旧数据：{bad.description}，账户 {account?.ToString() ?? "全部"} 勾稽一致");
    }
}
CorruptTransfer(bank, cash);
service.Unaudit(id, "测试反审核");
Check(reports.Build("account_detail", Filter(bank)).Rows.Count == 0, "反审核后转账和手续费不再计入账户明细");
