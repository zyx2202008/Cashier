using Microsoft.Data.Sqlite;

namespace Cashier.Core;

public sealed partial class Database
{
    private void RequireAccess(string permission)
    {
        AppSession.Require(permission);
        if (!string.Equals(FilePath, AppSession.Db.FilePath, StringComparison.OrdinalIgnoreCase))
            throw new AppException("当前登录不属于该账套，请先打开对应账套并登录。");
    }

    /// <summary>先在独立临时库验证、升级，完成后才原子回灌；任何验证失败都不改当前账套。</summary>
    public string RestoreFrom(string backupFile, Action<string>? log = null)
    {
        RequireAccess(Perm.Restore);
        if (!File.Exists(backupFile)) throw new AppException("备份文件不存在：" + backupFile);
        var stagePath = Path.Combine(DataDir, ".restore-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = backupFile, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            using (var stage = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = stagePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
            {
                source.Open();
                stage.Open();
                source.BackupDatabase(stage);
                var version = checked((int)new SqlCmd(stage, "PRAGMA user_version").Long());
                if (version < 1) throw new AppException("该文件不是可识别的出纳账套备份。");
                if (version > CurrentVersion) throw new AppException($"备份文件版本（{version}）高于当前程序，无法恢复。");
                ValidateBackupStructure(stage, version);
                if (version < CurrentVersion) Schema.Migrate(stage, version, CurrentVersion);
                new SqlCmd(stage, "PRAGMA foreign_keys=ON; PRAGMA journal_mode=DELETE;").Exec();
                // 连同账套一起原子写入新的代次，使其他进程的旧登录也失效。
                new SqlCmd(stage, "INSERT INTO meta(k,v) VALUES('session.epoch',$e) ON CONFLICT(k) DO UPDATE SET v=$e")
                    .P("$e", Guid.NewGuid().ToString("N")).Exec();
                ValidateBackupStructure(stage, CurrentVersion);
            }
            RequireAccess(Perm.Restore);
            var safety = RestoreValidatedFrom(stagePath, log);
            AppSession.Current = null;
            return safety;
        }
        catch (SqliteException ex)
        {
            if (ex.SqliteErrorCode is 11 or 26)
                throw new AppException("备份文件无法识别：不是有效的 SQLite 数据库文件。");
            throw new AppException(Database.FriendlyMessage(ex));
        }
        finally
        {
            // 只清理本次以随机标识创建的临时文件，不移动或删除用户提供的备份。
            foreach (var path in new[] { stagePath, stagePath + "-journal", stagePath + "-wal", stagePath + "-shm" })
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void ValidateBackupStructure(SqliteConnection actual, int version)
    {
        var integrity = new SqlCmd(actual, "PRAGMA integrity_check").List(r => r.GetString(0));
        if (integrity.Count != 1 || !string.Equals(integrity[0], "ok", StringComparison.OrdinalIgnoreCase))
            throw new AppException("备份文件完整性校验失败：" + string.Join("；", integrity.Take(3)));
        using var expected = new SqliteConnection("Data Source=:memory:");
        expected.Open();
        Schema.CreateV1(expected);
        if (version > 1) Schema.Migrate(expected, 1, version);
        var tables = new SqlCmd(expected, "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'")
            .List(r => r.GetString(0));
        foreach (var table in tables)
        {
            if (new SqlCmd(actual, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$n").P("$n", table).Long() != 1)
                throw new AppException("备份文件缺少必要的账套数据表：" + table);
            var wanted = Columns(expected, table);
            var present = Columns(actual, table);
            foreach (var column in wanted)
            {
                if (!present.TryGetValue(column.Key, out var descriptor) || descriptor != column.Value)
                    throw new AppException("备份文件账套结构不完整或不匹配：" + table + "." + column.Key);
            }
        }
        // 金额、号码和修订号保护所依赖的索引与触发器同样是账套结构的一部分。
        var objects = new SqlCmd(expected, "SELECT type,name,sql FROM sqlite_master WHERE type IN ('index','trigger') AND sql IS NOT NULL")
            .List(r => (type: r.GetString(0), name: r.GetString(1), sql: NormalizeSql(r.GetString(2))));
        foreach (var item in objects)
        {
            var sql = new SqlCmd(actual, "SELECT sql FROM sqlite_master WHERE type=$t AND name=$n")
                .P("$t", item.type).P("$n", item.name).Str();
            if (sql == null || NormalizeSql(sql) != item.sql)
                throw new AppException("备份文件账套结构不完整或不匹配：" + item.name);
        }
        if (new SqlCmd(actual, "PRAGMA foreign_key_check").List(r => r.GetValue(0)).Count != 0)
            throw new AppException("备份文件存在外键关联缺失，无法恢复，请先核对备份。");
    }

    private static Dictionary<string, string> Columns(SqliteConnection conn, string table) =>
        new SqlCmd(conn, "SELECT name,type,\"notnull\",pk FROM pragma_table_info($t)").P("$t", table)
            .List(r => (name: r.GetString(0), description: r.GetString(1).ToUpperInvariant() + "/" + r.GetInt64(2) + "/" + r.GetInt64(3)))
            .ToDictionary(x => x.name, x => x.description, StringComparer.OrdinalIgnoreCase);

    private static string NormalizeSql(string sql) =>
        string.Concat(sql.Where(c => !char.IsWhiteSpace(c))).Replace("IFNOTEXISTS", "", StringComparison.OrdinalIgnoreCase)
            .TrimEnd(';').ToUpperInvariant();
}
