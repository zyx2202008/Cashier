using System.Diagnostics;
using Cashier.Core;
using Cashier.Core.Services;
using ClosedXML.Excel;

if (args.Length == 2 && args[1] == "--perf") return PerfHarness.Run(args[0]);
if (args.Length != 1 || Directory.Exists(args[0])) throw new Exception("New isolated directory required");
AppSession.Db = new Database(args[0]);
AppSession.Db.EnsureCreated();
var admin = new AuthService().Login("admin", "admin888");
AppSession.Current = admin;

var checks = 0;
var failures = 0;
void Check(string name, bool ok)
{
    checks++;
    if (ok) return;
    failures++;
    Console.WriteLine("FAIL | " + name);
}
void Eq(string name, long actual, long expected) =>
    Check($"{name} actual={actual} expected={expected}", actual == expected);
void Reject(string name, Action action)
{
    try
    {
        action();
        Check(name + " (expected rejection)", false);
    }
    catch (AppException)
    {
        Check(name, true);
    }
}
long Scalar(string sql)
{
    using var conn = AppSession.Db.Open();
    return new SqlCmd(conn, sql).Long();
}

// Preserve legacy-code fixtures for report ordering and leading-zero export coverage.
long AddPartner(string code, string name, string kind)
{
    var id = MasterDataService.SavePartner(new Partner { Name = name, Kind = kind });
    using var conn = AppSession.Db.Open();
    new SqlCmd(conn, "UPDATE partners SET code=$c WHERE id=$i").P("$c", code).P("$i", id).Exec();
    return id;
}

var a = AddPartner("KH-A", "甲客户", "customer");
var b = AddPartner("KH-B", "乙客户", "customer");
var c = AddPartner("GY-C", "丙供应商", "supplier");
var d = AddPartner("KH-D", "丁单位", "both");
var e = AddPartner("KH-E", "戊客户", "customer");
var f = AddPartner("KH-F", "己客户", "customer");
var account = MasterDataService.ListAccounts(false).First().Id;

var voucherService = new VoucherService();
long V(VoucherKind kind, VoucherNature nature, string date, long cents, long? partner, bool audit = true, string summary = "报表测试")
{
    var id = voucherService.Create(new Voucher
    {
        Kind = kind, Nature = nature, BizDate = date, AmountCents = cents, PartnerId = partner,
        AccountId = kind is VoucherKind.Income or VoucherKind.Expense ? account : null,
        Summary = summary,
    });
    if (audit) voucherService.Audit(id);
    return id;
}

var invoiceService = new InvoiceService();
long I(InvoiceKind kind, string no, long partner, string date, long gross, long net, long tax, long? original = null, string? deliver = null) =>
    invoiceService.Save(new Invoice
    {
        Kind = kind, InvoiceType = "增值税专用发票", InvoiceNo = no, PartnerId = partner,
        RegDate = date, DeliverDate = deliver, GrossCents = gross, NetCents = net, TaxCents = tax,
        RateBp = 1300, OriginalInvoiceId = original,
    });

var aInv1 = I(InvoiceKind.Out, "OUT-A-1", a, "2025-12-01", 1_130_000, 1_000_000, 130_000);
I(InvoiceKind.Out, "OUT-A-2", a, "2025-12-31", 565_000, 500_000, 65_000, deliver: "2026-01-10");
I(InvoiceKind.Out, "OUT-A-3", a, "2026-01-01", 226_000, 200_000, 26_000);
I(InvoiceKind.Out, "OUT-A-R1", a, "2026-01-15", -113_000, -100_000, -13_000, original: aInv1);
var aVoidInvoice = I(InvoiceKind.Out, "OUT-A-VOID", a, "2025-12-15", 999_900, 884_867, 115_033);
invoiceService.Void(aVoidInvoice, "报表状态过滤");
I(InvoiceKind.Out, "OUT-B-1", b, "2025-12-20", 339_000, 300_000, 39_000);
I(InvoiceKind.Out, "OUT-D-1", d, "2025-12-05", 113_000, 100_000, 13_000);
var cInv1 = I(InvoiceKind.In, "IN-C-1", c, "2025-12-10", 452_000, 400_000, 52_000);
I(InvoiceKind.In, "IN-D-1", d, "2025-12-10", 226_000, 200_000, 26_000);
I(InvoiceKind.In, "IN-C-R1", c, "2026-01-20", -113_000, -100_000, -13_000, original: cInv1);

V(VoucherKind.Income, VoucherNature.CustomerReceipt, "2025-12-01", 300_000, a);
V(VoucherKind.Income, VoucherNature.CustomerReceipt, "2025-12-31", 200_000, a);
V(VoucherKind.Income, VoucherNature.CustomerReceipt, "2026-01-31", 100_000, a);
V(VoucherKind.Income, VoucherNature.CustomerReceipt, "2026-01-10", 700_000, a, audit: false);
var aVoidPay = V(VoucherKind.Income, VoucherNature.CustomerReceipt, "2026-01-20", 800_000, a);
voucherService.Void(aVoidPay, "报表状态过滤");
V(VoucherKind.Income, VoucherNature.CustomerReceipt, "2025-12-20", 200_000, b);
V(VoucherKind.Income, VoucherNature.CustomerReceipt, "2025-12-05", 113_000, d);
V(VoucherKind.Expense, VoucherNature.SupplierPayment, "2025-12-10", 400_000, c);
V(VoucherKind.Expense, VoucherNature.SupplierPayment, "2025-12-10", 226_000, d);

var reports = new ReportService();
ReportResult Run(InvoiceKind direction, string from, string to, long? partner = null) =>
    reports.Build("period_invoice_cash", new ReportFilter
    {
        From = from, To = to, PartnerId = partner, InvoiceDirection = direction,
    });
ReportRow? Row(ReportResult result, string code) => result.Rows.FirstOrDefault(x => (string?)x.Cells[0] == code);
long C(object? row, int index) => row switch
{
    object?[] cells => index >= cells.Length ? long.MinValue : Convert.ToInt64(cells[index] ?? 0L),
    ReportRow reportRow => C(reportRow.Cells, index),
    _ => long.MinValue,
};

Check("catalog has report", ReportService.Catalog.Any(x => x.id == "period_invoice_cash" && x.title == "期间开票与收款统计"));
Check("catalog report count", ReportService.Catalog.Length == 21);
Check("default direction is out", new ReportFilter().InvoiceDirection == InvoiceKind.Out);

