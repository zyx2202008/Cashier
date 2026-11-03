namespace Cashier.Core.Services;

public class ReportColumn
{
    public string Header { get; init; } = "";
    public int Width { get; init; } = 100;
    public bool Numeric { get; init; }
    public string Format { get; init; } = "text"; // text | money | date | percent
}

public class ReportRow
{
    public object?[] Cells { get; init; } = Array.Empty<object?>();
    public long? DrillId { get; init; }
    public string? DrillKind { get; init; }
}

public class ReportResult
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Period { get; set; } = "";
    public string Basis { get; set; } = "";
    public string Note { get; set; } = "";
    public List<ReportColumn> Columns { get; set; } = new();
    public List<ReportRow> Rows { get; set; } = new();
    public object?[]? TotalRow { get; set; }
    public string? DrillReportId { get; set; }
    public string? DrillFilterKind { get; set; } // partner | account
    public string? FileName { get; set; }
}

public class ReportFilter
{
    public string From { get; set; } = DateTime.Today.ToString("yyyy-MM") + "-01";
    public string To { get; set; } = DateTime.Today.ToString("yyyy-MM-dd");
    public long? PartnerId { get; set; }
    public long? ProjectId { get; set; }
    public long? AccountId { get; set; }
    public long? SubjectId { get; set; }
    public InvoiceKind InvoiceDirection { get; set; } = InvoiceKind.Out;
}

public class PeriodInvoiceCashDetail
{
    public InvoiceKind Direction { get; init; } = InvoiceKind.Out;
    public string PartnerCode { get; init; } = "";
    public string PartnerName { get; init; } = "";
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public List<Invoice> Invoices { get; init; } = new();
    public List<Voucher> Vouchers { get; init; } = new();
    public bool IsOut => Direction == InvoiceKind.Out;
    public int BlueCount => Invoices.Count(i => i.GrossCents > 0);
    public int RedCount => Invoices.Count(i => i.GrossCents < 0);
    public int NetCount => BlueCount - RedCount;
    public long GrossCents => Invoices.Sum(i => i.GrossCents);
    public long NetCents => Invoices.Sum(i => i.NetCents);
    public long TaxCents => Invoices.Sum(i => i.TaxCents);
    public long CashCents => Vouchers.Sum(v => v.AmountCents);
    public long DifferenceCents => GrossCents - CashCents;
}

