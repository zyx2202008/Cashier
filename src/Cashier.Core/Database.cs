using Microsoft.Data.Sqlite;

namespace Cashier.Core;

/// <summary>数据库位置、连接、建库迁移与一致性备份。</summary>
public sealed partial class Database
{
    public const int CurrentVersion = 5;

    public string DataDir { get; }
    public string FilePath { get; }
    public string BackupDir { get; }
    public string LogDir { get; }
    public string TemplateDir { get; }

    public Database(string dataDir)
    {
        DataDir = Path.GetFullPath(dataDir);
        Directory.CreateDirectory(DataDir);
        FilePath = Path.Combine(DataDir, "cashier.db");
        BackupDir = Path.Combine(DataDir, "backups");
        LogDir = Path.Combine(DataDir, "logs");
        TemplateDir = Path.Combine(DataDir, "templates");
        Directory.CreateDirectory(BackupDir);
        Directory.CreateDirectory(LogDir);
        Directory.CreateDirectory(TemplateDir);
    }

    /// <summary>默认数据目录：%APPDATA%\Cashier（用户可写、升级不覆盖）。</summary>
    public static string DefaultDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cashier");

    public static Database CreateDefault() => new(DefaultDataDir);

    public SqliteConnection Open()
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();
        var conn = new SqliteConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    public int GetUserVersion()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// 把底层 SQLite 异常翻译成用户可理解的提示（损坏/非账套文件/被占用）。
    /// 1.1.3：区分"被其他程序占用"与"文件损坏"——旧实现对被占用的账套也报"已损坏"，
    /// 会误导用户去做恢复操作。
    /// </summary>
    private static int GetUserVersionChecked(string filePath)
    {
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = filePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
                // 账套被其他进程占用时最多等 3 秒，避免启动长时间无响应（默认 30 秒）
                DefaultTimeout = 3,
            }.ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version;";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6 or 8)
        {
            throw new AppException("账套正被其他程序占用（数据库被锁定）：\n" + filePath +
                "\n\n请关闭同一账套的其他窗口后重试。");
        }
        catch (SqliteException ex)
        {
            throw new AppException($"账套数据文件无法识别或已损坏：{filePath}\n（{ex.Message}）\n请确认该文件为出纳账套（cashier.db），或从备份恢复后重试。");
        }
    }

    /// <summary>
    /// 统一使用 DELETE 日志模式。
    /// 1.1.3：WAL 模式下多连接并发写入会出现瞬时 SQLITE_READONLY(8)
    /// （同一账套双开、或两个进程同时写），异常文本为 "attempt to write a readonly database"，
    /// 用户无法理解且不会自动恢复；实测 DELETE 模式同压力下 0 失败。
    /// 本软件为单机单用户，无需 WAL 的并发读能力，故统一切回 DELETE；
    /// 历史 WAL 账套在打开时自动切换，切换失败（如被其他进程占用）则保持原样继续运行。
    /// </summary>
    private static void ConfigureJournal(SqliteConnection conn)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA busy_timeout=200; PRAGMA journal_mode=DELETE; PRAGMA busy_timeout=5000;";
            cmd.ExecuteScalar();
        }
        catch (SqliteException)
        {
            // 切换失败不影响使用，保持当前日志模式
        }
    }

    /// <summary>把底层 SQLite 异常翻译成用户能理解的中文（供界面统一提示）。</summary>
    public static string FriendlyMessage(Exception ex)
    {
        if (ex is SqliteException se)
        {
            return se.SqliteErrorCode switch
            {
                5 or 6 or 8 => "账套正被其他程序占用或文件不可写。请关闭同一账套的其他窗口后重试；若持续出现，请检查账套文件是否被设为只读。",
                11 or 26 => "账套数据文件无法识别或已损坏，请从备份恢复后重试。",
                13 => "磁盘空间不足，账套写入失败。请清理磁盘后重试。",
                _ => ex.Message,
            };
        }
        return ex.Message;
    }

    /// <summary>建库/升级。升级前自动生成一致性备份。</summary>
    public void EnsureCreated(Action<string>? log = null)
    {
        var exists = File.Exists(FilePath);
        var version = exists ? GetUserVersionChecked(FilePath) : 0;

        if (exists && version > CurrentVersion)
            throw new AppException($"数据文件版本（{version}）高于当前程序（{CurrentVersion}），请使用新版程序打开。");

        if (exists && version < CurrentVersion)
        {
            var bak = BackupCore(null, $"preupgrade-v{version}-to-v{CurrentVersion}");
            log?.Invoke($"数据库升级前已备份：{bak}");
        }

        using var conn = Open();
        ConfigureJournal(conn);
        if (version == 0)
        {
            Schema.CreateV1(conn);
            Schema.Seed(conn);
            using (var baseVersion = conn.CreateCommand())
            {
                baseVersion.CommandText = "PRAGMA user_version=1;";
                baseVersion.ExecuteNonQuery();
            }
            if (CurrentVersion > 1) Schema.Migrate(conn, 1, CurrentVersion);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA user_version={CurrentVersion};";
                cmd.ExecuteNonQuery();
            }
            log?.Invoke("已创建新账套数据库：" + FilePath);
        }
        else if (version < CurrentVersion)
        {
            Schema.Migrate(conn, version, CurrentVersion);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA user_version={CurrentVersion};";
            cmd.ExecuteNonQuery();
            log?.Invoke($"数据库已从 v{version} 升级到 v{CurrentVersion}。");
        }
    }

    /// <summary>使用 SQLite 在线备份 API，WAL 下同样保证一致性。</summary>
    public string BackupTo(string? folder = null, string? tag = null)
    {
        RequireAccess(Perm.Backup);
        return BackupCore(folder, tag);
    }

    private string BackupCore(string? folder = null, string? tag = null)
    {
        folder ??= BackupDir;
        Directory.CreateDirectory(folder);
        var name = $"cashier-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}{(string.IsNullOrEmpty(tag) ? "" : "-" + tag)}.db";
        var path = Path.Combine(folder, name);
        using var src = Open();
        using var dst = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        dst.Open();
        src.BackupDatabase(dst);
        dst.Close();
        return path;
    }

    /// <summary>恢复前先把当前库另存，再整体替换，失败可回退。</summary>
    private string RestoreValidatedFrom(string backupFile, Action<string>? log = null)
    {
        if (!File.Exists(backupFile)) throw new AppException("备份文件不存在：" + backupFile);

        // 校验备份文件可用性与版本
        using (var check = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backupFile,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString()))
        {
            try
            {
                check.Open();
                using var cmd = check.CreateCommand();
                cmd.CommandText = "PRAGMA integrity_check;";
                var r = cmd.ExecuteScalar()?.ToString();
                if (!string.Equals(r, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new AppException("备份文件完整性校验失败：" + r);

                cmd.CommandText = "PRAGMA user_version;";
                var v = Convert.ToInt32(cmd.ExecuteScalar());
                if (v < 1) throw new AppException("该文件不是可识别的出纳账套备份。");
                if (v > CurrentVersion)
                    throw new AppException($"备份文件版本（{v}）高于当前程序，无法恢复。");
                string[] tables = { "meta", "users", "audit_log", "accounts", "subjects", "partners", "depts", "projects", "handlers", "vouchers", "settlements", "invoices", "invoice_links", "periods", "import_batches", "settings" };
                if (v >= 2) tables = tables.Concat(new[] { "settlement_history", "invoice_link_history" }).ToArray();
                foreach (var table in tables)
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name";
                    cmd.Parameters.Clear();
                    cmd.Parameters.AddWithValue("$name", table);
                    if (Convert.ToInt32(cmd.ExecuteScalar()) != 1) throw new AppException("备份文件缺少必要的账套数据表：" + table);
                }
                foreach (var (table, column) in new[] { ("vouchers", "amount_cents"), ("invoices", "gross_cents"), ("settlements", "amount_cents"), ("users", "username") })
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name=$column";
                    cmd.Parameters.Clear();
                    cmd.Parameters.AddWithValue("$table", table);
                    cmd.Parameters.AddWithValue("$column", column);
                    if (Convert.ToInt32(cmd.ExecuteScalar()) != 1) throw new AppException("备份文件账套结构不完整：" + table + "." + column);
                }
            }
            catch (SqliteException)
            {
                throw new AppException("备份文件无法识别：不是有效的 SQLite 数据库文件。");
            }
        }

        var safety = BackupCore(null, "before-restore");
        log?.Invoke("恢复前已备份当前账套：" + safety);

        // 通过在线备份 API 从备份文件回灌到当前库，避免文件级替换的半途损坏
        using (var src = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backupFile,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString()))
        using (var dst = Open())
        {
            src.Open();
            src.BackupDatabase(dst);
        }

        EnsureCreated(log);
        return safety;
    }
}

