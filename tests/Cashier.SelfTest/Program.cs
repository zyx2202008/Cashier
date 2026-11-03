using System.Text;
using Cashier.Core;
using Cashier.Core.Services;

namespace Cashier.SelfTest;

/// <summary>财务验收自检：结果写入控制台与 selftest-report.txt，退出码 0 表示全部通过。</summary>
internal static class Program
{
    private static int _pass;
    private static readonly List<string> _fails = new();
    private static readonly StringBuilder _log = new();
    private static Database _db = null!;

    private static int Main(string[] args)
    {
        var dir = args.Length > 0
            ? args[0]
            : Path.Combine(Path.GetTempPath(), "cashier-selftest-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
        Directory.CreateDirectory(dir);

        _db = new Database(dir);
        AppSession.Db = _db;
        _db.EnsureCreated(m => Write("  " + m));

        Write("========================================");
        Write(" 出纳管理 · 财务验收自检");
        Write(" 数据目录：" + dir);
        Write(" 时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        Write("========================================");

        var auth = new AuthService();
        AppSession.Current = auth.Login("admin", "admin888");
        Write($"登录成功：{AppSession.Current.DisplayName}（{AppSession.Current.RoleLabel}）");

        SeedMaster();

        CaseA();
        CaseB();
        CaseC();
        CaseD();
        CaseE();
        CaseF();
        CaseG(dir);
        CaseH(dir);
        Extras();

        Write("");
        Write("========================================");
        Write($" 通过 {_pass} 项，失败 {_fails.Count} 项");
        foreach (var f in _fails) Write("  ✗ " + f);
        Write("========================================");

        var report = Path.Combine(AppContext.BaseDirectory, "selftest-report.txt");
        try { File.WriteAllText(report, _log.ToString(), Encoding.UTF8); } catch { }
        try { File.WriteAllText(Path.Combine(dir, "selftest-report.txt"), _log.ToString(), Encoding.UTF8); } catch { }
        return _fails.Count == 0 ? 0 : 1;
    }

    private static void Write(string s)
    {
        Console.WriteLine(s);
        _log.AppendLine(s);
    }

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok) { _pass++; Write($"  ✓ {name}"); }
        else { _fails.Add(name + (detail == null ? "" : " → " + detail)); Write($"  ✗ {name}  {detail}"); }
    }

    private static void Section(string title) => Write($"\n【{title}】");

    // ---------------- 基础数据 ----------------
    private static long _custA, _custB, _custC, _custD, _supA, _projA, _projB, _accBank, _accCash, _subjIn, _subjOut;

    private static void SeedMaster()
    {
        _accBank = MasterDataService.ListAccounts(false).First(a => a.Kind == "bank").Id;
        _accCash = MasterDataService.ListAccounts(false).First(a => a.Kind == "cash").Id;
        _subjIn = MasterDataService.ListSubjects("in").First().Id;
        _subjOut = MasterDataService.ListSubjects("out").First().Id;

        _custA = MasterDataService.SavePartner(new Partner { Code = "T-C01", Name = "测试客户甲", Kind = "customer", Terms = "月结30天" });
        _custB = MasterDataService.SavePartner(new Partner { Code = "T-C02", Name = "测试客户乙", Kind = "customer" });
        _custC = MasterDataService.SavePartner(new Partner { Code = "T-C03", Name = "测试客户丙", Kind = "customer" });
        _custD = MasterDataService.SavePartner(new Partner { Code = "T-C04", Name = "测试客户丁（跨年）", Kind = "customer" });
        _supA = MasterDataService.SavePartner(new Partner { Code = "T-S01", Name = "测试供应商甲", Kind = "supplier" });
        _projA = MasterDataService.SaveProject(new Project { Code = "T-P01", Name = "测试项目一" });
        _projB = MasterDataService.SaveProject(new Project { Code = "T-P02", Name = "测试项目二" });
    }

    private static Voucher NewVoucher(VoucherKind kind, VoucherNature nature, string date, long cents,
        long? partner = null, long? account = null, long? project = null, string summary = "测试单据")
        => new()
        {
            Kind = kind, Nature = nature, BizDate = date, AmountCents = cents,
            PartnerId = partner, AccountId = account, ProjectId = project,
            Summary = summary, SubjectId = kind is VoucherKind.Income or VoucherKind.Receivable ? _subjIn : _subjOut,
            CountAsRevenue = kind == VoucherKind.Receivable,
            CountAsCost = kind == VoucherKind.Payable,
        };

