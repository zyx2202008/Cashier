using Microsoft.Data.Sqlite;

namespace Cashier.Core.Services;

/// <summary>登录、用户与角色。</summary>
public class AuthService
{
    public User Login(string username, string password)
    {
        using var conn = AppSession.Db.Open();
        var u = new SqlCmd(conn, "SELECT * FROM users WHERE username=$u AND is_active=1")
            .P("$u", username.Trim()).First(MapUser)
            ?? throw new AppException("用户名或密码不正确。");
        var row = new SqlCmd(conn, "SELECT pwd_hash, salt FROM users WHERE id=$i")
            .P("$i", u.Id)
            .List(x => (h: R.S(x, "pwd_hash"), s: R.S(x, "salt")))
            .First();
        if (!PasswordHasher.Verify(password, row.h, row.s)) throw new AppException("用户名或密码不正确。");
        u.SessionEpoch = new SqlCmd(conn, "SELECT v FROM meta WHERE k='session.epoch'").Str();
        u.SessionLedgerPath = AppSession.Db.FilePath;
        AppSession.Current = u;
        AuditLogService.Write(conn, null, "login", "user", u.Id, null, null, null, null);
        return u;
    }

    public List<User> List()
    {
        using var conn = AppSession.Db.Open();
        using var cmd = new SqlCmd(conn, "SELECT * FROM users ORDER BY id");
        return cmd.List(MapUser);
    }

