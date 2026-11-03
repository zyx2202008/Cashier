namespace Cashier.Core;

public enum VoucherKind { Income, Expense, Receivable, Payable, Transfer }

public enum VoucherStatus { Draft, Pending, Audited, Void }

public enum InvoiceKind { Out, In }

public enum InvoiceStatus { Valid, Void }

public enum UserRole { Admin, Accountant, Cashier, Viewer }

/// <summary>业务性质：决定单据是否影响资金、往来、收入成本发生额。</summary>
public enum VoucherNature
{
    None = 0,
    // 收入类
    SaleCash = 1,        // 现收收入：资金流入 + 计入收入发生额 + 可开票
    CustomerReceipt = 2, // 客户回款：资金流入 + 冲减应收，不计收入
    CustomerPrepay = 3,  // 客户预收：资金流入 + 形成预收，不计收入
    SupplierRefund = 4,  // 供应商退回：资金流入 + 冲减应付，不计收入
    OtherIn = 5,         // 其他收入：资金流入 + 计入收入发生额（默认不计项目）
    // 支出类
    PurchaseCash = 11,   // 现付费用：资金流出 + 计入成本费用发生额
    SupplierPayment = 12,// 供应商付款：资金流出 + 冲减应付，不计成本
    SupplierPrepay = 13, // 供应商预付：资金流出 + 形成预付，不计成本
    CustomerRefund = 14, // 退还客户：资金流出 + 增加应收/冲减预收，不计成本
    OtherOut = 15,       // 其他支出：资金流出 + 计入成本费用发生额
}

public static class NatureInfo
{
    public static bool IsInflow(VoucherNature n) =>
        n is VoucherNature.SaleCash or VoucherNature.CustomerReceipt or VoucherNature.CustomerPrepay
          or VoucherNature.SupplierRefund or VoucherNature.OtherIn;

    public static bool IsOutflow(VoucherNature n) =>
        n is VoucherNature.PurchaseCash or VoucherNature.SupplierPayment or VoucherNature.SupplierPrepay
          or VoucherNature.CustomerRefund or VoucherNature.OtherOut;

    /// <summary>计入业务收入发生额（非资金口径）。</summary>
    public static bool CountsAsRevenue(VoucherNature n) => n is VoucherNature.SaleCash or VoucherNature.OtherIn;

    /// <summary>计入业务成本费用发生额。</summary>
    public static bool CountsAsCost(VoucherNature n) => n is VoucherNature.PurchaseCash or VoucherNature.OtherOut;

    public static bool AffectsReceivable(VoucherNature n) =>
        n is VoucherNature.CustomerReceipt or VoucherNature.CustomerPrepay or VoucherNature.CustomerRefund;

    public static bool AffectsPayable(VoucherNature n) =>
        n is VoucherNature.SupplierPayment or VoucherNature.SupplierPrepay or VoucherNature.SupplierRefund;

    /// <summary>可作为核销源（收付款单据）。</summary>
    public static bool IsSettleSource(VoucherNature n) =>
        n is VoucherNature.CustomerReceipt or VoucherNature.CustomerPrepay
          or VoucherNature.SupplierPayment or VoucherNature.SupplierPrepay;

    public static string Label(VoucherNature n) => n switch
    {
        VoucherNature.SaleCash => "现收收入",
        VoucherNature.CustomerReceipt => "客户回款",
        VoucherNature.CustomerPrepay => "客户预收",
        VoucherNature.SupplierRefund => "供应商退回",
        VoucherNature.OtherIn => "其他收入",
        VoucherNature.PurchaseCash => "现付费用",
        VoucherNature.SupplierPayment => "供应商付款",
        VoucherNature.SupplierPrepay => "供应商预付",
        VoucherNature.CustomerRefund => "退还客户",
        VoucherNature.OtherOut => "其他支出",
        _ => "—",
    };

    public static string[] IncomeNatures => new[] { "SaleCash", "CustomerReceipt", "CustomerPrepay", "SupplierRefund", "OtherIn" };
    public static string[] ExpenseNatures => new[] { "PurchaseCash", "SupplierPayment", "SupplierPrepay", "CustomerRefund", "OtherOut" };