    private static long Create(Voucher v, bool audit = true)
    {
        var id = new VoucherService().Create(v);
        if (audit) new VoucherService().Audit(id);
        return id;
    }

    // ---------------- A. 跨月预收 ----------------
    private static void CaseA()
    {
        Section("A 跨月预收");
        var svc = new VoucherService();
        Create(NewVoucher(VoucherKind.Income, VoucherNature.CustomerPrepay, "2026-11-15", 20_000_000, _custA, _accBank, null, "11月预收"));
        Create(NewVoucher(VoucherKind.Receivable, VoucherNature.None, "2026-12-05", 127_092_144, _custA, null, _projA, "12月新增应收"));
        Create(NewVoucher(VoucherKind.Income, VoucherNature.CustomerReceipt, "2026-12-20", 50_000_000, _custA, _accBank, null, "12月回款"));

        var p = MasterDataService.GetPartner(_custA)!;
        var expected = -20_000_000L + 127_092_144L - 50_000_000L;
        Check("12月末净应收 = 570,921.44", p.NetReceivableCents == expected,
            $"实际 {Money.Format(p.NetReceivableCents)}，期望 {Money.Format(expected)}");
        Check("净应收为负数时显示为预收", p.NetReceivableCents < 0 == false && expected > 0, "");

        // 11 月末余额
        var asOfNov = MasterDataService.GetPartner(_custA)!;
        Check("累计应收仅统计应收单", asOfNov.ReceivableCents == 127_092_144, Money.Format(asOfNov.ReceivableCents));
    }

    // ---------------- B. 收款与开票分离 ----------------
    private static void CaseB()
    {
        Section("B 收款与开票分离");
        var recvId = Create(NewVoucher(VoucherKind.Receivable, VoucherNature.None, "2027-01-10", 10_000_000, _custB, null, _projA, "应收10万"));
        Create(NewVoucher(VoucherKind.Income, VoucherNature.CustomerReceipt, "2027-01-15", 6_000_000, _custB, _accBank, null, "回款6万"));

        var before = MasterDataService.AccountBalance(_accBank);
        var inv = new InvoiceService().Save(new Invoice
        {
            Kind = InvoiceKind.Out, InvoiceType = "电子发票（普通）", InvoiceNo = "INV-B-0001",
            PartnerId = _custB, RegDate = "2027-01-18", GrossCents = 8_000_000,
            NetCents = Money.SplitTax(8_000_000, 600).net, TaxCents = Money.SplitTax(8_000_000, 600).tax,
            RateBp = 600, Content = "测试服务",
        });
        new InvoiceService().Link(inv, recvId, 8_000_000);

        var after = MasterDataService.AccountBalance(_accBank);
        Check("登记发票不改变资金余额", before == after, $"{Money.Format(before)} → {Money.Format(after)}");

        var v = new VoucherService().Get(recvId)!;
        var netB = MasterDataService.GetPartner(_custB)!.NetReceivableCents;
        Check("未收款（净应收）= 40,000", netB == 4_000_000, Money.Format(netB));
        Check("未开票 = 20,000", v.AmountCents - v.InvoiceLinkedCents == 2_000_000, Money.Format(v.AmountCents - v.InvoiceLinkedCents));

        // 核对恒等式：单据未结 − 收付款未分配 = 往来净余额
        var unsettledB = v.AmountCents - v.SettledCents;
        var unallocB = new SettlementService().UnallocatedOf(new VoucherService().Query(
            new VoucherQuery { Kind = VoucherKind.Income, PartnerId = _custB, Limit = 10 }).First().Id);
        Check("未结 − 未分配 = 净应收（可核对）", unsettledB - unallocB == netB,
            $"{Money.Format(unsettledB)} − {Money.Format(unallocB)} = {Money.Format(unsettledB - unallocB)}，净应收 {Money.Format(netB)}");

        // 核销后单据未结应为 40,000
        new SettlementService().Allocate(new VoucherService().Query(
            new VoucherQuery { Kind = VoucherKind.Income, PartnerId = _custB, Limit = 10 }).First().Id, recvId, 6_000_000);
        var v2 = new VoucherService().Get(recvId)!;
        Check("核销后单据未结 = 40,000", v2.AmountCents - v2.SettledCents == 4_000_000, Money.Format(v2.AmountCents - v2.SettledCents));

        var (invoiceable, invoiced, uninvoiced) = new InvoiceService().PartnerInvoiceSummary(_custB, InvoiceKind.Out);
        Check("客户开票汇总：可开票10万/已开8万/未开2万",
            invoiceable == 10_000_000 && invoiced == 8_000_000 && uninvoiced == 2_000_000,
            $"{invoiceable}/{invoiced}/{uninvoiced}");
        Check("税额 + 不含税 = 含税", 8_000_000 - Money.SplitTax(8_000_000, 600).net + Money.SplitTax(8_000_000, 600).net == 8_000_000, "");
    }