    public long Create(string username, string displayName, string password, UserRole role)
    {
        AppSession.Require(Perm.Users);
        if (string.IsNullOrWhiteSpace(username)) throw new AppException("请填写登录名。");
        if (string.IsNullOrWhiteSpace(displayName)) throw new AppException("请填写姓名。");
        if (password == null || password.Length < 6) throw new AppException("密码至少 6 位。");
        var (hash, salt) = PasswordHasher.Hash(password);
        using var conn = AppSession.Db.Open();
        try
        {
            var id = new SqlCmd(conn, @"INSERT INTO users(username,display_name,pwd_hash,salt,role,is_active,created_at)
                VALUES($u,$d,$h,$s,$r,1,$t)")
                .P("$u", username.Trim()).P("$d", displayName.Trim()).P("$h", hash).P("$s", salt)
                .P("$r", role.ToString().ToLower()).P("$t", SqlUtil.Now()).Exec();
            var newId = new SqlCmd(conn, "SELECT last_insert_rowid()").Long();
            AuditLogService.Write(conn, null, "create", "user", newId, username, null, $"{displayName}/{role}", null);
            return newId;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new AppException("登录名已存在：" + username);
        }
    }

    public void Update(long id, string displayName, UserRole role, bool active)
    {
        AppSession.Require(Perm.Users);
        using var conn = AppSession.Db.Open();
        var before = Get(conn, id);
        if (before == null) throw new AppException("用户不存在。");
        if (id == AppSession.Current?.Id && !active) throw new AppException("不能停用当前登录账号。");
        new SqlCmd(conn, "UPDATE users SET display_name=$d, auth_revision=auth_revision+CASE WHEN role<>$r OR is_active<>$a THEN 1 ELSE 0 END, role=$r, is_active=$a WHERE id=$i")
            .P("$d", displayName).P("$r", role.ToString().ToLower()).P("$a", active ? 1 : 0).P("$i", id).Exec();
        AuditLogService.Write(conn, null, "update", "user", id, before.Username, $"{before.DisplayName}/{before.RoleLabel}/{before.IsActive}",
            $"{displayName}/{role}/{active}", null);
    }

    public void ResetPassword(long id, string password)
    {
        AppSession.Require(Perm.Users);
        if (password == null || password.Length < 6) throw new AppException("密码至少 6 位。");
        var (hash, salt) = PasswordHasher.Hash(password);
        using var conn = AppSession.Db.Open();
        new SqlCmd(conn, "UPDATE users SET pwd_hash=$h, salt=$s, auth_revision=auth_revision+1 WHERE id=$i")
            .P("$h", hash).P("$s", salt).P("$i", id).Exec();
        AuditLogService.Write(conn, null, "reset-password", "user", id, null, null, null, null);
    }

    public void ChangeOwnPassword(string oldPwd, string newPwd)
    {
        var me = AppSession.Current ?? throw new AppException("未登录。");
        if (string.IsNullOrWhiteSpace(oldPwd)) throw new AppException("请填写原密码。");
        if (string.IsNullOrWhiteSpace(newPwd)) throw new AppException("请填写新密码。");
        if (newPwd.Length < 6) throw new AppException("新密码至少 6 位。");
        using var conn = AppSession.Db.Open();
        AppSession.ValidateCurrent(conn);
        var row = new SqlCmd(conn, "SELECT pwd_hash, salt FROM users WHERE id=$i").P("$i", me.Id)
            .List(x => (h: R.S(x, "pwd_hash"), s: R.S(x, "salt"))).First();
        if (!PasswordHasher.Verify(oldPwd, row.h, row.s)) throw new AppException("原密码不正确。");
        var (hash, salt) = PasswordHasher.Hash(newPwd);
        new SqlCmd(conn, "UPDATE users SET pwd_hash=$h, salt=$s, auth_revision=auth_revision+1 WHERE id=$i").P("$h", hash).P("$s", salt).P("$i", me.Id).Exec();
        me.AuthRevision++;
        AuditLogService.Write(conn, null, "change-password", "user", me.Id, null, null, null, null);
    }

    private static User? Get(SqliteConnection conn, long id) =>
        new SqlCmd(conn, "SELECT * FROM users WHERE id=$i").P("$i", id).First(MapUser);

    public static User MapUser(SqliteDataReader r) => new()
    {
        Id = R.L(r, "id"),
        AuthRevision = R.L(r, "auth_revision"),
        Username = R.S(r, "username"),
        DisplayName = R.S(r, "display_name"),
        Role = Enum.TryParse<UserRole>(R.S(r, "role"), true, out var role) ? role : UserRole.Viewer,
        IsActive = R.B(r, "is_active"),
        CreatedAt = R.S(r, "created_at"),
    };
}

public static class AuditLogService
{
    public static void Write(SqliteConnection conn, SqliteTransaction? tx, string action, string entity,
        long? entityId, string? docNo, string? before, string? after, string? reason)
    {
        new SqlCmd(conn, @"INSERT INTO audit_log(ts,user_id,username,action,entity,entity_id,doc_no,before_json,after_json,reason)
            VALUES($t,$uid,$un,$a,$e,$eid,$d,$b,$af,$r)", tx)
            .P("$t", SqlUtil.Now())
            .P("$uid", AppSession.Current?.Id)
            .P("$un", AppSession.UserName)
            .P("$a", action).P("$e", entity).P("$eid", entityId).P("$d", docNo)
            .P("$b", before).P("$af", after).P("$r", reason)
            .Exec();
    }

    public static List<AuditLogEntry> Query(string? keyword, string? action, string? from, string? to, int limit = 500)
    {
        using var conn = AppSession.Db.Open();
        var sql = "SELECT * FROM audit_log WHERE 1=1";
        if (!string.IsNullOrWhiteSpace(from)) sql += " AND ts >= $f";
        if (!string.IsNullOrWhiteSpace(to)) sql += " AND ts <= $t || ' 23:59:59'";
        if (!string.IsNullOrWhiteSpace(action)) sql += " AND action = $a";
        if (!string.IsNullOrWhiteSpace(keyword)) sql += " AND (doc_no LIKE $k OR username LIKE $k OR entity LIKE $k)";
        sql += " ORDER BY id DESC LIMIT " + Math.Clamp(limit, 1, 5000);
        using var cmd = new SqlCmd(conn, sql);
        if (!string.IsNullOrWhiteSpace(from)) cmd.P("$f", from);
        if (!string.IsNullOrWhiteSpace(to)) cmd.P("$t", to);
        if (!string.IsNullOrWhiteSpace(action)) cmd.P("$a", action);
        if (!string.IsNullOrWhiteSpace(keyword)) cmd.P("$k", "%" + keyword + "%");
        return cmd.List(r => new AuditLogEntry
        {
            Id = R.L(r, "id"),
            Ts = R.S(r, "ts"),
            Username = R.S(r, "username"),
            Action = R.S(r, "action"),
            Entity = R.S(r, "entity"),
            EntityId = R.LN(r, "entity_id"),
            DocNo = R.SN(r, "doc_no"),
            BeforeJson = R.SN(r, "before_json"),
            AfterJson = R.SN(r, "after_json"),
            Reason = R.SN(r, "reason"),
        });
    }
}

public static class SettingsService
{
    public static string Get(string key, string def = "")
    {
        using var conn = AppSession.Db.Open();
        var v = new SqlCmd(conn, "SELECT v FROM settings WHERE k=$k").P("$k", key).Str();
        return v ?? def;
    }