    public static VoucherNature Parse(string? s) =>
        Enum.TryParse<VoucherNature>(s, true, out var v) ? v : VoucherNature.None;
}

public static class KindInfo
{
    public static string Label(VoucherKind k) => k switch
    {
        VoucherKind.Income => "收入",
        VoucherKind.Expense => "支出",
        VoucherKind.Receivable => "应收",
        VoucherKind.Payable => "应付",
        VoucherKind.Transfer => "内部转账",
        _ => "",
    };

    public static string Prefix(VoucherKind k) => k switch
    {
        VoucherKind.Income => "SK",
        VoucherKind.Expense => "ZC",
        VoucherKind.Receivable => "YS",
        VoucherKind.Payable => "YF",
        VoucherKind.Transfer => "ZZ",
        _ => "DJ",
    };

    public static string StatusLabel(VoucherStatus s) => s switch
    {
        VoucherStatus.Draft => "草稿",
        VoucherStatus.Pending => "待审核",
        VoucherStatus.Audited => "已审核",
        VoucherStatus.Void => "已作废",
        _ => "",
    };
}

public class Voucher
{
    public long Id { get; set; }
    public long Revision { get; set; }
    public string DocNo { get; set; } = "";
    public VoucherKind Kind { get; set; }
    public VoucherNature Nature { get; set; }
    public string BizDate { get; set; } = "";
    public string Period { get; set; } = "";
    public long AmountCents { get; set; }
    public long? AccountId { get; set; }
    public long? ToAccountId { get; set; }
    public long FeeCents { get; set; }
    public long? PartnerId { get; set; }
    public long? ProjectId { get; set; }
    public long? DeptId { get; set; }
    public string? Handler { get; set; }
    public long? SubjectId { get; set; }
    public string Summary { get; set; } = "";
    public string? Note { get; set; }
    public VoucherStatus Status { get; set; }
    public bool CountAsRevenue { get; set; }
    public bool CountAsCost { get; set; }
    public bool Invoiceable { get; set; } = true;
    public string CreatedAt { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public string? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public string? AuditedAt { get; set; }
    public string? AuditedBy { get; set; }
    public string? VoidedAt { get; set; }
    public string? VoidedBy { get; set; }
    public string? VoidReason { get; set; }
    public long? RefId { get; set; }

    // 展示用（联表填充）
    public string AccountName { get; set; } = "";
    public string ToAccountName { get; set; } = "";
    public string PartnerName { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string DeptName { get; set; } = "";
    public string SubjectName { get; set; } = "";
    public long SettledCents { get; set; }   // 已核销（仅应收/应付）
    public long InvoiceLinkedCents { get; set; } // 已开票关联（仅应收）
    public long UnsettledCents => Math.Max(0, AmountCents - SettledCents);
    public long UninvoicedCents => Math.Max(0, AmountCents - InvoiceLinkedCents);
    public decimal Amount => Money.FromCents(AmountCents);

    // 界面显示用
    public string AmountText => Money.Format(AmountCents);
    public string InText => Kind == VoucherKind.Income ? Money.Format(AmountCents) : "";
    public string OutText => Kind == VoucherKind.Expense ? Money.Format(AmountCents) : "";
    public string FeeText => Money.Format(FeeCents);
    public string SettledText => Money.Format(SettledCents);
    public string UnsettledText => Money.Format(UnsettledCents);
    // A settlement is a real allocation, not merely another payment by the same partner.
    public string SettlementStatusText => Kind is not (VoucherKind.Receivable or VoucherKind.Payable) || Status != VoucherStatus.Audited
        ? "—" : SettledCents <= 0 ? "未结清" : SettledCents < AmountCents ? "部分结清" : "已结清";
    public string InvoiceLinkedText => Money.Format(InvoiceLinkedCents);
    public string UninvoicedText => Money.Format(UninvoicedCents);
    public string StatusText => KindInfo.StatusLabel(Status);
    public string KindText => KindInfo.Label(Kind);
    public string NatureText => NatureInfo.Label(Nature);
    public string AccountText => Kind == VoucherKind.Transfer ? $"{AccountName} → {ToAccountName}" : AccountName;
    public string PartnerText => string.IsNullOrEmpty(PartnerName) ? "—" : PartnerName;
    public string ProjectText => string.IsNullOrEmpty(ProjectName) ? "—" : ProjectName;
    public string HandlerText => string.IsNullOrEmpty(Handler) ? "—" : Handler!;
    public string CreatedText => CreatedAt;
}

public class Account
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "bank";
    public string? BankTail { get; set; }
    public string? SubjectCode { get; set; }
    public long OpeningCents { get; set; }
    public bool IsActive { get; set; } = true;
    public long BalanceCents { get; set; }  // 计算值
    public string KindLabel => Kind == "cash" ? "现金" : "银行";
    public string OpeningText => Money.Format(OpeningCents);
    public string BalanceText => Money.Format(BalanceCents);
    public string ActiveText => IsActive ? "启用" : "停用";
    public string DisplayName => $"{Name}（{Code}）";
}

public class Subject
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Direction { get; set; } = "in";
    public bool IsActive { get; set; } = true;
    public string DirectionLabel => Direction == "in" ? "收入" : "支出";
    public string ActiveText => IsActive ? "启用" : "停用";
    public string DisplayName => $"{Name}（{Code}）";
}