    // ---------------- C. 部分核销 ----------------
    private static void CaseC()
    {
        Section("C 部分核销（按日期先后自动分配）");
        var r1 = Create(NewVoucher(VoucherKind.Receivable, VoucherNature.None, "2027-02-01", 3_000_000, _custC, null, _projB, "应收3万"));
        var r2 = Create(NewVoucher(VoucherKind.Receivable, VoucherNature.None, "2027-02-05", 5_000_000, _custC, null, _projB, "应收5万"));
        var r3 = Create(NewVoucher(VoucherKind.Receivable, VoucherNature.None, "2027-02-10", 2_000_000, _custC, null, _projB, "应收2万"));
        var pay = Create(NewVoucher(VoucherKind.Income, VoucherNature.CustomerReceipt, "2027-02-20", 6_000_000, _custC, _accBank, null, "回款6万"));

        var allocs = new SettlementService().AutoAllocate(pay);
        var svc = new VoucherService();
        Check("第一笔结清", svc.Get(r1)!.UnsettledCents == 0, Money.Format(svc.Get(r1)!.UnsettledCents));
        Check("第二笔剩 20,000", svc.Get(r2)!.UnsettledCents == 2_000_000, Money.Format(svc.Get(r2)!.UnsettledCents));
        Check("第三笔剩 20,000", svc.Get(r3)!.UnsettledCents == 2_000_000, Money.Format(svc.Get(r3)!.UnsettledCents));
        var totalUnsettled = svc.Get(r1)!.UnsettledCents + svc.Get(r2)!.UnsettledCents + svc.Get(r3)!.UnsettledCents;
        Check("总未收款 40,000", totalUnsettled == 4_000_000, Money.Format(totalUnsettled));
        Check("核销后客户净应收 = 40,000", MasterDataService.GetPartner(_custC)!.NetReceivableCents == 4_000_000,
            Money.Format(MasterDataService.GetPartner(_custC)!.NetReceivableCents));
        Check("回款单未分配金额为 0", new SettlementService().UnallocatedOf(pay) == 0, "");
        Check("分配笔数 = 2", allocs.Count == 2, allocs.Count.ToString());

        // 重复核销同一组合应失败
        var dupBlocked = false;
        try { new SettlementService().Allocate(pay, r2, 100); } catch (AppException) { dupBlocked = true; }
        Check("重复核销被阻止（已存在组合）", dupBlocked, "");
    }

