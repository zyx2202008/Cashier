using ClosedXML.Excel;

namespace Cashier.Core.Services;

public class ImportRow
{
    public int RowNo { get; set; }
    public string? Error { get; set; }
    public bool Duplicate { get; set; }
    public Voucher? Voucher { get; set; }
    public string[] Raw { get; set; } = Array.Empty<string>();
    public bool Ok => Error == null;
    public string StatusText => Error != null ? "错误" : Duplicate ? "重复（跳过）" : "可导入";
}

public class ImportPreview
{
    public List<ImportRow> Rows { get; } = new();
    public string FileName { get; set; } = "";
    public VoucherKind Kind { get; set; }
    public int OkCount => Rows.Count(r => r.Ok && !r.Duplicate);
    public int ErrorCount => Rows.Count(r => !r.Ok);
    public int DuplicateCount => Rows.Count(r => r.Duplicate);
}

public class InvoiceImportRow
{
    public string OriginalInvoiceNo { get; set; } = "";
    public int RowNo { get; set; }
    public string? Error { get; set; }
    public bool Duplicate { get; set; }
    public Invoice? Invoice { get; set; }
    public string[] Raw { get; set; } = Array.Empty<string>();
    public bool Ok => Error == null;
    public string StatusText => Error != null ? "错误" : Duplicate ? "重复（跳过）" : "可导入";
}

public class InvoiceImportPreview
{
    public int UnlinkedInvoiceCount { get; internal set; }
    internal string LedgerPath { get; set; } = "";
    internal string PayloadSnapshot { get; set; } = "";
    public List<InvoiceImportRow> Rows { get; } = new();
    public string FileName { get; set; } = "";
    public InvoiceKind Kind { get; set; }
    public int OkCount => Rows.Count(r => r.Ok && !r.Duplicate);
    public int ErrorCount => Rows.Count(r => !r.Ok);
    public int DuplicateCount => Rows.Count(r => r.Duplicate);
}

public static partial class ExcelService
{
    private static readonly string[] VoucherHeaders =
    {
        "日期", "业务性质", "金额", "账户编码", "往来单位编码", "项目编码", "部门编码", "经办人", "科目编码", "摘要", "备注"
    };
    private static readonly string[] InvoiceHeaders =
    {
        "登记日期", "发票类型", "发票号码", "发票代码", "往来单位编码", "含税金额", "税率", "不含税金额", "税额", "交票日期", "原发票号码", "发票内容", "备注"
    };

    // ---------------- 导出 ----------------
    public static void ExportReport(ReportResult r, string path) => ExportReports(path, r);