public class Partner
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "customer";
    public string? Contact { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? Terms { get; set; }
    public string? Level { get; set; }
    public long OpeningReceivableCents { get; set; }
    public long OpeningPayableCents { get; set; }
    public bool IsActive { get; set; } = true;

    // 计算值
    public long ReceivableCents { get; set; }
    public long ReceivedCents { get; set; }
    public long InvoicedCents { get; set; }
    public long NetReceivableCents { get; set; }
    public long NetPayableCents { get; set; }
    public long PayableCents { get; set; }
    public long PaidCents { get; set; }
    public string KindLabel => Kind switch { "customer" => "客户", "supplier" => "供应商", _ => "客户/供应商" };
    public string NetReceivableText => Money.FormatSigned(NetReceivableCents);
    public string NetPayableText => Money.FormatSigned(NetPayableCents);
    public string OpeningReceivableText => Money.Format(OpeningReceivableCents);
    public string OpeningPayableText => Money.Format(OpeningPayableCents);
    public string ReceivableText => Money.Format(ReceivableCents);
    public string ReceivedText => Money.Format(ReceivedCents);
    public string InvoicedText => Money.Format(InvoicedCents);
    public string PayableText => Money.Format(PayableCents);
    public string PaidText => Money.Format(PaidCents);
    public string ActiveText => IsActive ? "启用" : "停用";
    public string DisplayName => $"{Name}（{Code}）";
    public string ReceivableStateText => NetReceivableCents < 0 ? "预收" : NetReceivableCents == 0 ? "已结清" : "待收款";
    public string PayableStateText => NetPayableCents < 0 ? "预付" : NetPayableCents == 0 ? "已结清" : "待付款";
}

public class Project
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public long? DeptId { get; set; }
    public string? Owner { get; set; }
    public bool IsActive { get; set; } = true;
    public string DeptName { get; set; } = "";
    public string ActiveText => IsActive ? "启用" : "停用";
    public string DisplayName => $"{Name}（{Code}）";
}

public class Dept
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public string ActiveText => IsActive ? "启用" : "停用";
    public string DisplayName => $"{Name}（{Code}）";
}

public class Handler
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public string ActiveText => IsActive ? "启用" : "停用";
}