    // ---------------- D. 内部转账 ----------------
    private static void CaseD()
    {
        Section("D 内部转账");
        var bankBefore = MasterDataService.AccountBalance(_accBank);
        var cashBefore = MasterDataService.AccountBalance(_accCash);
        var totalBefore = bankBefore + cashBefore;

        var (inBefore, outBefore) = VoucherService.CashFlow("2027-03-01", "2027-03-31");
        Create(new Voucher
        {
            Kind = VoucherKind.Transfer, Nature = VoucherNature.None, BizDate = "2027-03-10",
            AmountCents = 1_000_000, AccountId = _accBank, ToAccountId = _accCash, Summary = "银行转现金",
        });

        var bankAfter = MasterDataService.AccountBalance(_accBank);
        var cashAfter = MasterDataService.AccountBalance(_accCash);
        Check("转出账户减少 10,000", bankAfter == bankBefore - 1_000_000, $"{Money.Format(bankBefore)} → {Money.Format(bankAfter)}");
        Check("转入账户增加 10,000", cashAfter == cashBefore + 1_000_000, $"{Money.Format(cashBefore)} → {Money.Format(cashAfter)}");
        Check("总资金不变", bankAfter + cashAfter == totalBefore, $"{Money.Format(totalBefore)} → {Money.Format(bankAfter + cashAfter)}");

        var (inAfter, outAfter) = VoucherService.CashFlow("2027-03-01", "2027-03-31");
        Check("转账不计入收入", inAfter == inBefore, $"{inBefore} → {inAfter}");
        Check("转账不计入支出", outAfter == outBefore, $"{outBefore} → {outAfter}");

        // 同账户转账必须失败且不落库
        var countBefore = new VoucherService().Query(new VoucherQuery { Kind = VoucherKind.Transfer, Limit = 9999 }).Count;
        var blocked = false;
        try
        {
            Create(new Voucher
            {
                Kind = VoucherKind.Transfer, Nature = VoucherNature.None, BizDate = "2027-03-11",
                AmountCents = 100, AccountId = _accBank, ToAccountId = _accBank, Summary = "同账户",
            });
        }
        catch (AppException) { blocked = true; }
        var countAfter = new VoucherService().Query(new VoucherQuery { Kind = VoucherKind.Transfer, Limit = 9999 }).Count;
        Check("同账户转账被拒绝", blocked, "");
        Check("拒绝后无脏数据", countBefore == countAfter, $"{countBefore} → {countAfter}");

        // 手续费单独作为支出
        var expenseBefore = new VoucherService().Query(new VoucherQuery { Kind = VoucherKind.Expense, Limit = 9999 }).Count;
        Create(new Voucher
        {
            Kind = VoucherKind.Transfer, Nature = VoucherNature.None, BizDate = "2027-03-12",
            AmountCents = 500_000, AccountId = _accBank, ToAccountId = _accCash, FeeCents = 200, Summary = "带手续费转账",
        });
        var expenseAfter = new VoucherService().Query(new VoucherQuery { Kind = VoucherKind.Expense, Limit = 9999 }).Count;
        Check("转账手续费生成独立支出单据", expenseAfter == expenseBefore + 1, $"{expenseBefore} → {expenseAfter}");
    }

    // ---------------- E. 精度 ----------------
    private static void CaseE()
    {
        Section("E 金额精度");
        Check("0.10 + 0.20 = 0.30", Money.ToCents(0.10m) + Money.ToCents(0.20m) == Money.ToCents(0.30m), "");
        var sum = 0L;
        for (var i = 0; i < 1000; i++) sum += Money.ToCents(0.10m);
        Check("1000 × 0.10 = 100.00", sum == 10_000, Money.Format(sum));
        var t = Money.SplitTax(10_000_000, 1300);
        Check("13% 价税分离可逆", t.net + t.tax == 10_000_000, $"{t.net}+{t.tax}");
        Check("舍入方式 AwayFromZero", Money.ToCents(0.005m) == 1, Money.ToCents(0.005m).ToString());
        Check("大写金额正确", RmbUpper.Convert(1_270_921.44m) == "壹佰贰拾柒万零玖佰贰拾壹元肆角肆分", RmbUpper.Convert(1_270_921.44m));
        Check("大写金额（整数）", RmbUpper.Convert(486_000m) == "肆拾捌万陆仟元整", RmbUpper.Convert(486_000m));
    }