var decOut = Run(InvoiceKind.Out, "2025-12-01", "2025-12-31");
Check("dec out rows", decOut.Rows.Count == 3);
Check("dec out ordered by code", decOut.Rows.Select(x => (string)x.Cells[0]!).SequenceEqual(new[] { "KH-A", "KH-B", "KH-D" }));
var aDec = Row(decOut, "KH-A");
Check("dec A blue", C(aDec, 2) == 2);
Check("dec A red", C(aDec, 3) == 0);
Check("dec A net count", C(aDec, 4) == 2);
Check("dec A gross", C(aDec, 5) == 1_695_000);
Check("dec A net amount", C(aDec, 6) == 1_500_000);
Check("dec A tax", C(aDec, 7) == 195_000);
Check("dec A cash", C(aDec, 8) == 500_000);
Check("dec A difference", C(aDec, 9) == 1_195_000);
Check("dec A excludes void invoice", C(aDec, 2) == 2);
var bDec = Row(decOut, "KH-B");
Check("dec B values", C(bDec, 2) == 1 && C(bDec, 5) == 339_000 && C(bDec, 8) == 200_000 && C(bDec, 9) == 139_000);
var dDec = Row(decOut, "KH-D");
Check("dec both partner values", C(dDec, 2) == 1 && C(dDec, 5) == 113_000 && C(dDec, 8) == 113_000 && C(dDec, 9) == 0);
Check("dec out excludes supplier", Row(decOut, "GY-C") == null);
Check("dec out excludes no-activity partner", Row(decOut, "KH-F") == null);
Check("dec out total blue", C(decOut.TotalRow, 2) == 4);
Check("dec out total red", C(decOut.TotalRow, 3) == 0);
Check("dec out total net count", C(decOut.TotalRow, 4) == 4);
Check("dec out total gross", C(decOut.TotalRow, 5) == 2_147_000);
Check("dec out total net", C(decOut.TotalRow, 6) == 1_900_000);
Check("dec out total tax", C(decOut.TotalRow, 7) == 247_000);
Check("dec out total cash", C(decOut.TotalRow, 8) == 813_000);
Check("dec out total difference", C(decOut.TotalRow, 9) == 1_334_000);
for (var i = 2; i <= 9; i++)
{
    var sum = decOut.Rows.Sum(x => Convert.ToInt64(x.Cells[i] ?? 0L));
    Eq($"dec out total col{i} equals row sum", C(decOut.TotalRow, i), sum);
}
Check("out headers", decOut.Columns[8].Header == "期间收款金额" && decOut.Columns[9].Header == "票款差额（含税开票-收款）");
Check("out numeric formats", decOut.Columns.Skip(2).All(x => x.Numeric) && decOut.Columns[2].Format == "int" && decOut.Columns[5].Format == "money");

var decA = Run(InvoiceKind.Out, "2025-12-01", "2025-12-31", a);
Check("A filter one row", decA.Rows.Count == 1 && (string?)decA.Rows[0].Cells[0] == "KH-A");
for (var i = 2; i <= 9; i++) Eq($"A filter total col{i} matches row", C(decA.TotalRow, i), C(aDec, i));
Check("no-activity partner filter empty", Run(InvoiceKind.Out, "2025-12-01", "2025-12-31", f).Rows.Count == 0);

var decIn = Run(InvoiceKind.In, "2025-12-01", "2025-12-31");
Check("dec in rows", decIn.Rows.Count == 2);
var cDec = Row(decIn, "GY-C");
var dInDec = Row(decIn, "KH-D");
Check("dec in C values", C(cDec, 2) == 1 && C(cDec, 5) == 452_000 && C(cDec, 8) == 400_000 && C(cDec, 9) == 52_000);
Check("dec in both values", C(dInDec, 5) == 226_000 && C(dInDec, 8) == 226_000 && C(dInDec, 9) == 0);
Check("dec in excludes customers", Row(decIn, "KH-A") == null && Row(decIn, "KH-B") == null);
Check("dec in total", C(decIn.TotalRow, 2) == 2 && C(decIn.TotalRow, 5) == 678_000 && C(decIn.TotalRow, 8) == 626_000 && C(decIn.TotalRow, 9) == 52_000);
Check("in headers", decIn.Columns[8].Header == "期间付款金额" && decIn.Columns[9].Header == "票款差额（含税收票-付款）");

var janOut = Run(InvoiceKind.Out, "2026-01-01", "2026-01-31");
Check("jan out rows", janOut.Rows.Count == 1);
var aJan = Row(janOut, "KH-A");
Check("jan A counts", C(aJan, 2) == 1 && C(aJan, 3) == 1 && C(aJan, 4) == 0);
Check("jan A amounts", C(aJan, 5) == 113_000 && C(aJan, 6) == 100_000 && C(aJan, 7) == 13_000);
Check("jan A cash excludes pending and void", C(aJan, 8) == 100_000);
Check("jan A difference", C(aJan, 9) == 13_000);
Check("jan out total", C(janOut.TotalRow, 5) == 113_000 && C(janOut.TotalRow, 8) == 100_000 && C(janOut.TotalRow, 9) == 13_000);

var janIn = Run(InvoiceKind.In, "2026-01-01", "2026-01-31");
Check("jan in rows", janIn.Rows.Count == 1);
var cJan = Row(janIn, "GY-C");
Check("jan in red counts", C(cJan, 2) == 0 && C(cJan, 3) == 1 && C(cJan, 4) == -1);
Check("jan in red amounts", C(cJan, 5) == -113_000 && C(cJan, 6) == -100_000 && C(cJan, 7) == -13_000);
Check("jan in red difference", C(cJan, 8) == 0 && C(cJan, 9) == -113_000);

var jan15 = Run(InvoiceKind.Out, "2026-01-15", "2026-01-15");
Check("same-day single row", jan15.Rows.Count == 1);
Check("same-day red only", C(Row(jan15, "KH-A"), 2) == 0 && C(Row(jan15, "KH-A"), 3) == 1 && C(Row(jan15, "KH-A"), 4) == -1);
Check("same-day negative amounts", C(Row(jan15, "KH-A"), 5) == -113_000 && C(Row(jan15, "KH-A"), 6) == -100_000 && C(Row(jan15, "KH-A"), 7) == -13_000);
Check("same-day negative total", C(jan15.TotalRow, 5) == -113_000 && C(jan15.TotalRow, 9) == -113_000);

