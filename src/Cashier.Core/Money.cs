using System.Globalization;

namespace Cashier.Core;

/// <summary>金额工具：全系统金额一律以“分”为整数存储与传递，decimal 仅用于展示与换算。</summary>
public static class Money
{
    /// <summary>单笔金额上限 999,999,999.99 元。</summary>
    public const long MaxCents = 99_999_999_999L;

    public static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    public static long ToCents(decimal value)
    {
        decimal rounded;
        try
        {
            rounded = decimal.Round(value * 100m, 0, MidpointRounding.AwayFromZero);
        }
        catch (OverflowException)
        {
            throw new AppException($"金额超出允许范围（上限 {Format(MaxCents)} 元）。");
        }
        if (rounded > MaxCents || rounded < -MaxCents)
            throw new AppException($"金额超出允许范围（上限 {Format(MaxCents)} 元）。");
        return (long)rounded;
    }

    public static decimal FromCents(long cents) => cents / 100m;

    public static string Format(long cents) => FromCents(cents).ToString("#,##0.00", Ci);

    public static string Format(decimal value) => value.ToString("#,##0.00", Ci);

    public static string FormatSigned(long cents) => (cents < 0 ? "−" : "") + Format(Math.Abs(cents));

    /// <summary>解析用户输入的金额文本，返回分。空或非法抛异常。</summary>
    public static long Parse(string? text, string fieldName = "金额")
    {
        if (string.IsNullOrWhiteSpace(text)) throw new AppException($"请填写{fieldName}。");
        var t = text.Trim().Replace(",", "").Replace("¥", "").Replace("￥", "").Replace(" ", "");
        if (!decimal.TryParse(t, NumberStyles.Number, Ci, out var d))
            throw new AppException($"{fieldName}格式不正确：{text}");
        if (d <= 0) throw new AppException($"{fieldName}必须大于 0。");
        if (decimal.Round(d, 2) != d) throw new AppException($"{fieldName}最多保留两位小数。");
        return ToCents(d);
    }

    /// <summary>解析可空金额（0 或空返回 0）。</summary>
    public static long ParseOrZero(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var t = text.Trim().Replace(",", "").Replace("¥", "").Replace("￥", "");
        if (!decimal.TryParse(t, NumberStyles.Number, Ci, out var d)) throw new AppException("手续费格式不正确。");
        if (d < 0) throw new AppException("手续费不能为负数。");
        if (decimal.Round(d, 2) != d) throw new AppException("手续费最多保留两位小数。");
        return ToCents(d);
    }

    /// <summary>解析可空、可正可负的金额（期初余额用：负数表示预收/预付），0 或空返回 0。</summary>
    public static long ParseSignedOrZero(string? text, string fieldName = "金额")
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var t = text.Trim().Replace(",", "").Replace("¥", "").Replace("￥", "").Replace(" ", "");
        if (!decimal.TryParse(t, NumberStyles.Number, Ci, out var d))
            throw new AppException($"{fieldName}格式不正确：{text}");
        if (decimal.Round(d, 2) != d) throw new AppException($"{fieldName}最多保留两位小数。");
        return ToCents(d);
    }

    /// <summary>含税金额按税率（万分比）拆分，返回（不含税, 税额），保证 net + tax == gross。</summary>
    public static (long net, long tax) SplitTax(long grossCents, int rateBp)
    {
        if (rateBp < 0) rateBp = 0;
        var netD = decimal.Round(grossCents * 10000m / (10000m + rateBp), 0, MidpointRounding.AwayFromZero);
        var net = (long)netD;
        return (net, grossCents - net);
    }

    /// <summary>由不含税金额与税率计算含税金额。</summary>
    public static (long gross, long tax) GrossFromNet(long netCents, int rateBp)
    {
        var taxD = decimal.Round(netCents * (decimal)rateBp / 10000m, 0, MidpointRounding.AwayFromZero);
        var tax = (long)taxD;
        return (netCents + tax, tax);
    }

    public static string Upper(decimal value) => RmbUpper.Convert(value);
}

/// <summary>人民币大写。</summary>
public static class RmbUpper
{
    private static readonly string[] Digits = { "零", "壹", "贰", "叁", "肆", "伍", "陆", "柒", "捌", "玖" };
    private static readonly string[] Units = { "", "拾", "佰", "仟" };
    private static readonly string[] BigUnits = { "", "万", "亿", "万亿" };

    public static string Convert(decimal value)
    {
        var n = decimal.Round(value, 2, MidpointRounding.AwayFromZero);
        if (n == 0) return "零元整";
        var neg = n < 0;
        n = Math.Abs(n);
        var intPart = (long)decimal.Truncate(n);
        var dec = (int)decimal.Round((n - intPart) * 100m, 0, MidpointRounding.AwayFromZero);

        var sb = new System.Text.StringBuilder();
        if (intPart == 0)
        {
            sb.Append("零");
        }
        else
        {
            var str = intPart.ToString(Money.Ci);
            var groups = new List<int>();
            while (str.Length > 0)
            {
                var len = Math.Min(4, str.Length);
                groups.Add(int.Parse(str[^len..], Money.Ci));
                str = str[..^len];
            }
            var started = false;
            for (var g = groups.Count - 1; g >= 0; g--)
            {
                var grp = groups[g];
                if (grp == 0)
                {
                    if (started && sb.Length > 0 && sb[^1] != '零') sb.Append('零');
                    continue;
                }
                var gs = GroupToChinese(grp, out var needLeadZero);
                if (started && (needLeadZero || grp < 1000) && sb.Length > 0 && sb[^1] != '零') sb.Append('零');
                sb.Append(gs);
                if (g < BigUnits.Length) sb.Append(BigUnits[g]);
                started = true;
            }
            // 去掉多余的“零”
            var s = sb.ToString().TrimEnd('零');
            sb.Clear();
            sb.Append(s);
        }

        sb.Append("元");
        if (dec == 0) sb.Append('整');
        else
        {
            var jiao = dec / 10;
            var fen = dec % 10;
            if (jiao > 0) sb.Append(Digits[jiao]).Append('角');
            else if (fen > 0) sb.Append('零');
            if (fen > 0) sb.Append(Digits[fen]).Append('分');
        }
        return (neg ? "负" : "") + sb;
    }

    private static string GroupToChinese(int grp, out bool needLeadZero)
    {
        needLeadZero = false;
        var sb = new System.Text.StringBuilder();
        var zero = false;
        var digits = grp.ToString(Money.Ci).PadLeft(4, '0');
        for (var i = 0; i < 4; i++)
        {
            var d = digits[i] - '0';
            var unit = 3 - i;
            if (d == 0)
            {
                if (sb.Length > 0) zero = true;
                if (i == 0) needLeadZero = true;
            }
            else
            {
                if (zero && sb.Length > 0) sb.Append('零');
                zero = false;
                sb.Append(Digits[d]).Append(Units[unit]);
            }
        }
        return sb.ToString();
    }
}

public class AppException : Exception
{
    public AppException(string message) : base(message) { }
}
