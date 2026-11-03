using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cashier.Core.Services;

public sealed record InvoiceCustomerInput(string Name, string? Contact = null, string? Phone = null, string? Address = null);
public sealed record CustomerCandidate(long Id, string Code, string Name, string Kind, bool IsActive,
    string? Contact, string? Phone, string? Address)
{
    public bool CanSelect => IsActive && Kind is "customer" or "both";
    public string Display => $"{Code}  {Name} · {(Kind == "customer" ? "客户" : Kind == "both" ? "客户/供应商" : "供应商")} · {(IsActive ? "启用" : "停用")}\n联系人：{Contact ?? "—"}  电话：{Phone ?? "—"}  地址：{Address ?? "—"}";
}
public enum InvoiceCustomerOutcome { Created, Selected, ReviewRequired }
public sealed record InvoiceCustomerResult(InvoiceCustomerOutcome Outcome, long? Id, string? Code,
    IReadOnlyList<CustomerCandidate> Candidates, string Snapshot);

public static partial class MasterDataService
{
    private static void ValidatePartnerFields(Partner p)
    {
        if (string.IsNullOrWhiteSpace(p.Name)) throw new AppException("请填写名称。");
        PartnerCodeService.Prefix(p.Kind);
        if ((p.Kind == "customer" && p.OpeningPayableCents != 0) || (p.Kind == "supplier" && p.OpeningReceivableCents != 0))
            throw new AppException("期初往来余额与单位类型不匹配，同时有应收和应付请使用客户兼供应商。");
    }

    private static long InsertPartner(SqliteConnection conn, SqliteTransaction tx, Partner p)
    {
        ValidatePartnerFields(p);
        OpeningBalanceService.AssertOpeningChange(conn,tx,p.OpeningReceivableCents!=0 || p.OpeningPayableCents!=0);
        p.Code = PartnerCodeService.Allocate(conn, tx, p.Kind);
        try
        {
        new SqlCmd(conn, @"INSERT INTO partners(code,name,kind,contact,phone,address,terms,level,
            opening_receivable_cents,opening_payable_cents,is_active)
            VALUES($c,$n,$k,$ct,$ph,$ad,$tm,$lv,$or,$op,1)", tx)
            .P("$c", p.Code.Trim()).P("$n", p.Name.Trim()).P("$k", p.Kind).P("$ct", p.Contact)
            .P("$ph", p.Phone).P("$ad", p.Address).P("$tm", p.Terms).P("$lv", p.Level)
            .P("$or", p.OpeningReceivableCents).P("$op", p.OpeningPayableCents).Exec();
        }
        catch(SqliteException ex) when(IsPartnerCodeConflict(ex)) { throw new PartnerCodeConflict(ex); }
        var id = new SqlCmd(conn, "SELECT last_insert_rowid()", tx).Long();
        AuditLogService.Write(conn, tx, "create", "partner", id, p.Code, null, p.Name, null);
        return id;
    }

    private static bool IsPartnerCodeConflict(SqliteException ex) => ex.SqliteExtendedErrorCode == 2067 &&
        ex.Message.Contains("partners.code", StringComparison.OrdinalIgnoreCase);
    private sealed class PartnerCodeConflict(SqliteException inner):Exception("客户编码冲突",inner);

    private static string? OptionalCustomerField(string? value, int max, string label)
    {
        var trimmed = value?.Trim();
        if (trimmed?.Length > max) throw new AppException($"{label}最多 {max} 个字符。");
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static string CustomerName(string? value)
    {
        var name = value?.Trim() ?? "";
        if (name.Length == 0) throw new AppException("请填写客户名称。");
        if (name.Length > 200) throw new AppException("客户名称最多 200 个字符。");
        return name;
    }

    // No balance calculation, fuzzy match or write occurs when inspecting candidates.
    private static List<CustomerCandidate> CustomerMatches(SqliteConnection conn, SqliteTransaction tx, string name) =>
        new SqlCmd(conn, "SELECT id,code,name,kind,is_active,contact,phone,address FROM partners ORDER BY id", tx)
            .List(r => new CustomerCandidate(R.L(r,"id"),R.S(r,"code"),R.S(r,"name"),R.S(r,"kind"),R.B(r,"is_active"),
                R.SN(r,"contact"),R.SN(r,"phone"),R.SN(r,"address")))
            .Where(p => string.Equals(p.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)).ToList();

    private static string CustomerSnapshot(string name, IReadOnlyList<CustomerCandidate> matches) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new { Ledger = AppSession.Db.FilePath, Name = name.ToUpperInvariant(), Candidates = matches }))));

    /// <summary>Duplicate check, allocation, insert and audit share one immediate write transaction.</summary>
    public static InvoiceCustomerResult CreateCustomerForInvoice(InvoiceCustomerInput input, string? confirmedSnapshot = null)
    {
        AppSession.Require(Perm.MasterData);
        AppSession.Require(Perm.Invoice);
        var name = CustomerName(input.Name);
        var p = new Partner { Name=name,Kind="customer",IsActive=true,Level="A",
            Contact=OptionalCustomerField(input.Contact,100,"联系人"), Phone=OptionalCustomerField(input.Phone,100,"电话"),
            Address=OptionalCustomerField(input.Address,500,"地址") };
        using var conn = AppSession.Db.Open();
        using var tx = conn.BeginTransaction(deferred: false);
        var matches = CustomerMatches(conn,tx,name);
        var snapshot = CustomerSnapshot(name,matches);
        if ((confirmedSnapshot != null && confirmedSnapshot != snapshot) || (matches.Count > 0 && confirmedSnapshot == null))
            return new(InvoiceCustomerOutcome.ReviewRequired,null,null,matches,snapshot);
        try
        {
            var id = InsertPartner(conn,tx,p);
            tx.Commit();
            return new(InvoiceCustomerOutcome.Created,id,p.Code,Array.Empty<CustomerCandidate>(),snapshot);
        }
        catch (PartnerCodeConflict) { throw new AppException("客户编码分配冲突，请刷新名录后重试。"); }
    }

    public static InvoiceCustomerResult SelectExistingInvoiceCustomer(string name, long id, string expectedSnapshot)
    {
        AppSession.Require(Perm.Invoice);
        name = CustomerName(name);
        using var conn=AppSession.Db.Open();
        using var tx=conn.BeginTransaction(deferred:false);
        var matches=CustomerMatches(conn,tx,name);
        var snapshot=CustomerSnapshot(name,matches);
        if (snapshot != expectedSnapshot) return new(InvoiceCustomerOutcome.ReviewRequired,null,null,matches,snapshot);
        var candidate=matches.SingleOrDefault(p=>p.Id==id);
        if (candidate == null || !candidate.CanSelect) throw new AppException("请选择一条启用的客户或客户兼供应商；停用单位及纯供应商不能直接选用。");
        return new(InvoiceCustomerOutcome.Selected,candidate.Id,candidate.Code,matches,snapshot);
    }
}