var dec1 = Run(InvoiceKind.Out, "2025-12-01", "2025-12-01");
Check("start date inclusive invoice", C(Row(dec1, "KH-A"), 5) == 1_130_000);
Check("start date inclusive cash", C(Row(dec1, "KH-A"), 8) == 300_000);
Check("start date difference", C(Row(dec1, "KH-A"), 9) == 830_000);
var dec31 = Run(InvoiceKind.Out, "2025-12-31", "2025-12-31");
Check("end date inclusive invoice", C(Row(dec31, "KH-A"), 5) == 565_000);
Check("end date inclusive cash", C(Row(dec31, "KH-A"), 8) == 200_000);
var jan1 = Run(InvoiceKind.Out, "2026-01-01", "2026-01-01");
Check("new year boundary", C(Row(jan1, "KH-A"), 5) == 226_000 && C(Row(jan1, "KH-A"), 8) == 0);
var jan31 = Run(InvoiceKind.Out, "2026-01-31", "2026-01-31");
Check("cash-only row", C(Row(jan31, "KH-A"), 5) == 0 && C(Row(jan31, "KH-A"), 8) == 100_000 && C(Row(jan31, "KH-A"), 9) == -100_000);

var cross = Run(InvoiceKind.Out, "2025-12-01", "2026-01-31");
Check("cross-year rows", cross.Rows.Count == 3);
var aCross = Row(cross, "KH-A");
Check("cross A counts", C(aCross, 2) == 3 && C(aCross, 3) == 1 && C(aCross, 4) == 2);
Check("cross A amounts", C(aCross, 5) == 1_808_000 && C(aCross, 6) == 1_600_000 && C(aCross, 7) == 208_000);
Check("cross A cash and difference", C(aCross, 8) == 600_000 && C(aCross, 9) == 1_208_000);
Check("cross total", C(cross.TotalRow, 2) == 5 && C(cross.TotalRow, 3) == 1 && C(cross.TotalRow, 4) == 4
    && C(cross.TotalRow, 5) == 2_260_000 && C(cross.TotalRow, 6) == 2_000_000 && C(cross.TotalRow, 7) == 260_000
    && C(cross.TotalRow, 8) == 913_000 && C(cross.TotalRow, 9) == 1_347_000);
Check("period shows direction", cross.Period.Contains("销项") && decIn.Period.Contains("进项"));
Check("basis explains dates", cross.Basis.Contains("登记日期") && cross.Basis.Contains("业务日期"));
Check("note explains red and difference", cross.Note.Contains("红字") && cross.Note.Contains("票款差额") && cross.Note.Contains("核销"));
Check("file name has direction and dates", cross.FileName == "期间开票与收款统计_销项_2025-12-01_2026-01-31");
Check("money formatting", Money.Format(1_808_000) == "18,080.00");
Check("negative money formatting", Money.Format(-113_000) == "-1,130.00");

var empty = Run(InvoiceKind.Out, "2030-01-01", "2030-01-31");
Check("empty interval no rows", empty.Rows.Count == 0);
Check("empty interval total exists", empty.TotalRow != null);
Check("empty interval totals zero", Enumerable.Range(2, 8).All(i => C(empty.TotalRow, i) == 0));
Check("empty interval no NaN", empty.TotalRow!.All(x => !(x is double dd && double.IsNaN(dd))));

var logsBefore = Scalar("SELECT COUNT(*) FROM audit_log");
var fundsBefore = VoucherService.TotalFunds();
var readAgain = Run(InvoiceKind.Out, "2025-12-01", "2025-12-31");
Check("report still returns data", readAgain.Rows.Count == 3);
Check("report writes no audit log", Scalar("SELECT COUNT(*) FROM audit_log") == logsBefore);
Check("report leaves funds unchanged", VoucherService.TotalFunds() == fundsBefore);
Check("database is current version", AppSession.Db.GetUserVersion() == Database.CurrentVersion);
Check("foreign keys intact", Scalar("SELECT COUNT(*) FROM pragma_foreign_key_check") == 0);

PeriodService.Close("202512");
var closed = Run(InvoiceKind.Out, "2025-12-01", "2025-12-31");
Check("closed period still readable", closed.Rows.Count == 3 && C(Row(closed, "KH-A"), 5) == 1_695_000);
Check("note discloses close behavior", closed.Note.Contains("关账"));
PeriodService.Reopen("202512", "报表测试结束");

var detailA = reports.GetPeriodInvoiceCashDetail(a, "2025-12-01", "2025-12-31", InvoiceKind.Out);
Check("detail excludes void invoice", detailA.Invoices.Count == 2 && detailA.BlueCount == 2 && detailA.RedCount == 0 && detailA.NetCount == 2);
Check("detail invoice totals", detailA.GrossCents == 1_695_000 && detailA.NetCents == 1_500_000 && detailA.TaxCents == 195_000);
Check("detail cash totals", detailA.Vouchers.Count == 2 && detailA.CashCents == 500_000 && detailA.DifferenceCents == 1_195_000);
Check("detail invoice order", detailA.Invoices.Select(x => x.RegDate).SequenceEqual(new[] { "2025-12-01", "2025-12-31" }));
var detailJan = reports.GetPeriodInvoiceCashDetail(a, "2026-01-01", "2026-01-31", InvoiceKind.Out);
Check("detail red invoice counts", detailJan.Invoices.Count == 2 && detailJan.BlueCount == 1 && detailJan.RedCount == 1 && detailJan.NetCount == 0);
Check("detail red invoice totals", detailJan.GrossCents == 113_000 && detailJan.NetCents == 100_000 && detailJan.TaxCents == 13_000);
Check("detail receipts exclude pending and void", detailJan.Vouchers.Count == 1 && detailJan.CashCents == 100_000 && detailJan.DifferenceCents == 13_000);