public static class AppSession
{
    public static User? Current { get; set; }
    public static Database Db { get; set; } = null!;

    public static string UserName => Current?.DisplayName ?? "系统";
    public static UserRole Role => Current?.Role ?? UserRole.Viewer;

    public static void Require(string perm, SqliteConnection? conn = null, SqliteTransaction? tx = null)
    {
        if (Current == null) throw new AppException("未登录或登录已失效，请重新登录。");
        if (conn == null)
        {
            using var opened = Db.Open();
            Require(perm, opened);
            return;
        }
        ValidateCurrent(conn, tx);
        if (!Perm.Has(Role, perm))
            throw new AppException("当前账号没有执行该操作的权限。");
    }

    internal static void ValidateCurrent(SqliteConnection conn, SqliteTransaction? tx = null)
    {
        var me = Current ?? throw new AppException("未登录或登录已失效，请重新登录。");
        var user = new SqlCmd(conn, "SELECT * FROM users WHERE id=$i", tx).P("$i", me.Id)
            .First(Cashier.Core.Services.AuthService.MapUser);
        var epoch = new SqlCmd(conn, "SELECT v FROM meta WHERE k='session.epoch'", tx).Str();
        if (user == null || !user.IsActive || user.Username != me.Username || user.Role != me.Role ||
            user.AuthRevision != me.AuthRevision || string.IsNullOrEmpty(me.SessionEpoch) || me.SessionEpoch != epoch ||
            !string.Equals(me.SessionLedgerPath, Db.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            Current = null;
            throw new AppException("账号已停用、权限已变化或账套已恢复，原登录已失效，请重新登录。");
        }
    }

    public static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    public static string Today() => DateTime.Today.ToString("yyyy-MM-dd");
    public static string PeriodOf(string date) => date.Length >= 7 ? date[..4] + date.Substring(5, 2) : DateTime.Today.ToString("yyyyMM");
}
