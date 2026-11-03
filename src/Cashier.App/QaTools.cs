using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cashier.Core;
using Cashier.Core.Services;

namespace Cashier.App;

/// <summary>演示账套生成与界面截图（QA/验收用，不影响正式账套）。</summary>
public static class QaTools
{
    public static void SeedDemo()
    {
        var now = DateTime.Today;
        var m0 = new DateTime(now.Year, now.Month, 1);
        string D(int monthOffset, int day) => m0.AddMonths(monthOffset).AddDays(day - 1).ToString("yyyy-MM-dd");

        long Acc(string code) => MasterDataService.ListAccounts(false).First(a => a.Code == code).Id;

        var bank = Acc("100201");
        var bank2 = Acc("100202");
        var cash = Acc("1001");

        var c1 = MasterDataService.SavePartner(new Partner { Code = "KH-0001", Name = "星禾科技有限公司", Kind = "customer", Contact = "周明远", Phone = "138 0013 4081", Terms = "月结30天", Level = "A", OpeningReceivableCents = 0 });
        var c2 = MasterDataService.SavePartner(new Partner { Code = "KH-0002", Name = "云途物流股份公司", Kind = "customer", Contact = "李晓峰", Phone = "139 2288 6402", Terms = "月结45天", Level = "A" });
        var c3 = MasterDataService.SavePartner(new Partner { Code = "KH-0003", Name = "恒美建材集团", Kind = "customer", Contact = "孙海燕", Phone = "137 5566 1280", Terms = "预付40%", Level = "B" });
        var c4 = MasterDataService.SavePartner(new Partner { Code = "KH-0004", Name = "宇迪智能装备", Kind = "customer", Contact = "陈昊", Phone = "135 8899 2043", Terms = "货到付款", Level = "C", OpeningReceivableCents = 20_000_000 });
        var s1 = MasterDataService.SavePartner(new Partner { Code = "GY-0001", Name = "明泰电子", Kind = "supplier", Contact = "刘振", Phone = "136 1122 4455", Terms = "月结30天" });
        var s2 = MasterDataService.SavePartner(new Partner { Code = "GY-0002", Name = "信达云服务", Kind = "supplier", Contact = "沈云", Phone = "138 5566 7788", Terms = "年付" });
        var s3 = MasterDataService.SavePartner(new Partner { Code = "GY-0003", Name = "中恒办公用品", Kind = "supplier", Contact = "王强", Phone = "135 2233 6677", Terms = "现结" });

        var p1 = MasterDataService.SaveProject(new Project { Code = "P-2601", Name = "星禾·智能仓储集成", Owner = "李静" });
        var p2 = MasterDataService.SaveProject(new Project { Code = "P-2602", Name = "云途·干线运输系统", Owner = "王雅琴" });
        var p3 = MasterDataService.SaveProject(new Project { Code = "P-2603", Name = "恒美·门店数字化", Owner = "李静" });

        var depts = MasterDataService.ListDepts(false);
        var d1 = depts.First(d => d.Name == "业务部").Id;
        var d2 = depts.First(d => d.Name == "采购部").Id;
        var subjIn = MasterDataService.ListSubjects("in").First().Id;
        var subjOut = MasterDataService.ListSubjects("out").First().Id;

        var svc = new VoucherService();

        long V(VoucherKind kind, VoucherNature nature, string date, long cents, long? partner, long? account,
              long? project, string summary, bool audit = true, bool revenue = false, bool cost = false,
              long? toAccount = null, long fee = 0, long? subject = null)
        {
            var v = new Voucher
            {
                Kind = kind, Nature = nature, BizDate = date, AmountCents = cents,
                PartnerId = partner, AccountId = account, ToAccountId = toAccount, FeeCents = fee,
                ProjectId = project, DeptId = kind == VoucherKind.Expense ? d2 : d1,
                Summary = summary, SubjectId = subject,
                CountAsRevenue = revenue, CountAsCost = cost,
                Invoiceable = kind is VoucherKind.Receivable || nature == VoucherNature.SaleCash,
            };
            var id = svc.Create(v);
            if (audit) svc.Audit(id);
            return id;
        }

        // 上上月
        V(VoucherKind.Income, VoucherNature.SaleCash, D(-2, 6), 8_600_000, c2, bank, p2, "云途物流 运输服务费（现收）", subject: subjIn);
        V(VoucherKind.Expense, VoucherNature.PurchaseCash, D(-2, 8), 1_860_000, s3, bank, null, "采购办公家具", cost: true, subject: subjOut);
        V(VoucherKind.Expense, VoucherNature.PurchaseCash, D(-2, 12), 4_680_000, null, bank, null, "办公室租金", cost: true, subject: subjOut);

        // 上月
        var r1 = V(VoucherKind.Receivable, VoucherNature.None, D(-1, 5), 38_400_000, c1, null, p1, "星禾科技 智能仓储项目 一期款", revenue: true, subject: subjIn);
        V(VoucherKind.Income, VoucherNature.CustomerReceipt, D(-1, 18), 30_000_000, c1, bank, p1, "星禾科技 一期款回款");
        V(VoucherKind.Income, VoucherNature.CustomerPrepay, D(-1, 20), 10_000_000, c3, bank, p3, "恒美建材 项目预收");
        V(VoucherKind.Expense, VoucherNature.SupplierPayment, D(-1, 22), 12_860_000, s1, bank, p1, "支付明泰电子 机柜款");
        var b1 = V(VoucherKind.Payable, VoucherNature.None, D(-1, 10), 12_860_000, s1, null, p1, "采购服务器机柜 6 台", cost: true, subject: subjOut);
        V(VoucherKind.Transfer, VoucherNature.None, D(-1, 25), 5_000_000, null, bank, null, "基本户转一般户备付金", toAccount: bank2, fee: 200);

        // 本月
        var r2 = V(VoucherKind.Receivable, VoucherNature.None, D(0, 3), 48_600_000, c1, null, p1, "星禾科技 智能仓储项目 二期款", revenue: true, subject: subjIn);
        V(VoucherKind.Income, VoucherNature.CustomerReceipt, D(0, 6), 20_000_000, c1, bank, p1, "星禾科技 二期款部分回款");
        var r3 = V(VoucherKind.Receivable, VoucherNature.None, D(0, 8), 21_340_000, c2, null, p2, "云途物流 10 月运输服务费", revenue: true, subject: subjIn);
        V(VoucherKind.Income, VoucherNature.CustomerReceipt, D(0, 12), 21_340_000, c2, bank, p2, "云途物流 服务费回款");
        V(VoucherKind.Receivable, VoucherNature.None, D(0, 10), 16_000_000, c3, null, p3, "恒美建材 门店数字化 首期", revenue: true, subject: subjIn);
        V(VoucherKind.Expense, VoucherNature.PurchaseCash, D(0, 14), 1_280_000, s3, cash, null, "办公用品采购", cost: true, subject: subjOut);
        V(VoucherKind.Expense, VoucherNature.PurchaseCash, D(0, 15), 3_862_50, null, cash, p1, "差旅费报销（杭州—宁波）", cost: true, subject: subjOut);
        V(VoucherKind.Expense, VoucherNature.PurchaseCash, D(0, 16), 8_800_000, s2, bank, p2, "信达云服务 年度订阅", cost: true, subject: subjOut);
        V(VoucherKind.Income, VoucherNature.OtherIn, D(0, 18), 186_432, null, bank, null, "银行存款利息", subject: subjIn);
        V(VoucherKind.Expense, VoucherNature.CustomerRefund, D(0, 20), 1_000_000, c3, bank, p3, "退还恒美建材 多收预付款");
        V(VoucherKind.Expense, VoucherNature.SupplierPrepay, D(0, 21), 5_000_000, s1, bank, p1, "预付明泰电子 设备定金");

        // 待审核与草稿（用于演示状态）
        V(VoucherKind.Income, VoucherNature.SaleCash, D(0, 22), 960_000, c2, cash, p2, "零星配送服务费（待审核）", audit: false, subject: subjIn);
        V(VoucherKind.Receivable, VoucherNature.None, D(0, 23), 4_200_000, c4, null, p1, "宇迪智能 技术服务费（草稿）", audit: false, revenue: true, subject: subjIn);

        // 核销
        var settle = new SettlementService();
        var pay1 = svc.Query(new VoucherQuery { Kind = VoucherKind.Income, PartnerId = c1, OnlyAudited = true, Limit = 20 })
            .OrderBy(v => v.BizDate).First();
        settle.AutoAllocate(pay1.Id);
        var pay2 = svc.Query(new VoucherQuery { Kind = VoucherKind.Income, PartnerId = c2, OnlyAudited = true, Limit = 20 })
            .Where(v => v.Nature is VoucherNature.CustomerReceipt or VoucherNature.CustomerPrepay)
            .OrderBy(v => v.BizDate).First();
        settle.AutoAllocate(pay2.Id);
        settle.AutoAllocate(svc.Query(new VoucherQuery { Kind = VoucherKind.Expense, PartnerId = s1, OnlyAudited = true, Limit = 20 })
            .OrderBy(v => v.BizDate).First().Id);

        // 发票
        var inv = new InvoiceService();
        void Inv(InvoiceKind kind, string no, string code, long partner, string date, long gross, int rate, string content)
        {
            var (net, tax) = Money.SplitTax(gross, rate);
            inv.Save(new Invoice
            {
                Kind = kind, InvoiceType = kind == InvoiceKind.Out ? "电子发票（普通）" : "增值税专用发票",
                InvoiceNo = no, InvoiceCode = code, PartnerId = partner, RegDate = date,
                GrossCents = gross, NetCents = net, TaxCents = tax, RateBp = rate, Content = content,
            });
        }
        Inv(InvoiceKind.Out, "254420000000300001", "044001900111", c1, D(0, 7), 48_600_000, 600, "技术服务费");
        Inv(InvoiceKind.Out, "254420000000300002", "044001900111", c2, D(0, 13), 21_340_000, 600, "运输服务费");
        Inv(InvoiceKind.Out, "254420000000300003", "044001900111", c3, D(0, 11), 16_000_000, 1300, "技术服务费");
        Inv(InvoiceKind.In, "04400190011199887766", "044001900111", s1, D(-1, 11), 12_860_000, 1300, "服务器机柜");
        Inv(InvoiceKind.In, "04400190011199881122", "044001900111", s2, D(0, 16), 8_800_000, 600, "云服务订阅");

        // 演示备份文件
        AppSession.Db.BackupTo(null, "demo");

        // 关联：销项发票 → 应收单
        var invList = inv.List(InvoiceKind.Out, null, null, null, null, false);
        var i1 = invList.First(i => i.InvoiceNo == "254420000000300001");
        inv.Link(i1.Id, r2, 48_600_000);
        var i2 = invList.First(i => i.InvoiceNo == "254420000000300002");
        inv.Link(i2.Id, r3, 21_340_000);
    }