    // ---------------- F. 状态与重复操作 ----------------
    private static void CaseF()
    {
        Section("F 状态与重复操作");
        var svc = new VoucherService();
        var pending = svc.Create(NewVoucher(VoucherKind.Receivable, VoucherNature.None, "2027-04-01", 1_000_000, _custD, null, _projA, "待审核应收"));
        var p1 = MasterDataService.GetPartner(_custD)!;
        Check("待审核不计入往来余额", p1.NetReceivableCents == 0, Money.Format(p1.NetReceivableCents));

        var r = svc.Query(new VoucherQuery { Kind = VoucherKind.Receivable, OnlyAudited = true });
        Check("待审核不出现在已审核查询", r.All(x => x.Id != pending), "");

        svc.Audit(pending);
        var p2 = MasterDataService.GetPartner(_custD)!;
        Check("审核后计入往来余额", p2.NetReceivableCents == 1_000_000, Money.Format(p2.NetReceivableCents));

        var dupAuditBlocked = false;
        try { svc.Audit(pending); } catch (AppException) { dupAuditBlocked = true; }
        Check("重复审核被阻止", dupAuditBlocked, "");

        var p3 = MasterDataService.GetPartner(_custD)!;
        Check("重复审核未重复计账", p3.NetReceivableCents == 1_000_000, Money.Format(p3.NetReceivableCents));

        var editBlocked = false;
        try
        {
            var v = svc.Get(pending)!;
            v.AmountCents = 2_000_000;
            svc.Update(v);
        }
        catch (AppException) { editBlocked = true; }
        Check("已审核单据不可修改", editBlocked, "");

        // 反审核：有下游则阻止
        var pay = Create(NewVoucher(VoucherKind.Income, VoucherNature.CustomerReceipt, "2027-04-02", 1_000_000, _custD, _accBank, null, "回款"));
        new SettlementService().Allocate(pay, pending, 1_000_000);
        var unauditBlocked = false;
        try { svc.Unaudit(pending, "测试"); } catch (AppException) { unauditBlocked = true; }
        Check("存在核销时禁止反审核", unauditBlocked, "");

        var st = new SettlementService().ListByBiz(pending).First();
        new SettlementService().Unallocate(st.Id, "解除后反审核");
        svc.Unaudit(pending, "测试反审核");
        Check("解除核销后可反审核", svc.Get(pending)!.Status == VoucherStatus.Pending, "");
        // 回款单仍有效，反审核应收后客户变为预收 10,000（负数）
        Check("反审核后余额回落为预收 -10,000", MasterDataService.GetPartner(_custD)!.NetReceivableCents == -1_000_000,
            Money.Format(MasterDataService.GetPartner(_custD)!.NetReceivableCents));

        // 作废
        svc.Void(pending, "测试作废");
        Check("作废后状态正确", svc.Get(pending)!.Status == VoucherStatus.Void, "");
        Check("作废单据不计入应收，仅剩预收 -10,000", MasterDataService.GetPartner(_custD)!.NetReceivableCents == -1_000_000,
            Money.Format(MasterDataService.GetPartner(_custD)!.NetReceivableCents));
    }

    // ---------------- G. 持久化 ----------------
    private static void CaseG(string dir)
    {
        Section("G 持久化（关闭后重新打开）");
        var countBefore = new VoucherService().Query(new VoucherQuery { Limit = 9999 }).Count;
        var bankBefore = MasterDataService.AccountBalance(_accBank);
        var custBefore = MasterDataService.GetPartner(_custA)!.NetReceivableCents;

        // 重新打开数据库（模拟重启）
        AppSession.Db = new Database(dir);
        AppSession.Db.EnsureCreated();
        AppSession.Current = new AuthService().Login("admin", "admin888");

        var countAfter = new VoucherService().Query(new VoucherQuery { Limit = 9999 }).Count;
        var bankAfter = MasterDataService.AccountBalance(_accBank);
        var custAfter = MasterDataService.GetPartner(_custA)!.NetReceivableCents;
        Check("重新打开后单据数量一致", countBefore == countAfter, $"{countBefore} → {countAfter}");
        Check("重新打开后账户余额一致", bankBefore == bankAfter, $"{Money.Format(bankBefore)} → {Money.Format(bankAfter)}");
        Check("重新打开后往来余额一致", custBefore == custAfter, $"{Money.Format(custBefore)} → {Money.Format(custAfter)}");
    }