    public static void ExportReports(string path, params ReportResult[] reports)
    {
        if (reports.Length == 0) throw new AppException("没有可导出的数据。");
        using var wb = new XLWorkbook();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in reports)
        {
            var name = SafeSheetName(r.Title);
            var baseName = name;
            var seq = 2;
            while (!names.Add(name)) name = SafeSheetName($"{baseName}_{seq++}");
            WriteReportSheet(wb.Worksheets.Add(name), r);
        }
        wb.SaveAs(path);
    }

    private static void WriteReportSheet(IXLWorksheet ws, ReportResult r)
    {
        ws.Cell(1, 1).Value = r.Title;
        ws.Range(1, 1, 1, Math.Max(1, r.Columns.Count)).Merge();
        ws.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(14);
        ws.Cell(2, 1).Value = "统计期间：" + r.Period;
        ws.Cell(3, 1).Value = "口径说明：" + r.Basis;
        ws.Range(2, 1, 2, Math.Max(1, r.Columns.Count)).Merge();
        ws.Range(3, 1, 3, Math.Max(1, r.Columns.Count)).Merge();
        ws.Cell(2, 1).Style.Font.SetFontSize(9).Font.SetFontColor(XLColor.FromHtml("#64748B"));

        const int headerRow = 5;
        for (var c = 0; c < r.Columns.Count; c++)
        {
            var cell = ws.Cell(headerRow, c + 1);
            cell.Value = r.Columns[c].Header;
            cell.Style.Font.SetBold();
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#F1F5F9");
            cell.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
            cell.Style.Alignment.Horizontal = r.Columns[c].Numeric ? XLAlignmentHorizontalValues.Right : XLAlignmentHorizontalValues.Left;
            ws.Column(c + 1).Width = Math.Max(10, r.Columns[c].Width / 8.5);
        }

        var row = headerRow + 1;
        foreach (var item in r.Rows)
        {
            for (var c = 0; c < r.Columns.Count; c++)
            {
                var cell = ws.Cell(row, c + 1);
                SetCellValue(cell, item.Cells.ElementAtOrDefault(c), r.Columns[c]);
            }
            row++;
        }

        if (r.TotalRow != null)
        {
            for (var c = 0; c < r.Columns.Count; c++)
            {
                var cell = ws.Cell(row, c + 1);
                SetCellValue(cell, r.TotalRow.ElementAtOrDefault(c), r.Columns[c]);
                cell.Style.Font.SetBold();
                cell.Style.Border.TopBorder = XLBorderStyleValues.Thin;
            }
            row++;
        }

        if (!string.IsNullOrWhiteSpace(r.Note))
        {
            ws.Cell(row + 1, 1).Value = "备注：" + r.Note;
            ws.Range(row + 1, 1, row + 1, Math.Max(1, r.Columns.Count)).Merge();
            ws.Cell(row + 1, 1).Style.Font.SetFontSize(9).Font.SetFontColor(XLColor.FromHtml("#64748B"));
        }

        ws.SheetView.FreezeRows(headerRow);
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.FitToPages(1, 0);
    }

    private static void SetCellValue(IXLCell cell, object? v, ReportColumn col)
    {
        if (v == null) { cell.Value = ""; return; }
        if (col.Format == "money" && v is long cents)
        {
            cell.Value = cents / 100m;
            cell.Style.NumberFormat.Format = "#,##0.00;[Red]-#,##0.00";
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        }
        else if (col.Format == "percent" && v is decimal d)
        {
            cell.Value = d;
            cell.Style.NumberFormat.Format = "0.0%";
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        }
        else if (v is long l)
        {
            cell.Value = l;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        }
        else
        {
            cell.Value = v.ToString();
            if (col.Numeric) cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        }
    }

    public static void ExportTable(string title, IEnumerable<string> headers, IEnumerable<object?[]> rows,
        object?[]? totalRow, string path, string? note = null)
    {
        var r = new ReportResult
        {
            Title = title,
            Period = "",
            Basis = note ?? "",
            Columns = headers.Select(h => new ReportColumn { Header = h, Width = 120 }).ToList(),
            Note = note ?? "",
        };
        foreach (var x in rows) r.Rows.Add(new ReportRow { Cells = x });
        r.TotalRow = totalRow;
        ExportReport(r, path);
    }

    // ---------------- 模板 ----------------
    public static void CreateVoucherTemplate(VoucherKind kind, string path)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("导入模板");
        for (var i = 0; i < VoucherHeaders.Length; i++)
        {
            var c = ws.Cell(1, i + 1);
            c.Value = VoucherHeaders[i];
            c.Style.Font.SetBold();
            c.Style.Fill.BackgroundColor = XLColor.FromHtml("#F1F5F9");
            ws.Column(i + 1).Width = i == 9 ? 32 : 16;
        }
        var isIncome = kind == VoucherKind.Income;
        var natures = isIncome ? NatureInfo.IncomeNatures : NatureInfo.ExpenseNatures;
        ws.Cell(2, 1).Value = DateTime.Today.ToString("yyyy-MM-dd");
        ws.Cell(2, 2).Value = isIncome ? "SaleCash" : "PurchaseCash";
        ws.Cell(2, 3).Value = 1000.00;
        ws.Cell(2, 10).Value = "示例：请删除本行后填写真实数据";
        ws.Cell(2, 3).Style.NumberFormat.Format = "#,##0.00";

        var help = wb.Worksheets.Add("填写说明");
        var lines = new[]
        {
            $"1. 本模板用于导入{(isIncome ? "收入" : "支出")}单据，导入后状态为“待审核”，需在软件内审核后才影响账务。",
            "2. 日期格式：yyyy-MM-dd 或 Excel 日期单元格。",
            "3. 金额单位为元，最多两位小数，必须大于 0。",
            "4. 业务性质可选值：" + string.Join("、", natures),
            "5. 账户编码、往来单位编码、项目编码、部门编码、科目编码必须与软件基础资料一致，可留空（账户除外）。",
            $"6. 账户编码为必填（{(isIncome ? "入账账户" : "付款账户")}）。",
            "7. 摘要必填，最长 200 字。",
            "8. 相同日期+性质+金额+摘要+往来单位的记录会被判定为重复行并跳过。",
        };
        for (var i = 0; i < lines.Length; i++) help.Cell(i + 1, 1).Value = lines[i];
        help.Column(1).Width = 110;

        var dic = wb.Worksheets.Add("可选编码");
        dic.Cell(1, 1).Value = "账户编码"; dic.Cell(1, 2).Value = "账户名称";
        dic.Cell(1, 3).Value = "往来单位编码"; dic.Cell(1, 4).Value = "往来单位名称";
        dic.Cell(1, 5).Value = "项目编码"; dic.Cell(1, 6).Value = "项目名称";
        var accs = MasterDataService.ListAccounts(false);
        var partners = MasterDataService.ListPartners(isIncome ? "customer" : "supplier", false);
        var projects = MasterDataService.ListProjects(false);
        for (var i = 0; i < Math.Max(accs.Count, Math.Max(partners.Count, projects.Count)); i++)
        {
            if (i < accs.Count) { dic.Cell(i + 2, 1).Value = accs[i].Code; dic.Cell(i + 2, 2).Value = accs[i].Name; }
            if (i < partners.Count) { dic.Cell(i + 2, 3).Value = partners[i].Code; dic.Cell(i + 2, 4).Value = partners[i].Name; }
            if (i < projects.Count) { dic.Cell(i + 2, 5).Value = projects[i].Code; dic.Cell(i + 2, 6).Value = projects[i].Name; }
        }
        for (var c = 1; c <= 6; c++) dic.Column(c).Width = 20;
        wb.SaveAs(path);
    }

    // ---------------- 导入 ----------------
    public static ImportPreview PreviewVoucherImport(VoucherKind kind, string path)
    {
        var preview = new ImportPreview { FileName = Path.GetFileName(path), Kind = kind };
        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheets.First();
        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
        if (lastRow < 2) throw new AppException("文件中没有数据行。");

        // 表头校验
        for (var i = 0; i < VoucherHeaders.Length; i++)
        {
            var h = ws.Cell(1, i + 1).GetString().Trim();
            if (!h.Contains(VoucherHeaders[i]))
                throw new AppException($"第 {i + 1} 列表头应为“{VoucherHeaders[i]}”，实际为“{h}”。请使用下载的模板。");
        }

        var accByCode = MasterDataService.ListAccounts(false).ToDictionary(a => a.Code, a => a);
        var partnerByCode = MasterDataService.ListPartners(null, false).ToDictionary(p => p.Code, p => p);
        var projByCode = MasterDataService.ListProjects(false).ToDictionary(p => p.Code, p => p);
        var deptByCode = MasterDataService.ListDepts(false).ToDictionary(d => d.Code, d => d);
        var subjByCode = MasterDataService.ListSubjects(null, false).ToDictionary(s => s.Code, s => s);
        using var conn = AppSession.Db.Open();
        var seen = new HashSet<(string date, VoucherNature nature, long amount, long? account, long? partner, string summary)>();

        for (var rowNo = 2; rowNo <= lastRow; rowNo++)
        {
            var raw = Enumerable.Range(1, VoucherHeaders.Length)
                .Select(c => ws.Cell(rowNo, c).GetString().Trim()).ToArray();
            if (raw.All(string.IsNullOrWhiteSpace)) continue;

            var item = new ImportRow { RowNo = rowNo, Raw = raw };
            try
            {
                var date = ReadDate(ws.Cell(rowNo, 1));
                if (PeriodService.IsClosed(SqlUtil.PeriodOf(date)))
                    throw new AppException($"{SqlUtil.PeriodOf(date)} 期间已关闭");
                var natureText = raw[1];
                var nature = ParseNature(natureText);
                if (nature == VoucherNature.None || !Enum.IsDefined(nature))
                    throw new AppException($"业务性质“{natureText}”无法识别");
                if (kind == VoucherKind.Income && !NatureInfo.IsInflow(nature))
                    throw new AppException($"“{natureText}”不是收入类业务性质");
                if (kind == VoucherKind.Expense && !NatureInfo.IsOutflow(nature))
                    throw new AppException($"“{natureText}”不是支出类业务性质");

                var amount = ReadMoney(ws.Cell(rowNo, 3));
                var accCode = raw[3];
                if (string.IsNullOrWhiteSpace(accCode)) throw new AppException("账户编码不能为空");
                if (!accByCode.TryGetValue(accCode, out var acc)) throw new AppException($"账户编码 {accCode} 不存在");

                Partner? partner = null;
                if (!string.IsNullOrWhiteSpace(raw[4]))
                {
                    if (!partnerByCode.TryGetValue(raw[4], out partner)) throw new AppException($"往来单位编码 {raw[4]} 不存在");
                }
                if (nature is VoucherNature.CustomerReceipt or VoucherNature.CustomerPrepay
                    or VoucherNature.SupplierPayment or VoucherNature.SupplierPrepay && partner == null)
                    throw new AppException("该业务性质必须填写往来单位编码");

                Project? project = null;
                if (!string.IsNullOrWhiteSpace(raw[5]) && !projByCode.TryGetValue(raw[5], out project))
                    throw new AppException($"项目编码 {raw[5]} 不存在");
                Dept? dept = null;
                if (!string.IsNullOrWhiteSpace(raw[6]) && !deptByCode.TryGetValue(raw[6], out dept))
                    throw new AppException($"部门编码 {raw[6]} 不存在");
                Subject? subject = null;
                if (!string.IsNullOrWhiteSpace(raw[8]) && !subjByCode.TryGetValue(raw[8], out subject))
                    throw new AppException($"科目编码 {raw[8]} 不存在");

                var summary = raw[9];
                if (string.IsNullOrWhiteSpace(summary)) throw new AppException("摘要不能为空");

                var v = new Voucher
                {
                    Kind = kind, Nature = nature, BizDate = date, AmountCents = amount,
                    AccountId = acc.Id, PartnerId = partner?.Id, ProjectId = project?.Id,
                    DeptId = dept?.Id, Handler = string.IsNullOrWhiteSpace(raw[7]) ? null : raw[7],
                    SubjectId = subject?.Id, Summary = summary, Note = raw[10],
                    CountAsRevenue = kind == VoucherKind.Income && NatureInfo.CountsAsRevenue(nature),
                    CountAsCost = kind == VoucherKind.Expense && NatureInfo.CountsAsCost(nature),
                    Invoiceable = true,
                };
                VoucherService.Validate(v);
                VoucherService.ValidateReferences(v, conn, null);
                item.Voucher = v;

                var key = (v.BizDate, v.Nature, v.AmountCents, v.AccountId, v.PartnerId, v.Summary);
                var dup = !seen.Add(key) || IsDuplicate(conn, null, v);
                if (dup) item.Duplicate = true;
            }
            catch (Exception ex)
            {
                item.Error = ex.Message;
            }
            preview.Rows.Add(item);
        }
        return preview;
    }

    public static int CommitVoucherImport(ImportPreview preview)
    {
        AppSession.Require(Perm.Import);
        var ok = preview.Rows.Where(r => r.Ok && !r.Duplicate && r.Voucher != null).ToList();
        if (ok.Count == 0) throw new AppException("没有可导入的数据行。");

        using var conn = AppSession.Db.Open();
        using var tx = conn.BeginTransaction();
        try
        {
            var svc = new VoucherService();
            var imported = 0;
            foreach (var r in ok)
            {
                if (IsDuplicate(conn, tx, r.Voucher!))
                    throw new AppException($"第 {r.RowNo} 行与已有单据重复，请重新预览。整批未导入。");
                svc.CreateInTransaction(r.Voucher!, submit: true, conn, tx);
                imported++;
            }
            new SqlCmd(conn, @"INSERT INTO import_batches(ts,file_name,kind,total_rows,ok_rows,fail_rows,user_name,detail_json)
            VALUES($t,$f,$k,$tt,$ok,$bad,$u,$d)", tx)
            .P("$t", SqlUtil.Now()).P("$f", preview.FileName)
            .P("$k", preview.Kind.ToString().ToLower())
            .P("$tt", preview.Rows.Count).P("$ok", imported).P("$bad", preview.ErrorCount)
            .P("$u", AppSession.UserName)
            .P("$d", System.Text.Json.JsonSerializer.Serialize(preview.Rows.Where(r => !r.Ok)
                .Select(r => new { row = r.RowNo, error = r.Error })))
            .Exec();
            tx.Commit();
            return imported;
        }
        catch (Exception ex)
        {
            tx.Rollback();
            throw new AppException("本次导入已全部回滚：" + ex.Message);
        }
    }

    private static bool IsDuplicate(Microsoft.Data.Sqlite.SqliteConnection conn, Microsoft.Data.Sqlite.SqliteTransaction? tx, Voucher v) =>
        new SqlCmd(conn, @"SELECT COUNT(*) FROM vouchers WHERE kind=$k AND status<>'void'
          AND biz_date=$d AND nature=$n AND amount_cents=$a AND account_id IS $acc
          AND partner_id IS $p AND summary=$s", tx)
          .P("$k", v.Kind.ToString().ToLowerInvariant()).P("$d", v.BizDate).P("$n", v.Nature.ToString())
          .P("$a", v.AmountCents).P("$acc", v.AccountId).P("$p", v.PartnerId).P("$s", v.Summary).Long() > 0;

    /// <summary>业务性质解析：先按英文名，再按中文标签（如“现收收入”），兼容用户直接填写中文。</summary>
    private static VoucherNature ParseNature(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return VoucherNature.None;
        if (Enum.TryParse<VoucherNature>(t, true, out var byName) && Enum.IsDefined(byName)) return byName;
        foreach (VoucherNature n in Enum.GetValues(typeof(VoucherNature)))
            if (n != VoucherNature.None && NatureInfo.Label(n) == t) return n;
        return VoucherNature.None;
    }

    public static void CreateInvoiceTemplate(InvoiceKind kind, string path)
    {
        InvoiceImportPermission();
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("发票导入模板");
        for (var i = 0; i < InvoiceHeaders.Length; i++)
        {
            ws.Cell(1, i + 1).Value = InvoiceHeaders[i];
            ws.Cell(1, i + 1).Style.Font.SetBold();
            ws.Cell(1, i + 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#F1F5F9");
            ws.Column(i + 1).Width = 20;
        }
        foreach (var col in new[] { 3, 4, 5, 11 }) ws.Column(col).Style.NumberFormat.Format = "@";
        foreach(var col in new[]{3,11})ws.Column(col).Width=36;
        foreach(var col in new[]{4,5})ws.Column(col).Width=26;
        foreach(var col in new[]{12,13})ws.Column(col).Width=40;
        foreach(var col in new[]{6,8,9}){ws.Column(col).Style.NumberFormat.Format="#,##0.00";ws.Column(col).Style.Alignment.Horizontal=XLAlignmentHorizontalValues.Right;}
        ws.SheetView.FreezeRows(1);
        var help = wb.Worksheets.Add("填写说明");
        var lines = new[]
        {
            $"本模板用于导入{(kind == InvoiceKind.Out ? "销项" : "进项")}发票；发票登记不影响资金余额。",
            "发票号码、发票代码、往来单位编码和原发票号码请按文本填写，保留前导零。",
            "日期使用 yyyy-MM-dd，金额单位为元、最多两位小数；税率填写百分数，例如 13 表示 13%。",
            "不含税金额与税额按票面填写，二者之和必须等于含税金额。",
            "红字发票金额使用负数，原发票号码必填；可引用已登记蓝票或同一文件中的有效蓝票，不要求蓝票排在红票前。",
            "同一文件及已有有效发票的重复号码会被标记，导入前请确认。",
            "导入按确认的有效行整批写入，任何保存失败均整批回滚。导入后可在发票界面关联业务。",
            "单文件最多 20,000 行，不支持公式，请粘贴为实际票面值。可在预览界面导出逐行校验结果。"
        };
        for (var i = 0; i < lines.Length; i++) { help.Cell(i + 1, 1).Value = lines[i];help.Cell(i+1,1).Style.Alignment.WrapText=true;help.Row(i+1).Height=42; }
        help.Column(1).Width = 110;
        var dict = wb.Worksheets.Add("往来单位编码");
        dict.Cell(1, 1).Value = "编码"; dict.Cell(1, 2).Value = "名称";
        var partners = MasterDataService.ListPartners(kind == InvoiceKind.Out ? "customer" : "supplier", false);
        for (var i = 0; i < partners.Count; i++) { dict.Cell(i + 2, 1).Value = partners[i].Code; dict.Cell(i + 2, 2).Value = partners[i].Name; }
        dict.Column(1).Width = 24; dict.Column(2).Width = 35;
        wb.SaveAs(path);
    }

    public static InvoiceImportPreview PreviewInvoiceImport(InvoiceKind kind, string path) => PreviewInvoiceBatch(kind,path);

    public static int CommitInvoiceImport(InvoiceImportPreview preview) => CommitInvoiceBatch(preview);

    private static int ReadInvoiceRate(IXLCell cell)
    {
        if (cell.DataType != XLDataType.Number)
            return InvoiceService.ParseRate(cell.GetString().Trim());

        // 必须检查实际数值，不能用四舍五入后的显示文本。
        // Excel 百分比格式的底层数值是比例，普通数字则按模板要求直接表示百分数。
        var rate = (decimal)cell.GetDouble();
        var format = cell.Style.NumberFormat;
        if (format.NumberFormatId is 9 or 10 || HasPercentFormat(format.Format)) rate *= 100m;
        return InvoiceService.ParseRate(rate.ToString(System.Globalization.CultureInfo.CurrentCulture));
    }

    private static bool HasPercentFormat(string format)
    {
        var quoted = false;
        var bracketed = false;
        for (var i = 0; i < format.Length; i++)
        {
            var c = format[i];
            if (c == '"' && !bracketed) { quoted = !quoted; continue; }
            if (quoted) continue;
            if (c == '[') { bracketed = true; continue; }
            if (c == ']') { bracketed = false; continue; }
            if (bracketed) continue;
            if (c is '\\' or '_' or '*') { i++; continue; }
            if (c == '%') return true;
        }
        return false;
    }

    private static long ReadInvoiceMoney(IXLCell cell, string name)
    {
        if (cell.IsEmpty()) throw new AppException($"{name}不能为空");
        decimal amount;
        if (cell.DataType == XLDataType.Number) amount = (decimal)cell.GetDouble();
        else if (!decimal.TryParse(cell.GetString().Trim().Replace(",", ""), out amount)) throw new AppException($"{name}格式不正确");
        if (decimal.Round(amount, 2) != amount) throw new AppException($"{name}最多保留两位小数");
        return Money.ToCents(amount);
    }

    public static List<(string ts, string file, string kind, int total, int ok, int fail, string user)> ImportHistory(int limit = 100)
    {
        using var conn = AppSession.Db.Open();
        return new SqlCmd(conn, $"SELECT * FROM import_batches ORDER BY id DESC LIMIT {limit}")
            .List(r => (R.S(r, "ts"), R.S(r, "file_name"), R.S(r, "kind"),
                (int)R.L(r, "total_rows"), (int)R.L(r, "ok_rows"), (int)R.L(r, "fail_rows"), R.S(r, "user_name")));
    }

    private static string ReadDate(IXLCell cell)
    {
        if (cell.DataType == XLDataType.DateTime) return cell.GetDateTime().ToString("yyyy-MM-dd");
        var s = cell.GetString().Trim();
        if (DateTime.TryParse(s, out var d)) return d.ToString("yyyy-MM-dd");
        throw new AppException($"日期“{s}”格式不正确，应为 yyyy-MM-dd");
    }

    private static long ReadMoney(IXLCell cell)
    {
        if (cell.DataType == XLDataType.Number)
        {
            var d = (decimal)cell.GetDouble();
            if (d <= 0) throw new AppException("金额必须大于 0");
            if (decimal.Round(d, 2) != d) throw new AppException("金额最多保留两位小数");
            return Money.ToCents(d);
        }
        var s = cell.GetString().Trim().Replace(",", "");
        if (!decimal.TryParse(s, out var v)) throw new AppException($"金额“{s}”格式不正确");
        if (v <= 0) throw new AppException("金额必须大于 0");
        if (decimal.Round(v, 2) != v) throw new AppException("金额最多保留两位小数");
        return Money.ToCents(v);
    }

    private static string SafeSheetName(string name)
    {
        var bad = new[] { ':', '\\', '/', '?', '*', '[', ']' };
        var s = new string((name ?? "").Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
        if (s.Length == 0) s = "Sheet";
        return s.Length > 30 ? s[..30] : s;
    }
}