    public static void Set(string key, string value)
    {
        using var conn = AppSession.Db.Open();
        new SqlCmd(conn, "INSERT INTO settings(k,v) VALUES($k,$v) ON CONFLICT(k) DO UPDATE SET v=$v")
            .P("$k", key).P("$v", value).Exec();
    }

    public static string BookName => Get("book.name", "默认账套");
    public static int PageSize => int.TryParse(Get("ui.pageSize", "50"), out var n) ? Math.Clamp(n, 10, 500) : 50;
}

public static class PeriodService
{
    public static void Ensure(string period)
    {
        using var conn = AppSession.Db.Open();
        new SqlCmd(conn, "INSERT OR IGNORE INTO periods(period,status) VALUES($p,'open')").P("$p", period).Exec();
    }

    public static bool IsClosed(string period)
    {
        using var conn = AppSession.Db.Open();
        var s = new SqlCmd(conn, "SELECT status FROM periods WHERE period=$p").P("$p", period).Str();
        return s == "closed";
    }

    public static void AssertOpen(string period, string what = "该期间")
    {
        if (IsClosed(period)) throw new AppException($"{period} 期间已关闭，{what}不允许操作。请先由管理员重新开启期间。");
    }

    public static List<PeriodInfo> List()
    {
        using var conn = AppSession.Db.Open();
        return new SqlCmd(conn, "SELECT * FROM periods ORDER BY period DESC")
            .List(r => new PeriodInfo
            {
                Period = R.S(r, "period"),
                Status = R.S(r, "status"),
                ClosedAt = R.SN(r, "closed_at"),
                ClosedBy = R.SN(r, "closed_by"),
            });
    }

    public static void Close(string period)
    {
        AppSession.Require(Perm.PeriodClose);
        using var conn = AppSession.Db.Open();
        new SqlCmd(conn, "INSERT INTO periods(period,status) VALUES($p,'open') ON CONFLICT(period) DO NOTHING").P("$p", period).Exec();
        new SqlCmd(conn, "UPDATE periods SET status='closed', closed_at=$t, closed_by=$u WHERE period=$p")
            .P("$t", SqlUtil.Now()).P("$u", AppSession.UserName).P("$p", period).Exec();
        AuditLogService.Write(conn, null, "period-close", "period", null, period, null, "closed", null);
    }

    public static void Reopen(string period, string reason)
    {
        AppSession.Require(Perm.PeriodClose);
        if (string.IsNullOrWhiteSpace(reason)) throw new AppException("重新开启期间必须填写原因。");
        using var conn = AppSession.Db.Open();
        new SqlCmd(conn, "UPDATE periods SET status='open', closed_at=NULL, closed_by=NULL WHERE period=$p").P("$p", period).Exec();
        AuditLogService.Write(conn, null, "period-reopen", "period", null, period, "closed", "open", reason);
    }
}
