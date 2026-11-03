using Cashier.Core;
using Cashier.Core.Services;
using Microsoft.Data.Sqlite;

if (args.Length != 1 || Directory.Exists(args[0])) throw new Exception("Fresh isolated directory required");
AppSession.Db = new Database(args[0]); AppSession.Db.EnsureCreated();
AppSession.Current = new AuthService().Login("admin", "admin888");
var svc = new VoucherService(); var settlement = new SettlementService();
var date = AppSession.Today(); var bank = MasterDataService.ListAccounts(false).First().Id;
int checks = 0, failures = 0;
void Check(string name, bool ok) { checks++; Console.WriteLine($"{(ok ? "PASS" : "FAIL")} | {name}"); if (!ok) failures++; }
void Reject(string name, Action action) { try { action(); Check(name, false); } catch (AppException) { Check(name, true); } }
foreach (var isCustomer in new[] { true, false })
{
    var partner = MasterDataService.SavePartner(new Partner { Name = "测试往来" + isCustomer, Kind = isCustomer ? "customer" : "supplier" });
    var biz = svc.Create(new Voucher { Kind = isCustomer ? VoucherKind.Receivable : VoucherKind.Payable, PartnerId = partner, BizDate = date, AmountCents = 1000000, Summary = "业务" });
    Check("pending business has no settlement status", svc.Get(biz)!.SettlementStatusText == "—");
    svc.Audit(biz);
    Check("audited business is itself the receivable/payable record", svc.Get(biz)!.SettlementStatusText == "未结清" && svc.Get(biz)!.AmountCents == 1000000);
    var pay = svc.Create(new Voucher { Kind = isCustomer ? VoucherKind.Income : VoucherKind.Expense, Nature = isCustomer ? VoucherNature.CustomerReceipt : VoucherNature.SupplierPayment, PartnerId = partner, AccountId = bank, BizDate = date, AmountCents = 1000000, Summary = "收付款" });
    svc.Audit(pay);
    Check("unallocated payment does not invent a business association", svc.Get(biz)!.SettlementStatusText == "未结清");
    settlement.Allocate(pay, biz, 300000);
    Check("partial allocation calculates exact status and remainder", svc.Get(biz) is { SettledCents: 300000, UnsettledCents: 700000, SettlementStatusText: "部分结清" });
    Reject("cannot reverse a still-linked business", () => svc.Unaudit(biz, "测试"));
    Reject("cannot void a still-linked payment", () => svc.Void(pay, "测试"));
    settlement.AutoAllocate(pay);
    Check("additional explicit allocation updates the status", svc.Get(biz) is { SettledCents: 1000000, UnsettledCents: 0, SettlementStatusText: "已结清" });
    var allocation = settlement.ListByBiz(biz).Single();
    settlement.Unallocate(allocation.Id, "测试解除");
    Check("undo allocation recalculates the status", svc.Get(biz) is { SettledCents: 0, UnsettledCents: 1000000, SettlementStatusText: "未结清" });
    using (var conn = AppSession.Db.Open())
        Check("undo retains historical allocation evidence", new SqlCmd(conn, "SELECT COUNT(*) FROM settlement_history WHERE biz_voucher_id=$i").P("$i", biz).Long() >= 1);
    svc.Unaudit(biz, "测试反审核");
    Check("reversed business is not shown as settled", svc.Get(biz)!.SettlementStatusText == "—");
    var edit = svc.Get(biz)!; edit.AmountCents = 1200000; svc.Update(edit); svc.Audit(biz);
    Check("edited and reaudited amount is used", svc.Get(biz) is { AmountCents: 1200000, UnsettledCents: 1200000, SettlementStatusText: "未结清" });
    svc.Unaudit(biz, "测试删除"); svc.DeleteDraft(biz);
    Check("unlinked draft can be deleted", svc.Get(biz) == null);
    svc.Void(pay, "测试作废");
    Check("voided payment is excluded from active operations", svc.Get(pay)!.Status == VoucherStatus.Void);
}
var invoicePartner = MasterDataService.SavePartner(new Partner { Name = "先开票客户", Kind = "customer" });
var invoiceService = new InvoiceService();
var invoice = invoiceService.Save(new Invoice { AutoLink = true, Kind = InvoiceKind.Out, InvoiceType = "普通", InvoiceNo = "FIRST", PartnerId = invoicePartner, RegDate = date, GrossCents = 50000, NetCents = 50000, TaxCents = 0, RateBp = 0 });
var lateBiz = svc.Create(new Voucher { Kind = VoucherKind.Receivable, PartnerId = invoicePartner, BizDate = date, AmountCents = 100000, Summary = "发票先于业务" });
using (var conn = AppSession.Db.Open()) new SqlCmd(conn, "CREATE TRIGGER test_link_failure BEFORE INSERT ON invoice_links BEGIN SELECT RAISE(ABORT,'link failure'); END").Exec();
try { svc.Audit(lateBiz); Check("invoice linking error fails audit", false); } catch (SqliteException) { Check("invoice linking error fails audit", true); }
Check("failed automatic invoice link rolls back voucher audit", svc.Get(lateBiz)!.Status == VoucherStatus.Pending && invoiceService.Links(invoice).Count == 0);
using (var conn = AppSession.Db.Open()) new SqlCmd(conn, "DROP TRIGGER test_link_failure").Exec();
svc.Audit(lateBiz);
Check("auditing business links a pre-existing invoice", invoiceService.Links(invoice).Single() is var link && link.voucherId == lateBiz && link.amount == 50000);
AppSession.Db = new Database(args[0]); AppSession.Db.EnsureCreated();
using (var conn = AppSession.Db.Open())
    Check("reopen retains settlement history and valid foreign keys", new SqlCmd(conn, "SELECT COUNT(*) FROM settlement_history").Long() >= 2 && new SqlCmd(conn, "SELECT COUNT(*) FROM pragma_foreign_key_check").Long() == 0);
Console.WriteLine($"RESULT checks={checks} failures={failures}");
return failures == 0 ? 0 : 1;