    public static void CaptureScreens(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var pages = new (string id, string file)[]
        {
            ("ledger", "01-收支明细"),
            ("receivable", "02-应收管理"),
            ("invoiceOut", "03-销项发票"),
            ("rpt_customer_summary", "04-客户应收汇总"),
            ("rpt_cash_month", "05-收支月报表"),
            ("md_partner", "06-客户与供应商"),
            ("audit", "07-审核中心"),
            ("tool_backup", "08-备份与恢复"),
            ("tool_import", "09-导入导出"),
        };

        foreach (var (id, file) in pages)
        {
            var win = new MainWindow { Width = 1400, Height = 820, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
            win.Show();
            win.Navigate(id);
            win.UpdateLayout();
            DoEvents();
            var w = (int)win.ActualWidth;
            var h = (int)win.ActualHeight;
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(win);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(Path.Combine(outDir, file + ".png"))) enc.Save(fs);
            win.Close();
            DoEvents();
        }
        var invoiceForm = new Cashier.App.Views.InvoiceWindow(InvoiceKind.Out, null)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0
        };
        invoiceForm.Show();
        CaptureWindow(invoiceForm, Path.Combine(outDir, "10-发票登记.png"));
        invoiceForm.Close();
        DoEvents();
    }

    /// <summary>用实际桌面单据窗口生成并保存测试数据，调用者应提供隔离的演示账套。</summary>
    public static bool CheckForms(string resultPath)
    {
        var results = new List<string>();
        var ok = true;
        foreach (var kind in new[] { VoucherKind.Receivable, VoucherKind.Payable, VoucherKind.Income, VoucherKind.Expense, VoucherKind.Transfer })
        {
            var win = new Cashier.App.Views.VoucherWindow(kind, null);
            try
            {
                win.Show();
                ((System.Windows.Controls.TextBox)win.FindName("TxtAmount")).Text = "12.34";
                ((System.Windows.Controls.TextBox)win.FindName("TxtSummary")).Text = "界面录入验证" + kind;
                if (kind is VoucherKind.Receivable or VoucherKind.Payable)
                    ((System.Windows.Controls.ComboBox)win.FindName("CmbPartner")).SelectedIndex = 1;
                var method = typeof(Cashier.App.Views.VoucherWindow).GetMethod("BuildVoucher", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                Voucher Build() => (Voucher)method.Invoke(win, null)!;
                var v = Build();
                if (kind is VoucherKind.Receivable or VoucherKind.Payable && (v.AccountId != null || v.ToAccountId != null))
                    throw new Exception("应收应付带入了隐藏账户");
                if (kind is VoucherKind.Income or VoucherKind.Expense && (v.AccountId == null || v.ToAccountId != null))
                    throw new Exception("收支账户选择不正确");
                if (kind == VoucherKind.Transfer && (v.AccountId == null || v.ToAccountId == null || v.AccountId == v.ToAccountId))
                    throw new Exception("转账账户选择不正确");
                var id = new VoucherService().Create(v);
                var saved = new VoucherService().Get(id)!;
                if (saved.AccountId != v.AccountId || saved.ToAccountId != v.ToAccountId || saved.PartnerId != v.PartnerId)
                    throw new Exception("保存后的账户或往来单位与界面不一致");
                var edit = new Cashier.App.Views.VoucherWindow(kind, saved);
                try
                {
                    edit.Show();
                    var edited = (Voucher)method.Invoke(edit, null)!;
                    if (edited.AccountId != v.AccountId || edited.ToAccountId != v.ToAccountId || edited.PartnerId != v.PartnerId)
                        throw new Exception("编辑窗口未保留原账户或往来单位");
                }
                finally { edit.Close(); }
                results.Add("PASS | " + kind + " | 新建、保存和再次打开后账户与往来单位一致");
            }
            catch (Exception ex)
            {
                ok = false;
                results.Add("FAIL | " + kind + " | " + (ex.InnerException?.Message ?? ex.Message));
            }
            finally { win.Close(); DoEvents(); }
        }
        results.Add("RESULT=" + (ok ? "OK" : "FAIL"));
        File.WriteAllLines(resultPath, results, System.Text.Encoding.UTF8);
        return ok;
    }

    /// <summary>把已显示的窗口渲染为 PNG（用于记录真实登录后的主界面）。</summary>
    public static void CaptureWindow(Window win, string file)
    {
        win.UpdateLayout();
        DoEvents();
        var w = (int)Math.Max(1, win.ActualWidth);
        var h = (int)Math.Max(1, win.ActualHeight);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(win);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(file);
        enc.Save(fs);
    }

    public static void CaptureLogin(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var win = new LoginWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
        win.Show();
        win.UpdateLayout();
        DoEvents();
        var rtb = new RenderTargetBitmap((int)win.ActualWidth, (int)win.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(win);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(Path.Combine(outDir, "00-登录窗口.png"));
        enc.Save(fs);
        win.Close();
        DoEvents();
    }

    private static void DoEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