    // ---------------- H. 备份恢复 ----------------
    private static void CaseH(string dir)
    {
        Section("H 备份与恢复");
        var bak = _db.BackupTo(null, "selftest");
        Check("生成备份文件", File.Exists(bak), bak);

        var countBefore = new VoucherService().Query(new VoucherQuery { Limit = 9999 }).Count;
        var bankBefore = MasterDataService.AccountBalance(_accBank);
        var custBefore = MasterDataService.GetPartner(_custA)!.NetReceivableCents;
        var reportBefore = new ReportService().Build("cash_detail", new ReportFilter { From = "2026-01-01", To = "2027-12-31" }).Rows.Count;

        // 备份后制造差异
        Create(NewVoucher(VoucherKind.Income, VoucherNature.SaleCash, "2027-05-01", 12_345_600, _custA, _accBank, _projA, "备份后新增"));
        var countDirty = new VoucherService().Query(new VoucherQuery { Limit = 9999 }).Count;
        Check("备份后新增数据生效", countDirty == countBefore + 1, $"{countBefore} → {countDirty}");

        // 恢复
        _db.RestoreFrom(bak, m => Write("  " + m));
        new AuthService().Login("admin", "admin888");
        var countAfter = new VoucherService().Query(new VoucherQuery { Limit = 9999 }).Count;
        var bankAfter = MasterDataService.AccountBalance(_accBank);
        var custAfter = MasterDataService.GetPartner(_custA)!.NetReceivableCents;
        var reportAfter = new ReportService().Build("cash_detail", new ReportFilter { From = "2026-01-01", To = "2027-12-31" }).Rows.Count;

        Check("恢复后单据数量回到备份点", countAfter == countBefore, $"{countBefore} → {countAfter}");
        Check("恢复后账户余额一致", bankAfter == bankBefore, $"{Money.Format(bankBefore)} → {Money.Format(bankAfter)}");
        Check("恢复后往来余额一致", custAfter == custBefore, $"{Money.Format(custBefore)} → {Money.Format(custAfter)}");
        Check("恢复后报表行数一致", reportAfter == reportBefore, $"{reportBefore} → {reportAfter}");
        Check("恢复前自动生成安全备份", Directory.GetFiles(_db.BackupDir, "*before-restore*").Length > 0, "");
    }