var detailC = reports.GetPeriodInvoiceCashDetail(c, "2025-12-01", "2025-12-31", InvoiceKind.In);
Check("in detail counts", detailC.Invoices.Count == 1 && detailC.Vouchers.Count == 1 && detailC.CashCents == 400_000);
var inParts = reports.BuildPeriodInvoiceCashDetailReports(detailC);
Check("in detail report titles", inParts.Count == 2 && inParts[0].Title == "进项发票明细" && inParts[1].Title == "付款明细");
Check("in detail invoice report total", inParts[0].Rows.Count == 1 && C(inParts[0].TotalRow, 3) == 452_000);
Check("in detail cash report total", inParts[1].Rows.Count == 1 && C(inParts[1].TotalRow, 3) == 400_000);
var outParts = reports.BuildPeriodInvoiceCashDetailReports(detailJan);
Check("out detail report titles", outParts[0].Title == "销项发票明细" && outParts[1].Title == "收款明细");
Check("out detail invoice report total", C(outParts[0].TotalRow, 3) == 113_000 && outParts[0].TotalRow![1] is string);
Check("out detail invoice report label", (string?)outParts[0].TotalRow![1] == "2 张（蓝 1 / 红 1）");

Reject("invalid from date", () => { _ = Run(InvoiceKind.Out, "2026-13-01", "2026-01-31"); });
Reject("from after to", () => { _ = Run(InvoiceKind.Out, "2026-02-01", "2026-01-01"); });
Reject("missing partner detail", () => { _ = reports.GetPeriodInvoiceCashDetail(99_999, "2025-12-01", "2025-12-31", InvoiceKind.Out); });
Reject("detail from after to", () => { _ = reports.GetPeriodInvoiceCashDetail(a, "2026-02-01", "2026-01-01", InvoiceKind.Out); });

var reportPath = Path.Combine(args[0], "report.xlsx");
ExcelService.ExportReport(cross, reportPath);
using (var wb = new XLWorkbook(reportPath))
{
    var ws = wb.Worksheet(1);
    Check("excel title", ws.Cell(1, 1).GetString() == "期间开票与收款统计");
    Check("excel period", ws.Cell(2, 1).GetString().Contains("2025-12-01") && ws.Cell(2, 1).GetString().Contains("2026-01-31") && ws.Cell(2, 1).GetString().Contains("销项"));
    Check("excel basis", ws.Cell(3, 1).GetString().Contains("登记日期") && ws.Cell(3, 1).GetString().Contains("业务日期"));
    Check("excel header", ws.Cell(5, 1).GetString() == "单位编码" && ws.Cell(5, 6).GetString() == "含税金额合计");
    Check("excel count numeric", ws.Cell(6, 3).DataType == XLDataType.Number && ws.Cell(6, 3).GetDouble() == 3d);
    Check("excel gross numeric", ws.Cell(6, 6).DataType == XLDataType.Number && ws.Cell(6, 6).GetDouble() == 18080d);
    Check("excel difference numeric", ws.Cell(6, 10).DataType == XLDataType.Number && ws.Cell(6, 10).GetDouble() == 12080d);
    Check("excel note", ws.Cell(11, 1).GetString().Contains("备注：") && ws.Cell(11, 1).GetString().Contains("红字发票"));
}

var detailPath = Path.Combine(args[0], "detail.xlsx");
ExcelService.ExportReports(detailPath, inParts.ToArray());
using (var wb = new XLWorkbook(detailPath))
{
    Check("detail workbook sheets", wb.Worksheets.Select(x => x.Name).SequenceEqual(new[] { "进项发票明细", "付款明细" }));
    Check("detail invoice money cell", wb.Worksheet(1).Cell(6, 4).DataType == XLDataType.Number && wb.Worksheet(1).Cell(6, 4).GetDouble() == 4520d);
    Check("detail cash money cell", wb.Worksheet(2).Cell(6, 4).DataType == XLDataType.Number && wb.Worksheet(2).Cell(6, 4).GetDouble() == 4000d);
}

var eReceivable = V(VoucherKind.Receivable, VoucherNature.None, "2026-02-05", 100_000, e, summary: "戊客户应收");
var eReceipt = V(VoucherKind.Income, VoucherNature.CustomerReceipt, "2026-02-10", 50_000, e, summary: "戊客户回款");
I(InvoiceKind.Out, "OUT-E-1", e, "2026-02-12", 113_000, 100_000, 13_000);
var febBefore = Run(InvoiceKind.Out, "2026-02-01", "2026-02-28");
Check("feb row before settlement", C(Row(febBefore, "KH-E"), 5) == 113_000 && C(Row(febBefore, "KH-E"), 8) == 50_000 && C(Row(febBefore, "KH-E"), 9) == 63_000);
new SettlementService().Allocate(eReceipt, eReceivable, 50_000);
var febAfter = Run(InvoiceKind.Out, "2026-02-01", "2026-02-28");
Check("settlement does not change report", C(Row(febAfter, "KH-E"), 5) == 113_000 && C(Row(febAfter, "KH-E"), 8) == 50_000 && C(Row(febAfter, "KH-E"), 9) == 63_000);
Check("settlement applied", new SettlementService().UnallocatedOf(eReceipt) == 0 && new SettlementService().UnsettledOf(eReceivable) == 50_000);

// ---------------- 深度边界与组合 ----------------
void RowEq(ReportResult result, string code, params long[] values)
{
    var row = Row(result, code);
    Check($"{result.Period} row {code} exists", row != null);
    for (var i = 0; i < values.Length; i++) Eq($"{result.Period} {code} col{i + 2}", C(row, i + 2), values[i]);
}
void Invariants(ReportResult result)
{
    foreach (var row in result.Rows)
    {
        Check($"{result.FileName} row cell count", row.Cells.Length == result.Columns.Count);
        Eq($"{result.FileName} net count rule", C(row, 4), C(row, 2) - C(row, 3));
        Eq($"{result.FileName} gross rule", C(row, 5), C(row, 6) + C(row, 7));
        Eq($"{result.FileName} difference rule", C(row, 9), C(row, 5) - C(row, 8));
    }
    Eq($"{result.FileName} total net count rule", C(result.TotalRow, 4), C(result.TotalRow, 2) - C(result.TotalRow, 3));
    Eq($"{result.FileName} total gross rule", C(result.TotalRow, 5), C(result.TotalRow, 6) + C(result.TotalRow, 7));
    Eq($"{result.FileName} total difference rule", C(result.TotalRow, 9), C(result.TotalRow, 5) - C(result.TotalRow, 8));
    for (var i = 2; i <= 9; i++)
        Eq($"{result.FileName} total col{i} equals row sum", C(result.TotalRow, i), result.Rows.Sum(x => C(x, i)));
}

