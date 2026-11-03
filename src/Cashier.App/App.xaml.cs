using System.IO;
using System.Windows;
using System.Windows.Threading;
using Cashier.Core;
using Cashier.Core.Services;

namespace Cashier.App;

public partial class App : Application
{
    public static Database Db { get; private set; } = null!;
    public static string? StartupPage { get; set; }

    /// <summary>QA 专用：设置后登录窗口自动填充并提交（正常启动为 null）。</summary>
    public static (string user, string pwd)? QaAutoLogin { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            TryLog(ex);
        };

        string? dataDir = null;
        for (var i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] is "--data-dir" or "-d" && i + 1 < e.Args.Length) dataDir = e.Args[i + 1];
            if (e.Args[i].StartsWith("--data-dir=")) dataDir = e.Args[i]["--data-dir=".Length..];
            if (e.Args[i].StartsWith("--page=")) StartupPage = e.Args[i]["--page=".Length..];
        }

        if ((e.Args.Contains("--qa-invoice-customer") || e.Args.Contains("--qa-batch-layout")) && dataDir == null)
        {
            Shutdown(2); // QA must never fall back to the real/default ledger.
            return;
        }

        // 1.1.3：指定了账套目录但目录里没有账套文件时先确认。
        // 旧行为是直接新建空账套，路径写错（例如含空格的路径没加引号被截断）会让用户在
        // 一个空白账套里录入数据，而界面上没有任何提示。QA 钩子需要空目录，不拦截。
        if (dataDir != null && !IsQaInvocation(e.Args) && !ConfirmNewBook(dataDir))
        {
            Shutdown(5);
            return;
        }

        try
        {
            Db = dataDir == null ? Database.CreateDefault() : new Database(dataDir);
            Db.EnsureCreated(msg => Log(msg));
            AppSession.Db = Db;
        }
        catch (Exception ex)
        {
            MessageBox.Show("账套数据库初始化失败：\n" + ex.Message + "\n\n数据目录：" + (dataDir ?? Database.DefaultDataDir),
                "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // QA：生成演示账套 / 界面截图（仅用于验收，正式使用不会触发）
        if(e.Args.Contains("--qa-batch-layout"))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var ix=Array.IndexOf(e.Args,"--qa-batch-layout");var output=e.Args[ix+1];
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>
            {
                try{Shutdown(QaBatchLayout.Run(output)?0:2);}catch(Exception ex){Directory.CreateDirectory(output);File.WriteAllText(Path.Combine(output,"layout-error.txt"),ex.ToString());Shutdown(2);}
            }));return;
        }
        if (e.Args.Contains("--qa-invoice-customer"))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var index=Array.IndexOf(e.Args,"--qa-invoice-customer");
            var output=e.Args[index+1];
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>
            {
                try { Shutdown(QaInvoiceCustomer.Run(output) ? 0 : 2); }
                catch(Exception ex) { Directory.CreateDirectory(output);File.WriteAllText(Path.Combine(output,"ui-error.txt"),ex.ToString());Shutdown(2); }
            }));
            return;
        }
        if (e.Args.Contains("--seed-demo"))
        {
            try
            {
                AppSession.Current = new AuthService().Login("admin", "admin888");
                QaTools.SeedDemo();
                Console.WriteLine("演示账套已生成：" + Db.FilePath);
                Shutdown(0);
            }
            catch (Exception ex)
            {
                MessageBox.Show("演示账套生成失败：" + ex.Message, "QA", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(2);
            }
            return;
        }
        if (e.Args.Contains("--seed-demo") || e.Args.Contains("--qa-shot") || e.Args.Contains("--qa-forms")) ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (e.Args.Contains("--qa-forms"))
        {
            var ix = Array.IndexOf(e.Args, "--qa-forms");
            var path = ix + 1 < e.Args.Length ? e.Args[ix + 1] : Path.Combine(Db.DataDir, "forms-test.txt");
            try
            {
                AppSession.Current = new AuthService().Login("admin", "admin888");
                var pass = QaTools.CheckForms(path);
                Shutdown(pass ? 0 : 2);
            }
            catch (Exception ex) { File.WriteAllText(path, ex.ToString()); Shutdown(2); }
            return;
        }
        if (e.Args.Contains("--qa-login-flow"))
        {
            var i3 = Array.IndexOf(e.Args, "--qa-login-flow");
            var f3 = i3 + 1 < e.Args.Length ? e.Args[i3 + 1] : Path.Combine(Db.DataDir, "login-flow.txt");
            QaLoginFlow(f3);
            return;
        }
        if (e.Args.Contains("--qa-login"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var i2 = Array.IndexOf(e.Args, "--qa-login");
            var dir2 = i2 + 1 < e.Args.Length ? e.Args[i2 + 1] : Path.Combine(Db.DataDir, "screenshots");
            try { QaTools.CaptureLogin(dir2); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(dir2, "error-login.txt"), ex.ToString()); Shutdown(2); }
            return;
        }
        if (e.Args.Contains("--qa-shot"))
        {
            var idx = Array.IndexOf(e.Args, "--qa-shot");
            var outDir = idx + 1 < e.Args.Length ? e.Args[idx + 1] : Path.Combine(Db.DataDir, "screenshots");
            try
            {
                AppSession.Current = new AuthService().Login("admin", "admin888");
                QaTools.CaptureScreens(outDir);
                Shutdown(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(outDir, "error.txt"), ex.ToString());
                MessageBox.Show("截图失败：" + ex.Message, "QA", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(2);
            }
            return;
        }

        ShowLoginAndMain();
    }

    /// <summary>登录窗口 → 主窗口（真实启动路径，QA 也调用这里）。</summary>
    public bool ShowLoginAndMain()
    {
        // 登录窗是此刻唯一的窗口：必须先关闭“最后一个窗口关闭即退出”，
        // 否则登录成功关闭登录窗会立刻触发应用退出，主窗口来不及显示。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var login = new LoginWindow();
        var ok = login.ShowDialog();
        if (ok != true)
        {
            Shutdown();
            return false;
        }

        try
        {
            var main = new MainWindow();
            MainWindow = main;
            ShutdownMode = ShutdownMode.OnMainWindowClose; // 之后由主窗口关闭决定退出
            main.Show();
            main.Activate();
            if (!main.IsVisible)
            {
                Log("警告：主窗口 Show 后仍不可见");
                MessageBox.Show("主窗口未能显示，请重启软件。", "出纳管理", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown(3);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            TryLog(ex);
            MessageBox.Show("进入主界面失败：\n" + ex.Message + "\n\n日志：" + Path.Combine(Db.LogDir, "error.log"),
                "出纳管理", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(4);
            return false;
        }
    }

    /// <summary>QA：自动化走一遍真实登录流程并记录结果。</summary>
    public void QaLoginFlow(string resultFile)
    {
        var log = new System.Text.StringBuilder();
        try
        {
            QaAutoLogin = ("admin", "admin888");
            var ok = ShowLoginAndMain();
            log.AppendLine("ShowLoginAndMain 返回：" + ok);
            log.AppendLine("ShutdownMode：" + ShutdownMode);
            log.AppendLine("MainWindow 是否为空：" + (MainWindow == null));
            log.AppendLine("MainWindow 可见：" + (MainWindow?.IsVisible.ToString() ?? "null"));
            log.AppendLine("窗口数量：" + Windows.Count);
            var pass = ok && MainWindow is { IsVisible: true };
            if (pass && MainWindow != null)
            {
                var png = Path.ChangeExtension(resultFile, ".png");
                try
                {
                    QaTools.CaptureWindow(MainWindow, png);
                    log.AppendLine("主界面截图：" + png);
                }
                catch (Exception ex2) { log.AppendLine("截图失败：" + ex2.Message); }
            }
            log.AppendLine("RESULT=" + (pass ? "OK" : "FAIL"));
        }
        catch (Exception ex)
        {
            log.AppendLine("RESULT=FAIL");
            log.AppendLine(ex.ToString());
        }
        finally
        {
            try { File.WriteAllText(resultFile, log.ToString(), System.Text.Encoding.UTF8); } catch { }
            Shutdown(0);
        }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        TryLog(e.Exception);
        MessageBox.Show("操作出现异常：\n" + Database.FriendlyMessage(e.Exception) + "\n\n如问题持续，请联系管理员并附上日志。",
            "出纳管理 · 操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    /// <summary>QA/演示钩子：这些模式必须允许在空目录上建库，不能弹确认框。</summary>
    private static bool IsQaInvocation(string[] args) =>
        args.Any(a => a.StartsWith("--qa-", StringComparison.Ordinal) || a == "--seed-demo");

    /// <summary>
    /// 指定目录下没有 cashier.db 时确认是否新建空账套。
    /// 返回 true 表示继续（目录里已有账套、路径非法交由后续逻辑报错、或用户确认新建）。
    /// </summary>
    private static bool ConfirmNewBook(string dataDir)
    {
        string full;
        try { full = Path.GetFullPath(dataDir); }
        catch { return true; } // 非法路径：交给 Database 构造/建库抛业务异常，这里不重复判断

        if (File.Exists(Path.Combine(full, "cashier.db"))) return true;

        var r = MessageBox.Show(
            "该目录下没有账套文件（cashier.db）：\n" + full +
            "\n\n将在该目录新建一个空账套，是否继续？\n" +
            "（如果是账套路径写错，请选择“否”，再用 --data-dir 重新指定正确目录）",
            "出纳管理 · 新建账套", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        return r == MessageBoxResult.Yes;
    }

    private static void TryLog(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            var dir = Db?.LogDir ?? Path.Combine(Database.DefaultDataDir, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\r\n\r\n");
        }
        catch { }
    }

    private static void Log(string msg)
    {
        try
        {
            var dir = Db?.LogDir ?? Path.Combine(Database.DefaultDataDir, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "startup.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\r\n");
        }
        catch { }
    }
}