public class Invoice
{
    public long Id { get; set; }
    public long Revision { get; set; }
    /// <summary>录入渠道显式启用保守自动匹配；旧调用保持原有手工关联语义。</summary>
    public bool AutoLink { get; set; }
    public InvoiceKind Kind { get; set; }
    public string InvoiceType { get; set; } = "";
    public string InvoiceNo { get; set; } = "";
    public string? InvoiceCode { get; set; }
    public long? OriginalInvoiceId { get; set; }
    public long PartnerId { get; set; }
    public string RegDate { get; set; } = "";
    public string? DeliverDate { get; set; }
    public long GrossCents { get; set; }
    public long NetCents { get; set; }
    public long TaxCents { get; set; }
    public int RateBp { get; set; }
    public string? Content { get; set; }
    public string? Note { get; set; }
    public InvoiceStatus Status { get; set; }
    public string CreatedAt { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public string? VoidedAt { get; set; }
    public string? VoidReason { get; set; }
    public string PartnerName { get; set; } = "";
    public long LinkedCents { get; set; }
    public string KindLabel => Kind == InvoiceKind.Out ? "销项" : "进项";
    public string StatusLabel => Status == InvoiceStatus.Valid ? "有效" : "已作废";
    public string RateLabel => (RateBp / 100m).ToString("0.##") + "%";
    public string GrossText => Money.Format(GrossCents);
    public string NetText => Money.Format(NetCents);
    public string TaxText => Money.Format(TaxCents);
    public string LinkedText => Money.Format(LinkedCents);
    public string RegDateText => RegDate;
    public string DeliverDateText => string.IsNullOrEmpty(DeliverDate) ? "—" : DeliverDate!;
    public string NoText => InvoiceNo;
    public string CodeText => string.IsNullOrEmpty(InvoiceCode) ? "—" : InvoiceCode!;
    public string TypeShort => InvoiceType;
}

public class Settlement
{
    public long Id { get; set; }
    public long PayVoucherId { get; set; }
    public long BizVoucherId { get; set; }
    public long AmountCents { get; set; }
    public string CreatedAt { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public string PayDocNo { get; set; } = "";
    public string BizDocNo { get; set; } = "";
    public string BizDate { get; set; } = "";
    public string BizSummary { get; set; } = "";
}

public class User
{
    internal string? SessionEpoch { get; set; }
    internal string? SessionLedgerPath { get; set; }
    internal long AuthRevision { get; set; }
    public long Id { get; set; }
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public UserRole Role { get; set; }
    public bool IsActive { get; set; } = true;
    public string CreatedAt { get; set; } = "";
    public string RoleLabel => Role switch
    {
        UserRole.Admin => "管理员",
        UserRole.Accountant => "会计",
        UserRole.Cashier => "出纳",
        UserRole.Viewer => "只读",
        _ => "",
    };
}

public class PeriodInfo
{
    public string Period { get; set; } = "";
    public string Status { get; set; } = "open";
    public string? ClosedAt { get; set; }
    public string? ClosedBy { get; set; }
    public bool IsClosed => Status == "closed";
}

public class AuditLogEntry
{
    public long Id { get; set; }
    public string Ts { get; set; } = "";
    public string Username { get; set; } = "";
    public string Action { get; set; } = "";
    public string Entity { get; set; } = "";
    public long? EntityId { get; set; }
    public string? DocNo { get; set; }
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
    public string? Reason { get; set; }
}

public static class Perm
{
    public const string CreateVoucher = "voucher.create";
    public const string AuditVoucher = "voucher.audit";
    public const string VoidVoucher = "voucher.void";
    public const string Settle = "settle";
    public const string Invoice = "invoice";
    public const string MasterData = "master";
    public const string PeriodClose = "period.close";
    public const string Users = "users";
    public const string Backup = "backup";
    public const string Restore = "restore";
    public const string Import = "import";
    public const string OpeningLock = "opening.lock";
    public const string BalanceAdjust = "balance.adjust";

    public static bool Has(UserRole role, string perm)
    {
        if (role == UserRole.Admin) return true;
        return role switch
        {
            UserRole.Accountant => perm is CreateVoucher or AuditVoucher or VoidVoucher or Settle or Invoice
                or MasterData or PeriodClose or Backup or Import or BalanceAdjust,
            UserRole.Cashier => perm is CreateVoucher or Settle or Invoice or Import,
            UserRole.Viewer => false,
            _ => false,
        };
    }
}