var secondAccount = MasterDataService.SaveAccount(new Account { Code = "BANK-2", Name = "第二个账户", Kind = "bank" });
var gCash = AddPartner("G-CASH", "现金客户G", "customer");
var hInv = AddPartner("G-INV", "仅票客户H", "customer");
var iStop = AddPartner("G-STOP", "停用客户I", "customer");
var jBoth = AddPartner("G-BOTH2", "双身份J", "both");
var kRed = AddPartner("G-RED", "红冲客户K", "customer");
var mNat = AddPartner("G-NAT", "全性质M", "both");
var lead = AddPartner("00123", "前导零客户", "customer");

I(InvoiceKind.Out, "OUT-H-1", hInv, "2027-06-11", 226_000, 200_000, 26_000);
I(InvoiceKind.Out, "OUT-I-1", iStop, "2027-06-12", 113_000, 100_000, 13_000);
I(InvoiceKind.Out, "OUT-J-OUT", jBoth, "2027-06-13", 339_000, 300_000, 39_000);
var kPre1 = I(InvoiceKind.Out, "OUT-K-PRE1", kRed, "2027-05-20", 1_130_000, 1_000_000, 130_000);
var kPre2 = I(InvoiceKind.Out, "OUT-K-PRE2", kRed, "2027-05-21", 226_000, 200_000, 26_000);
I(InvoiceKind.Out, "OUT-K-R1", kRed, "2027-06-15", -904_000, -800_000, -104_000, original: kPre1);
I(InvoiceKind.Out, "OUT-K-3", kRed, "2027-06-16", 113_000, 100_000, 13_000);
I(InvoiceKind.Out, "OUT-K-R2", kRed, "2027-06-16", -113_000, -100_000, -13_000, original: kPre2);
I(InvoiceKind.Out, "OUT-LEAD-1", lead, "2027-06-20", 11_300, 10_000, 1_300);
I(InvoiceKind.In, "IN-J-IN", jBoth, "2027-06-14", 226_000, 200_000, 26_000);
I(InvoiceKind.In, "IN-C-2027", c, "2027-06-25", 56_500, 50_000, 6_500);

V(VoucherKind.Income, VoucherNature.CustomerReceipt, "2027-06-10", 250_000, gCash);
V(VoucherKind.Receivable, VoucherNature.None, "2027-06-05", 400_000, gCash);
voucherService.Create(new Voucher
{
    Kind = VoucherKind.Income, Nature = VoucherNature.SaleCash, BizDate = "2027-06-06",
    AmountCents = 123_456, PartnerId = gCash, AccountId = account, Summary = "深度草稿",
}, submit: false);
V(VoucherKind.Income, VoucherNature.SaleCash, "2027-06-07", 234_567, gCash, audit: false);
var gVoidVoucher = V(VoucherKind.Income, VoucherNature.OtherIn, "2027-06-08", 345_678, gCash);
voucherService.Void(gVoidVoucher, "深度状态过滤");
var transferId = voucherService.Create(new Voucher
{
    Kind = VoucherKind.Transfer, BizDate = "2027-06-19", AmountCents = 555_555,
    AccountId = account, ToAccountId = secondAccount, Summary = "深度内部转账",
});
voucherService.Audit(transferId);
V(VoucherKind.Income, VoucherNature.OtherIn, "2027-06-17", 777_777, null);
V(VoucherKind.Expense, VoucherNature.OtherOut, "2027-06-18", 888_888, null);
V(VoucherKind.Income, VoucherNature.SaleCash, "2027-06-19", 999_999, null);
foreach (var (nature, cents) in new[]
{
    (VoucherNature.SaleCash, 10_000L), (VoucherNature.CustomerReceipt, 20_000L),
    (VoucherNature.CustomerPrepay, 30_000L), (VoucherNature.SupplierRefund, 40_000L),
    (VoucherNature.OtherIn, 50_000L),
})
    V(VoucherKind.Income, nature, "2027-06-21", cents, mNat);
foreach (var (nature, cents) in new[]
{
    (VoucherNature.PurchaseCash, 11_000L), (VoucherNature.SupplierPayment, 22_000L),
    (VoucherNature.SupplierPrepay, 33_000L), (VoucherNature.CustomerRefund, 44_000L),
    (VoucherNature.OtherOut, 55_000L),
})
    V(VoucherKind.Expense, nature, "2027-06-22", cents, mNat);
V(VoucherKind.Income, VoucherNature.CustomerReceipt, "2027-06-23", 100_000, jBoth);
V(VoucherKind.Expense, VoucherNature.SupplierPayment, "2027-06-24", 80_000, jBoth);
V(VoucherKind.Income, VoucherNature.CustomerReceipt, "2027-06-12", 50_000, iStop);
V(VoucherKind.Expense, VoucherNature.SupplierPayment, "2027-06-26", 60_000, c);
MasterDataService.SavePartner(new Partner { Id = iStop, Code = "G-STOP", Name = "停用客户I", Kind = "customer", IsActive = false });

Reject("red quota rejects over original", () => I(InvoiceKind.Out, "OUT-K-OVER", kRed, "2027-06-17", -339_000, -300_000, -39_000, original: kPre1));
Reject("red date cannot predate original", () => I(InvoiceKind.Out, "OUT-K-EARLY", kRed, "2027-05-19", -113_000, -100_000, -13_000, original: kPre1));

var junOut = Run(InvoiceKind.Out, "2027-06-01", "2027-06-30");
Check("jun out rows and order", junOut.Rows.Select(x => (string?)x.Cells[0])
    .SequenceEqual(new[] { "00123", "G-BOTH2", "G-CASH", "G-INV", "G-NAT", "G-RED", "G-STOP" }));
