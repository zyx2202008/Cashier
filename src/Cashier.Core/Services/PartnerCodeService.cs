using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Cashier.Core.Services;

/// <summary>往来单位共用编码字段；序列与资料的写入必须处于同一写事务。</summary>
internal static class PartnerCodeService
{
    private const string KeyPrefix = "partner_code_sequence_v3.";

    internal static string Prefix(string kind) => kind switch
    {
        "customer" or "both" => "KH",
        "supplier" => "GYS",
        _ => throw new AppException("请选择有效的单位类型。")
    };

    private static string Format(string prefix, long number) =>
        prefix + "-" + number.ToString("D4", CultureInfo.InvariantCulture);

    // Only strings that have the system's prefix and at least four ASCII digits
    // contribute to a high-water mark; other legacy codes are left intact.
    private static long Number(string code, string prefix)
    {
        if (!code.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase)) return 0;
        var digits = code.AsSpan(prefix.Length + 1);
        if (digits.Length < 4) return 0;
        foreach (var c in digits) if (c is < '0' or > '9') return 0;
        if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            throw new AppException($"历史编码 {code} 的序号超出可用范围，请先核对后重试升级。");
        return n;
    }

    internal static bool RequiresNewCode(Partner before, string kind)
    {
        var prefix = Prefix(kind);
        if (before.Kind == kind) return false;
        var n = Number(before.Code, prefix);
        return Prefix(before.Kind) != prefix || n == 0 || before.Code != Format(prefix, n);
    }

    internal static string Allocate(SqliteConnection conn, SqliteTransaction tx, string kind)
    {
        var prefix = Prefix(kind);
        var key = KeyPrefix + prefix;
        var raw = new SqlCmd(conn, "SELECT v FROM meta WHERE k=$k", tx).P("$k", key).Str();
        if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 0)
            throw new AppException("往来单位编码序列缺失或损坏，请检查账套。");
        string code;
        do
        {
            if (number == long.MaxValue) throw new AppException(prefix + " 编码序号已用尽。");
            code = Format(prefix, ++number);
        }
        while (new SqlCmd(conn, "SELECT COUNT(*) FROM partners WHERE code=$c COLLATE NOCASE", tx).P("$c", code).Long() != 0);
        new SqlCmd(conn, "UPDATE meta SET v=$v WHERE k=$k", tx)
            .P("$v", number.ToString(CultureInfo.InvariantCulture)).P("$k", key).Exec();
        return code;
    }

    internal static void MigrateV3(SqliteConnection conn)
    {
        // BeginTransaction(false) obtains the SQLite write reservation before the
        // version check, so two instances cannot both migrate or allocate at once.
        using var tx = conn.BeginTransaction(deferred: false);
        var version = new SqlCmd(conn, "PRAGMA user_version", tx).Long();
        if (version >= 3) { tx.Commit(); return; }
        if (version != 2) throw new AppException("账套版本不符合往来编码升级要求。");

        var partners = new SqlCmd(conn, "SELECT id,code,kind FROM partners ORDER BY id", tx)
            .List(r => (Id: R.L(r, "id"), Code: R.S(r, "code"), Kind: R.S(r, "kind")));
        var historicalCodes = new SqlCmd(conn,
            "SELECT doc_no FROM audit_log WHERE entity='partner' AND doc_no IS NOT NULL", tx)
            .List(r => R.S(r, "doc_no"));
        var preserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in partners.Where(p => p.Kind != "supplier"))
            if (!preserved.Add(p.Code))
                throw new AppException("现有客户编码存在大小写冲突，无法安全升级：" + p.Code);

        foreach (var prefix in new[] { "KH", "GYS" })
        {
            var key = KeyPrefix + prefix;
            if (new SqlCmd(conn, "SELECT COUNT(*) FROM meta WHERE k=$k", tx).P("$k", key).Long() != 0)
                throw new AppException("升级前发现已有编码序列设置，请检查账套：" + key);
            long max = 0;
            foreach (var code in partners.Select(p => p.Code).Concat(historicalCodes))
                max = Math.Max(max, Number(code, prefix));
            new SqlCmd(conn, "INSERT INTO meta(k,v) VALUES($k,$v)", tx)
                .P("$k", key).P("$v", max.ToString(CultureInfo.InvariantCulture)).Exec();
        }

        foreach (var p in partners.Where(p => p.Kind == "supplier"))
        {
            var n = Number(p.Code, "GYS");
            if (n > 0 && p.Code == Format("GYS", n) && preserved.Add(p.Code)) continue;
            var code = Allocate(conn, tx, "supplier");
            new SqlCmd(conn, "UPDATE partners SET code=$c WHERE id=$i", tx).P("$c", code).P("$i", p.Id).Exec();
            AuditLogService.Write(conn, tx, "update", "partner", p.Id, code, p.Code, code, "供应商编码升级");
        }
        new SqlCmd(conn, "PRAGMA user_version=3", tx).Exec();
        tx.Commit();
    }
}
