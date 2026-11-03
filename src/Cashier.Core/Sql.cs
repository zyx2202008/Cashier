using Microsoft.Data.Sqlite;

namespace Cashier.Core;

/// <summary>轻量 SQL 命令包装，减少样板代码。</summary>
public sealed class SqlCmd : IDisposable
{
    private readonly SqliteCommand _cmd;

    public SqlCmd(SqliteConnection conn, string sql, SqliteTransaction? tx = null)
    {
        _cmd = conn.CreateCommand();
        _cmd.CommandText = sql;
        if (tx != null) _cmd.Transaction = tx;
    }

    public SqliteParameterCollection Parameters => _cmd.Parameters;

    public SqlCmd P(string name, object? value)
    {
        _cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return this;
    }

    public int Exec() => _cmd.ExecuteNonQuery();

    public object? Scalar() => _cmd.ExecuteScalar();

    public long Long() => Convert.ToInt64(Scalar() ?? 0L);

    public string? Str() => Scalar()?.ToString();

    public List<T> List<T>(Func<SqliteDataReader, T> map)
    {
        var list = new List<T>();
        using var r = _cmd.ExecuteReader();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    public T? First<T>(Func<SqliteDataReader, T> map)
    {
        using var r = _cmd.ExecuteReader();
        return r.Read() ? map(r) : default;
    }

    public void Dispose() => _cmd.Dispose();
}

public static class R
{
    public static long L(SqliteDataReader r, string name)
    {
        var i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? 0L : r.GetInt64(i);
    }

    public static long? LN(SqliteDataReader r, string name)
    {
        var i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? null : r.GetInt64(i);
    }

    public static string S(SqliteDataReader r, string name)
    {
        var i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? "" : r.GetString(i);
    }

    public static string? SN(SqliteDataReader r, string name)
    {
        var i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    public static bool B(SqliteDataReader r, string name)
    {
        var i = r.GetOrdinal(name);
        return !r.IsDBNull(i) && r.GetInt64(i) != 0;
    }

    public static int I(SqliteDataReader r, string name)
    {
        var i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? 0 : r.GetInt32(i);
    }
}

public static class SqlUtil
{
    public static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    public static string Today() => DateTime.Today.ToString("yyyy-MM-dd");

    public static string PeriodOf(string date)
    {
        if (string.IsNullOrWhiteSpace(date) || date.Length < 7) throw new AppException("日期格式不正确：" + date);
        return date[..4] + date.Substring(5, 2);
    }

    public static void ValidateDate(string date, string fieldName = "日期")
    {
        if (!DateTime.TryParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)) throw new AppException($"{fieldName}格式不正确：{date}，请使用 yyyy-MM-dd。");
        if (d.Year < 1990 || d.Year > 2100) throw new AppException($"{fieldName}超出允许范围：{date}");
    }
}