RowEq(junOut, "00123", 1, 0, 1, 11_300, 10_000, 1_300, 0, 11_300);
RowEq(junOut, "G-BOTH2", 1, 0, 1, 339_000, 300_000, 39_000, 100_000, 239_000);
RowEq(junOut, "G-CASH", 0, 0, 0, 0, 0, 0, 250_000, -250_000);
RowEq(junOut, "G-INV", 1, 0, 1, 226_000, 200_000, 26_000, 0, 226_000);
RowEq(junOut, "G-NAT", 0, 0, 0, 0, 0, 0, 150_000, -150_000);
RowEq(junOut, "G-RED", 1, 2, -1, -904_000, -800_000, -104_000, 0, -904_000);
RowEq(junOut, "G-STOP", 1, 0, 1, 113_000, 100_000, 13_000, 50_000, 63_000);
Eq("jun out total blue", C(junOut.TotalRow, 2), 5);
Eq("jun out total red", C(junOut.TotalRow, 3), 2);
Eq("jun out total net count", C(junOut.TotalRow, 4), 3);
Eq("jun out total gross", C(junOut.TotalRow, 5), -214_700);
Eq("jun out total net", C(junOut.TotalRow, 6), -190_000);
Eq("jun out total tax", C(junOut.TotalRow, 7), -24_700);
Eq("jun out total cash excludes null partner", C(junOut.TotalRow, 8), 550_000);
Eq("jun out total difference", C(junOut.TotalRow, 9), -764_700);
Invariants(junOut);

var junIn = Run(InvoiceKind.In, "2027-06-01", "2027-06-30");
Check("jun in rows and order", junIn.Rows.Select(x => (string?)x.Cells[0])
    .SequenceEqual(new[] { "G-BOTH2", "G-NAT", "GY-C" }));
RowEq(junIn, "G-BOTH2", 1, 0, 1, 226_000, 200_000, 26_000, 80_000, 146_000);
RowEq(junIn, "G-NAT", 0, 0, 0, 0, 0, 0, 165_000, -165_000);
RowEq(junIn, "GY-C", 1, 0, 1, 56_500, 50_000, 6_500, 60_000, -3_500);
Eq("jun in total blue", C(junIn.TotalRow, 2), 2);
Eq("jun in total gross", C(junIn.TotalRow, 5), 282_500);
Eq("jun in total cash excludes null partner", C(junIn.TotalRow, 8), 305_000);
Eq("jun in total difference", C(junIn.TotalRow, 9), -22_500);
Check("jun in excludes customers", junIn.Rows.All(x => (string?)x.Cells[0] is not ("G-CASH" or "G-INV" or "G-STOP" or "00123" or "G-RED")));
Invariants(junIn);

var mayOut = Run(InvoiceKind.Out, "2027-05-01", "2027-05-31");
Check("may out only prior blue", mayOut.Rows.Count == 1 && (string?)mayOut.Rows[0].Cells[0] == "G-RED");
RowEq(mayOut, "G-RED", 2, 0, 2, 1_356_000, 1_200_000, 156_000, 0, 1_356_000);
Eq("may out total gross", C(mayOut.TotalRow, 5), 1_356_000);
Invariants(mayOut);

Check("red count can exceed blue count", C(Row(junOut, "G-RED"), 3) > C(Row(junOut, "G-RED"), 2) && C(Row(junOut, "G-RED"), 4) == -1);
Check("all inflow natures counted", C(Row(junOut, "G-NAT"), 8) == 150_000);
Check("all outflow natures counted", C(Row(junIn, "G-NAT"), 8) == 165_000);
Check("pending draft void receivable and transfer excluded", C(Row(junOut, "G-CASH"), 8) == 250_000);
Check("null partner cash absent from out rows", junOut.Rows.All(x => !string.IsNullOrWhiteSpace((string?)x.Cells[0])));
Check("null partner cash absent from in rows", junIn.Rows.All(x => !string.IsNullOrWhiteSpace((string?)x.Cells[0])));

var stopFilter = Run(InvoiceKind.Out, "2027-06-01", "2027-06-30", iStop);
Check("inactive partner still reported", stopFilter.Rows.Count == 1 && C(stopFilter.Rows[0], 8) == 50_000);
Check("inactive partner available in filter list", MasterDataService.ListPartners("customer", true).Any(x => x.Id == iStop));
Check("inactive partner hidden from active list", MasterDataService.ListPartners("customer", false).All(x => x.Id != iStop));
var cashFilter = Run(InvoiceKind.Out, "2027-06-01", "2027-06-30", gCash);
Check("cash-only partner filter", cashFilter.Rows.Count == 1 && C(cashFilter.Rows[0], 5) == 0 && C(cashFilter.TotalRow, 8) == 250_000);
var invFilter = Run(InvoiceKind.Out, "2027-06-01", "2027-06-30", hInv);
Check("invoice-only partner filter", invFilter.Rows.Count == 1 && C(invFilter.Rows[0], 8) == 0 && C(invFilter.Rows[0], 9) == 226_000);
Check("out report ignores supplier-only filter", Run(InvoiceKind.Out, "2027-06-01", "2027-06-30", c).Rows.Count == 0);
Check("in report ignores customer-only filter", Run(InvoiceKind.In, "2027-06-01", "2027-06-30", gCash).Rows.Count == 0);
Check("both partner does not mix directions", C(Row(junOut, "G-BOTH2"), 5) == 339_000 && C(Row(junIn, "G-BOTH2"), 5) == 226_000);

var detailCashOnly = reports.GetPeriodInvoiceCashDetail(gCash, "2027-06-01", "2027-06-30", InvoiceKind.Out);
Check("cash-only detail counts", detailCashOnly.Invoices.Count == 0 && detailCashOnly.BlueCount == 0 && detailCashOnly.RedCount == 0 && detailCashOnly.NetCount == 0);
Check("cash-only detail vouchers", detailCashOnly.Vouchers.Count == 1 && detailCashOnly.CashCents == 250_000 && detailCashOnly.DifferenceCents == -250_000);
var cashOnlyParts = reports.BuildPeriodInvoiceCashDetailReports(detailCashOnly);
Check("cash-only invoice sheet empty", cashOnlyParts[0].Rows.Count == 0 && (string?)cashOnlyParts[0].TotalRow![1] == "0 张（蓝 0 / 红 0）" && C(cashOnlyParts[0].TotalRow, 3) == 0);
Check("cash-only cash sheet total", cashOnlyParts[1].Rows.Count == 1 && (string?)cashOnlyParts[1].TotalRow![1] == "1 笔" && C(cashOnlyParts[1].TotalRow, 3) == 250_000);