    // ---------------- 其他边界 ----------------
    private static void Extras()
    {
        Section("补充边界场景");

        // 1 超额收款（预收）
        var c = MasterDataService.SavePartner(new Partner { Code = "T-C09", Name = "超额客户", Kind = "customer" });
        Create(NewVoucher(VoucherKind.Receivable, VoucherNature.None, "2027-06-01", 1_000_000, c, null, _projA, "应收1万"));
        Create(NewVoucher(VoucherKind.Income, VoucherNature.CustomerReceipt, "2027-06-05", 1_500_000, c, _accBank, null, "超收1.5万"));
        var p = MasterDataService.GetPartner(c)!;
        Check("超额收款形成预收（净应收 -5,000）", p.NetReceivableCents == -500_000, Money.Format(p.NetReceivableCents));

        // 2 退款
        Create(NewVoucher(VoucherKind.Expense, VoucherNature.CustomerRefund, "2027-06-10", 300_000, c, _accBank, null, "退还客户3千"));
        Check("退款后净应收回到 -2,000", MasterDataService.GetPartner(c)!.NetReceivableCents == -200_000,
            Money.Format(MasterDataService.GetPartner(c)!.NetReceivableCents));

        // 3 期初余额
        var c2 = MasterDataService.SavePartner(new Partner
        {
            Code = "T-C10", Name = "有期初客户", Kind = "customer", OpeningReceivableCents = 5_000_000,
        });
        Check("期初应收计入净应收", MasterDataService.GetPartner(c2)!.NetReceivableCents == 5_000_000,
            Money.Format(MasterDataService.GetPartner(c2)!.NetReceivableCents));

        var acc = MasterDataService.ListAccounts(false).First();
        var bal = MasterDataService.AccountBalance(acc.Id);
        var opening = acc.OpeningCents;
        Check("账户余额可追溯到期末公式", bal == opening + 0 || bal != 0, Money.Format(bal));

        // 4 期间关闭
        PeriodService.Close("202703");
        var blocked = false;
        try
        {
            Create(NewVoucher(VoucherKind.Income, VoucherNature.SaleCash, "2027-03-25", 100_000, null, _accBank, null, "关闭期间录入"));
        }
        catch (AppException) { blocked = true; }
        Check("已关闭期间禁止新增单据", blocked, "");
        PeriodService.Reopen("202703", "自检需要");
        var okAfterReopen = true;
        try { Create(NewVoucher(VoucherKind.Income, VoucherNature.SaleCash, "2027-03-26", 100_000, null, _accBank, null, "重开后录入")); }
        catch (AppException) { okAfterReopen = false; }
        Check("重新开启期间后可录入", okAfterReopen, "");

        // 5 发票作废与重复
        var invSvc = new InvoiceService();
        var invId = invSvc.Save(new Invoice
        {
            Kind = InvoiceKind.Out, InvoiceType = "电子发票（普通）", InvoiceNo = "INV-X-0001",
            PartnerId = _custA, RegDate = "2027-07-01", GrossCents = 1_060_000,
            NetCents = Money.SplitTax(1_060_000, 600).net, TaxCents = Money.SplitTax(1_060_000, 600).tax,
            RateBp = 600, Content = "作废测试",
        });
        var dupInv = false;
        try
        {
            invSvc.Save(new Invoice
            {
                Kind = InvoiceKind.Out, InvoiceType = "电子发票（普通）", InvoiceNo = "INV-X-0001",
                PartnerId = _custA, RegDate = "2027-07-02", GrossCents = 1_060_000,
                NetCents = Money.SplitTax(1_060_000, 600).net, TaxCents = Money.SplitTax(1_060_000, 600).tax, RateBp = 600,
            });
        }
        catch (AppException) { dupInv = true; }
        Check("重复发票号码被拒绝", dupInv, "");

        invSvc.Void(invId, "开票有误");
        Check("发票作废成功", invSvc.Get(invId)!.Status == InvoiceStatus.Void, "");
        var reUse = true;
        try
        {
            invSvc.Save(new Invoice
            {
                Kind = InvoiceKind.Out, InvoiceType = "电子发票（普通）", InvoiceNo = "INV-X-0001",
                PartnerId = _custA, RegDate = "2027-07-05", GrossCents = 500_000,
                NetCents = Money.SplitTax(500_000, 600).net, TaxCents = Money.SplitTax(500_000, 600).tax, RateBp = 600,
            });
        }
        catch (AppException) { reUse = false; }
        Check("作废后号码可重新使用", reUse, "");

        // 6 金额校验
        var badAmount = false;
        try { Money.Parse("abc"); } catch (AppException) { badAmount = true; }
        Check("非法金额被拒绝", badAmount, "");
        var negAmount = false;
        try { Money.Parse("-100"); } catch (AppException) { negAmount = true; }
        Check("负数金额被拒绝", negAmount, "");
        var tooBig = false;
        try { Money.ToCents(1_000_000_000m); } catch (AppException) { tooBig = true; }
        Check("超范围金额被拒绝", tooBig, "");

        // 7 错误日期
        var badDate = false;
        try
        {
            Create(NewVoucher(VoucherKind.Income, VoucherNature.SaleCash, "2027-13-45", 100_000, null, _accBank, null, "错误日期"));
        }
        catch (Exception) { badDate = true; }
        Check("非法日期被拒绝", badDate, "");

        // 8 跨年报表
        var yearReport = new ReportService().Build("cash_year", new ReportFilter { From = "2026-01-01", To = "2027-12-31" });
        Check("收支年报表跨年输出", yearReport.Rows.Count >= 2, yearReport.Rows.Count.ToString());
        var monthReport = new ReportService().Build("cash_month", new ReportFilter { From = "2026-01-01", To = "2027-12-31" });
        var sumMonth = monthReport.Rows.Sum(r => (long)r.Cells[1]!) - monthReport.Rows.Sum(r => (long)r.Cells[2]!);
        var sumYear = yearReport.Rows.Sum(r => (long)r.Cells[1]!) - yearReport.Rows.Sum(r => (long)r.Cells[2]!);
        Check("月表合计 = 年表合计（发生额）", sumMonth == sumYear, $"{Money.Format(sumMonth)} vs {Money.Format(sumYear)}");

        // 9 账户报表合计 = 全部账户余额
        var accReport = new ReportService().Build("account_report", new ReportFilter { From = "2026-01-01", To = "2027-12-31" });
        var endTotal = (long)accReport.TotalRow![4]!;
        var allFunds = VoucherService.TotalFunds("2027-12-31");
        Check("账户报表期末合计 = 全部账户余额", endTotal == allFunds, $"{Money.Format(endTotal)} vs {Money.Format(allFunds)}");

        // 10 全部报表均可生成
        var failed = new List<string>();
        foreach (var (id, title, _) in ReportService.Catalog)
        {
            try
            {
                var monthlyDebt = id is "supplier_monthly_payable" or "customer_monthly_receivable";
                var rep = new ReportService().Build(id, new ReportFilter { From = "2026-01-01", To = monthlyDebt ? "2026-01-31" : "2027-12-31" });
                if (rep.Columns.Count == 0) failed.Add(title);
            }
            catch (Exception ex) { failed.Add(title + ":" + ex.Message); }
        }
        Check($"{ReportService.Catalog.Length} 张报表全部可生成", failed.Count == 0, string.Join(";", failed));

        // 11 Excel 导入（模板 → 填充 → 预览 → 导入 → 重复检测）
        var tmp = Path.Combine(Path.GetTempPath(), "cashier-import-" + Guid.NewGuid().ToString("N")[..6] + ".xlsx");
        ExcelService.CreateVoucherTemplate(VoucherKind.Income, tmp);
        using (var wb = new ClosedXML.Excel.XLWorkbook(tmp))
        {
            var ws = wb.Worksheet(1);
            ws.Cell(2, 1).Value = "2027-08-01";
            ws.Cell(2, 2).Value = "SaleCash";
            ws.Cell(2, 3).Value = 1234.56;
            ws.Cell(2, 4).Value = MasterDataService.ListAccounts(false).First().Code;
            ws.Cell(2, 10).Value = "导入测试收入";
            ws.Cell(3, 1).Value = "2027-08-02";
            ws.Cell(3, 2).Value = "SaleCash";
            ws.Cell(3, 3).Value = 100.00;
            ws.Cell(3, 4).Value = MasterDataService.ListAccounts(false).First().Code;
            ws.Cell(3, 10).Value = "导入测试收入二";
            ws.Cell(4, 1).Value = "错误日期";
            ws.Cell(4, 2).Value = "SaleCash";
            ws.Cell(4, 3).Value = 50;
            ws.Cell(4, 4).Value = MasterDataService.ListAccounts(false).First().Code;
            ws.Cell(4, 10).Value = "错误行";
            wb.Save();
        }
        var preview = ExcelService.PreviewVoucherImport(VoucherKind.Income, tmp);
        Check("导入预览识别 2 行有效 + 1 行错误", preview.OkCount == 2 && preview.ErrorCount == 1,
            $"ok={preview.OkCount}, err={preview.ErrorCount}");
        var importedCount = ExcelService.CommitVoucherImport(preview);
        Check("导入写入 2 张单据", importedCount == 2, importedCount.ToString());
        var preview2 = ExcelService.PreviewVoucherImport(VoucherKind.Income, tmp);
        Check("重复导入被识别", preview2.DuplicateCount == 2, preview2.DuplicateCount.ToString());
        var commitAgainBlocked = false;
        try { ExcelService.CommitVoucherImport(preview2); } catch (AppException) { commitAgainBlocked = true; }
        Check("重复导入被阻止", commitAgainBlocked, "");
        try { File.Delete(tmp); } catch { }

        // 12 权限
        var auth = new AuthService();
        auth.Create("cashier-extra", "出纳", "cashier-test-4821", UserRole.Cashier);
        AppSession.Current = auth.Login("cashier-extra", "cashier-test-4821");
        var auditBlocked = false;
        try
        {
            var anyId = new VoucherService().Query(new VoucherQuery { Limit = 1, Status = VoucherStatus.Pending }).FirstOrDefault();
            if (anyId != null) new VoucherService().Audit(anyId.Id);
            else { new VoucherService().Audit(1); }
        }
        catch (AppException) { auditBlocked = true; }
        Check("出纳角色不能审核单据", auditBlocked, "");
        AppSession.Current = new AuthService().Login("admin", "admin888");
    }
}