var detailRed = reports.GetPeriodInvoiceCashDetail(kRed, "2027-06-01", "2027-06-30", InvoiceKind.Out);
Check("red detail counts", detailRed.Invoices.Count == 3 && detailRed.BlueCount == 1 && detailRed.RedCount == 2 && detailRed.NetCount == -1);
Check("red detail totals", detailRed.GrossCents == -904_000 && detailRed.NetCents == -800_000 && detailRed.TaxCents == -104_000);
Check("red detail difference", detailRed.Vouchers.Count == 0 && detailRed.DifferenceCents == -904_000);
Check("red detail order", detailRed.Invoices.Select(x => x.InvoiceNo).SequenceEqual(new[] { "OUT-K-R1", "OUT-K-3", "OUT-K-R2" }));
var redParts = reports.BuildPeriodInvoiceCashDetailReports(detailRed);
Check("red detail report label", (string?)redParts[0].TotalRow![1] == "3 张（蓝 1 / 红 2）");
Check("red detail report totals", C(redParts[0].TotalRow, 3) == -904_000 && C(redParts[0].TotalRow, 4) == -800_000 && C(redParts[0].TotalRow, 5) == -104_000);
Check("red detail cash report empty", redParts[1].Rows.Count == 0 && (string?)redParts[1].TotalRow![1] == "0 笔" && C(redParts[1].TotalRow, 3) == 0);

var detailInBoth = reports.GetPeriodInvoiceCashDetail(jBoth, "2027-06-01", "2027-06-30", InvoiceKind.In);
Check("in detail both partner", detailInBoth.Invoices.Count == 1 && detailInBoth.Vouchers.Count == 1
    && detailInBoth.GrossCents == 226_000 && detailInBoth.CashCents == 80_000 && detailInBoth.DifferenceCents == 146_000);
var inBothParts = reports.BuildPeriodInvoiceCashDetailReports(detailInBoth);
Check("in detail both titles", inBothParts[0].Title == "进项发票明细" && inBothParts[1].Title == "付款明细");
Check("in detail both totals", (string?)inBothParts[0].TotalRow![1] == "1 张（蓝 1 / 红 0）" && (string?)inBothParts[1].TotalRow![1] == "1 笔");
var detailInvOnly = reports.GetPeriodInvoiceCashDetail(hInv, "2027-06-01", "2027-06-30", InvoiceKind.Out);
Check("invoice-only detail has no cash", detailInvOnly.Vouchers.Count == 0 && detailInvOnly.CashCents == 0 && detailInvOnly.DifferenceCents == 226_000);
var invOnlyParts = reports.BuildPeriodInvoiceCashDetailReports(detailInvOnly);
Check("invoice-only cash sheet label", (string?)invOnlyParts[1].TotalRow![1] == "0 笔");

PeriodService.Close("202706");
var junClosed = Run(InvoiceKind.Out, "2027-06-01", "2027-06-30");
Check("closed 2027-06 still readable", junClosed.Rows.Count == 7 && C(junClosed.TotalRow, 5) == -214_700 && junClosed.Note.Contains("关账"));
PeriodService.Reopen("202706", "深度测试完成");

var junPath = Path.Combine(args[0], "deep-jun.xlsx");
ExcelService.ExportReport(junOut, junPath);
using (var wb = new XLWorkbook(junPath))
{
    var ws = wb.Worksheet(1);
    Check("deep excel title and period", ws.Cell(1, 1).GetString() == "期间开票与收款统计"
        && ws.Cell(2, 1).GetString().Contains("2027-06-01") && ws.Cell(2, 1).GetString().Contains("2027-06-30"));
    Check("deep excel header", ws.Cell(5, 1).GetString() == "单位编码" && ws.Cell(5, 10).GetString().StartsWith("票款差额", StringComparison.Ordinal));
    Check("deep excel leading zero text", ws.Cell(6, 1).DataType == XLDataType.Text && ws.Cell(6, 1).GetString() == "00123");
    Check("deep excel first row numeric", ws.Cell(6, 3).DataType == XLDataType.Number && ws.Cell(6, 3).GetDouble() == 1d && ws.Cell(6, 6).GetDouble() == 113d);
    Check("deep excel red row", ws.Cell(11, 3).GetDouble() == 1d && ws.Cell(11, 4).GetDouble() == 2d && ws.Cell(11, 5).GetDouble() == -1d);
    Check("deep excel red money", ws.Cell(11, 6).GetDouble() == -9040d && ws.Cell(11, 7).GetDouble() == -8000d
        && ws.Cell(11, 8).GetDouble() == -1040d && ws.Cell(11, 10).GetDouble() == -9040d);
    Check("deep excel negative number format", ws.Cell(11, 6).Style.NumberFormat.Format.Contains("[Red]") && ws.Cell(11, 6).DataType == XLDataType.Number);
    Check("deep excel total row", ws.Cell(13, 3).GetDouble() == 5d && ws.Cell(13, 4).GetDouble() == 2d && ws.Cell(13, 5).GetDouble() == 3d
        && ws.Cell(13, 6).GetDouble() == -2147d && ws.Cell(13, 7).GetDouble() == -1900d && ws.Cell(13, 8).GetDouble() == -247d
        && ws.Cell(13, 9).GetDouble() == 5500d && ws.Cell(13, 10).GetDouble() == -7647d);
    Check("deep excel note row", ws.Cell(15, 1).GetString().StartsWith("备注：", StringComparison.Ordinal) && ws.Cell(15, 1).GetString().Contains("红字发票"));
}

var redDetailPath = Path.Combine(args[0], "deep-red-detail.xlsx");
ExcelService.ExportReports(redDetailPath, redParts.ToArray());
using (var wb = new XLWorkbook(redDetailPath))
{
    var invSheet = wb.Worksheet(1);
    var cashSheet = wb.Worksheet(2);
    Check("deep detail sheets", wb.Worksheets.Select(x => x.Name).SequenceEqual(new[] { "销项发票明细", "收款明细" }));
    Check("deep detail invoice title and period", invSheet.Cell(1, 1).GetString() == "销项发票明细"
        && invSheet.Cell(2, 1).GetString().Contains("红冲客户K") && invSheet.Cell(2, 1).GetString().Contains("2027-06-01"));
    Check("deep detail invoice rows", invSheet.Cell(9, 2).GetString() == "3 张（蓝 1 / 红 2）"
        && invSheet.Cell(9, 4).GetDouble() == -9040d && invSheet.Cell(9, 5).GetDouble() == -8000d && invSheet.Cell(9, 6).GetDouble() == -1040d);
    Check("deep detail negative invoice cell", invSheet.Cell(6, 4).GetDouble() == -9040d && invSheet.Cell(6, 4).DataType == XLDataType.Number);
    Check("deep detail cash empty total", cashSheet.Cell(6, 2).GetString() == "0 笔" && cashSheet.Cell(6, 4).GetDouble() == 0d);
}

Check("deep money formatting", Money.Format(-904_000) == "-9,040.00" && Money.Format(1_356_000) == "13,560.00" && Money.Format(0) == "0.00");

Console.WriteLine($"RESULT checks={checks} failures={failures}");
return failures == 0 ? 0 : 1;

static class PerfHarness
{
    public static int Run(string dir)
    {
        if (Directory.Exists(dir)) throw new Exception("New isolated directory required");
        var total = Stopwatch.StartNew();
        AppSession.Db = new Database(dir);
        AppSession.Db.EnsureCreated();
        AppSession.Current = new AuthService().Login("admin", "admin888");
        var checks = 0;
        var failures = 0;
        void Check(string name, bool ok)
        {
            checks++;
            if (ok) return;
            failures++;
            Console.WriteLine("FAIL | " + name);
        }

        using var conn = AppSession.Db.Open();
        var partnerIds = new List<long>();
        var accountId = new SqlCmd(conn, "SELECT id FROM accounts ORDER BY id LIMIT 1").Long();
        using (var tx = conn.BeginTransaction())
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO partners(code,name,kind,is_active) VALUES($c,$n,'customer',1)";
                var pc = cmd.CreateParameter(); pc.ParameterName = "$c"; cmd.Parameters.Add(pc);
                var pn = cmd.CreateParameter(); pn.ParameterName = "$n"; cmd.Parameters.Add(pn);
                for (var i = 0; i < 1000; i++)
                {
                    pc.Value = "PERF-" + i.ToString("0000");
                    pn.Value = "性能客户" + i.ToString("0000");
                    cmd.ExecuteNonQuery();
                    partnerIds.Add(new SqlCmd(conn, "SELECT last_insert_rowid()", tx).Long());
                }
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO invoices(kind,invoice_type,invoice_no,partner_id,reg_date,gross_cents,net_cents,tax_cents,rate_bp,status,created_at,created_by,sign,original_invoice_id)
                    VALUES('out','增值税专用发票',$no,$p,$d,113000,100000,13000,1300,'valid',$at,'perf',1,NULL)";
                var no = cmd.CreateParameter(); no.ParameterName = "$no"; cmd.Parameters.Add(no);
                var pid = cmd.CreateParameter(); pid.ParameterName = "$p"; cmd.Parameters.Add(pid);
                var date = cmd.CreateParameter(); date.ParameterName = "$d"; cmd.Parameters.Add(date);
                var at = cmd.CreateParameter(); at.ParameterName = "$at"; cmd.Parameters.Add(at);
                for (var i = 0; i < 50_000; i++)
                {
                    no.Value = "PERF-INV-" + i.ToString("000000");
                    pid.Value = partnerIds[i % partnerIds.Count];
                    date.Value = new DateTime(2027, 1, 1).AddDays(i % 365).ToString("yyyy-MM-dd");
                    at.Value = "2027-01-01 00:00:00";
                    cmd.ExecuteNonQuery();
                }
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO vouchers(doc_no,kind,nature,biz_date,period,amount_cents,account_id,partner_id,summary,status,created_at,created_by)
                    VALUES($no,'income','CustomerReceipt',$d,$per,50000,$acc,$p,'性能回款','audited',$at,'perf')";
                var no = cmd.CreateParameter(); no.ParameterName = "$no"; cmd.Parameters.Add(no);
                var date = cmd.CreateParameter(); date.ParameterName = "$d"; cmd.Parameters.Add(date);
                var per = cmd.CreateParameter(); per.ParameterName = "$per"; cmd.Parameters.Add(per);
                var acc = cmd.CreateParameter(); acc.ParameterName = "$acc"; cmd.Parameters.Add(acc);
                var pid = cmd.CreateParameter(); pid.ParameterName = "$p"; cmd.Parameters.Add(pid);
                var at = cmd.CreateParameter(); at.ParameterName = "$at"; cmd.Parameters.Add(at);
                acc.Value = accountId;
                for (var i = 0; i < 50_000; i++)
                {
                    var day = new DateTime(2027, 1, 1).AddDays(i % 365);
                    no.Value = "PERF-V-" + i.ToString("000000");
                    date.Value = day.ToString("yyyy-MM-dd");
                    per.Value = day.ToString("yyyy-MM");
                    pid.Value = partnerIds[i % partnerIds.Count];
                    at.Value = "2027-01-01 00:00:00";
                    cmd.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
        var seedMs = total.ElapsedMilliseconds;

        var service = new ReportService();
        var filter = new ReportFilter { From = "2027-01-01", To = "2027-12-31", InvoiceDirection = InvoiceKind.Out };
        var timings = new List<long>();
        ReportResult? result = null;
        for (var run = 0; run < 3; run++)
        {
            var sw = Stopwatch.StartNew();
            result = service.Build("period_invoice_cash", filter);
            sw.Stop();
            timings.Add(sw.ElapsedMilliseconds);
        }
        Check("perf rows", result!.Rows.Count == 1000);
        Check("perf invoice total", Convert.ToInt64(result.TotalRow![5]) == 50_000L * 113_000L);
        Check("perf cash total", Convert.ToInt64(result.TotalRow[8]) == 50_000L * 50_000L);
        Check("perf gross rule", Convert.ToInt64(result.TotalRow[5]) == Convert.ToInt64(result.TotalRow[6]) + Convert.ToInt64(result.TotalRow[7]));
        Check("perf warm query under 5s", timings.Skip(1).Min() < 5000);
        Check("perf current schema", AppSession.Db.GetUserVersion() == Database.CurrentVersion);
        Console.WriteLine($"PERF seed_ms={seedMs} query_ms={string.Join(",", timings)} rows={result.Rows.Count} total_ms={total.ElapsedMilliseconds}");
        Console.WriteLine($"RESULT checks={checks} failures={failures}");
        return failures == 0 ? 0 : 1;
    }
}

