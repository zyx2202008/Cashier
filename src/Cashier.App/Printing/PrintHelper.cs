using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using Cashier.Core;
using Cashier.Core.Services;
using Microsoft.Win32;

namespace Cashier.App.Printing;

public class PrintTable
{
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Note { get; set; } = "";
    public List<string> Headers { get; } = new();
    public List<double> Widths { get; } = new();
    public List<bool> RightAligned { get; } = new();
    public List<string[]> Rows { get; } = new();
    public string[]? TotalRow { get; set; }
    public bool Landscape { get; set; }
}

public static class PrintHelper
{
    // ---------------- 从 DataGrid 导出 / 打印 ----------------
    public static void ExportGrid(DataGrid grid, string title, string subtitle)
    {
        var t = FromGrid(grid, title, subtitle);
        if (t.Rows.Count == 0) throw new AppException("当前没有可导出的数据。");
        var dlg = new SaveFileDialog
        {
            Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
            FileName = $"{title}_{DateTime.Now:yyyyMMddHHmm}.xlsx",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dlg.ShowDialog() != true) return;

        var res = new ReportResult { Title = title, Period = subtitle, Basis = "", Note = t.Note };
        foreach (var h in t.Headers) res.Columns.Add(new ReportColumn { Header = h, Width = 120 });
        foreach (var r in t.Rows) res.Rows.Add(new ReportRow { Cells = r.Select(x => (object?)x).ToArray() });
        if (t.TotalRow != null) res.TotalRow = t.TotalRow.Select(x => (object?)x).ToArray();
        ExcelService.ExportReport(res, dlg.FileName);
        if (!File.Exists(dlg.FileName)) throw new AppException("导出失败：文件未生成。");
        Ui.Info($"已导出 {t.Rows.Count} 行到：\n{dlg.FileName}", "导出成功");
    }

    public static void PrintGrid(DataGrid grid, string title, string subtitle)
    {
        var t = FromGrid(grid, title, subtitle);
        if (t.Rows.Count == 0) throw new AppException("当前没有可打印的数据。");
        Print(t);
    }

    public static void ExportReport(ReportResult r)
    {
        var name = r.FileName ?? $"{r.Title}_{DateTime.Now:yyyyMMddHHmm}";
        if (!name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) name += ".xlsx";
        var dlg = new SaveFileDialog
        {
            Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
            FileName = name,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dlg.ShowDialog() != true) return;
        ExcelService.ExportReport(r, dlg.FileName);
        if (!File.Exists(dlg.FileName)) throw new AppException("导出失败：文件未生成。");
        Ui.Info($"已导出 {r.Rows.Count} 行到：\n{dlg.FileName}", "导出成功");
    }

    public static void ExportReports(string fileBase, params ReportResult[] reports)
    {
        if (reports.Length == 0) throw new AppException("当前没有可导出的数据。");
        var name = fileBase;
        if (!name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) name += ".xlsx";
        var dlg = new SaveFileDialog
        {
            Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
            FileName = name,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dlg.ShowDialog() != true) return;
        ExcelService.ExportReports(dlg.FileName, reports);
        if (!File.Exists(dlg.FileName)) throw new AppException("导出失败：文件未生成。");
        Ui.Info($"已导出 {reports.Sum(r => r.Rows.Count)} 行到：\n{dlg.FileName}", "导出成功");
    }

    public static void PrintReport(ReportResult r)
    {
        if (r.Rows.Count == 0) throw new AppException("当前没有可打印的数据。");
        var t = BuildReportTable(r);
        Print(t);
    }

    public static PrintTable BuildReportTable(ReportResult r)
    {
        var t = new PrintTable { Title = r.Title, Subtitle = r.Period, Note = r.Note };
        foreach (var c in r.Columns)
        {
            t.Headers.Add(c.Header);
            t.Widths.Add(c.Width);
            t.RightAligned.Add(c.Numeric);
        }
        foreach (var row in r.Rows)
        {
            var cells = new string[r.Columns.Count];
            for (var i = 0; i < r.Columns.Count; i++)
                cells[i] = FormatCell(row.Cells.ElementAtOrDefault(i), r.Columns[i]);
            t.Rows.Add(cells);
        }
        if (r.TotalRow != null)
        {
            var cells = new string[r.Columns.Count];
            for (var i = 0; i < r.Columns.Count; i++)
                cells[i] = FormatCell(r.TotalRow.ElementAtOrDefault(i), r.Columns[i]);
            t.TotalRow = cells;
        }
        t.Landscape = r.Columns.Count > 7;
        return t;
    }

    private static string FormatCell(object? v, ReportColumn col)
    {
        if (v == null) return "";
        return col.Format switch
        {
            "money" when v is long c => Money.Format(c),
            "percent" when v is decimal d => d.ToString("0.0%"),
            _ => v.ToString() ?? "",
        };
    }

    private static PrintTable FromGrid(DataGrid grid, string title, string subtitle)
    {
        var t = new PrintTable { Title = title, Subtitle = subtitle, Landscape = grid.Columns.Count > 8 };
        var cols = grid.Columns.Where(c => c.Visibility == Visibility.Visible).ToList();
        foreach (var c in cols)
        {
            t.Headers.Add(c.Header?.ToString() ?? "");
            t.Widths.Add(c.ActualWidth > 10 ? c.ActualWidth : 110);
            t.RightAligned.Add(c is NumericColumn nc && nc.RightAligned);
        }
        foreach (var item in grid.Items)
        {
            if (item == null) continue;
            var row = new string[cols.Count];
            for (var i = 0; i < cols.Count; i++) row[i] = CellText(cols[i], item);
            t.Rows.Add(row);
        }

        // 合计行：仅对右对齐（金额）列求和
        var total = new string[cols.Count];
        var any = false;
        for (var i = 0; i < cols.Count; i++)
        {
            if (!t.RightAligned[i]) continue;
            decimal sum = 0;
            var ok = true;
            foreach (var row in t.Rows)
            {
                var s = row[i].Replace(",", "").Replace("−", "-");
                if (string.IsNullOrWhiteSpace(s)) continue;
                if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) sum += d;
                else { ok = false; break; }
            }
            if (ok) { total[i] = Money.Format(sum); any = true; }
        }
        if (any)
        {
            total[0] = "合计";
            t.TotalRow = total;
            // 1.1.2：列表导出/打印的合计是页面所有行的简单相加（含草稿、待审核与已作废单据），
            // 必须标注口径，避免与仅统计已审核的账面数字混淆。
            t.Note = string.IsNullOrWhiteSpace(t.Note)
                ? "合计为列表全部行的简单相加（含草稿、待审核与已作废单据，如有）；正式账面口径请以报表页为准。"
                : t.Note;
        }
        return t;
    }

    private static string CellText(DataGridColumn col, object item)
    {
        if (col is DataGridTextColumn tc && tc.Binding is Binding b && b.Path?.Path is string path)
        {
            var prop = item.GetType().GetProperty(path);
            return prop?.GetValue(item)?.ToString() ?? "";
        }
        return "";
    }

    // ---------------- 打印与预览 ----------------
    private static void Print(PrintTable t)
    {
        var doc = Build(t);
        var preview = new PrintPreviewWindow(doc, t.Title) { Owner = MainWindow.Instance };
        preview.ShowDialog();
    }

    public static FixedDocument Build(PrintTable t)
    {
        var doc = new FixedDocument();
        var pw = t.Landscape ? 1122.0 : 793.0;
        var ph = t.Landscape ? 793.0 : 1122.0;
        var margin = 36.0;
        var contentW = pw - margin * 2;
        var rowH = 21.0;
        var headH = 26.0;
        var firstTitleH = 62.0;
        var nextTitleH = 30.0;
        var footerH = 26.0;

        var totalW = t.Widths.Sum();
        var scale = totalW > contentW ? contentW / totalW : 1.0;
        var widths = t.Widths.Select(w => w * scale).ToList();

        var rowsPerFirst = Math.Max(1, (int)((ph - margin * 2 - firstTitleH - headH - footerH) / rowH));
        var rowsPerNext = Math.Max(1, (int)((ph - margin * 2 - nextTitleH - headH - footerH) / rowH));

        var pages = new List<List<string[]>>();
        var idx = 0;
        var first = true;
        var printableRows = t.Rows.ToList();
        if (t.TotalRow != null) printableRows.Add(t.TotalRow);
        while (idx < printableRows.Count)
        {
            var cap = first ? rowsPerFirst : rowsPerNext;
            var slice = printableRows.Skip(idx).Take(cap).ToList();
            pages.Add(slice);
            idx += cap;
            first = false;
        }
        if (pages.Count == 0) pages.Add(new List<string[]>());

        var tf = new Typeface(new FontFamily("Microsoft YaHei UI, Microsoft YaHei"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var tfBold = new Typeface(new FontFamily("Microsoft YaHei UI, Microsoft YaHei"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var ink = new SolidColorBrush(Color.FromRgb(0x17, 0x2B, 0x3A));
        var muted = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
        var line = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0));
        var brand = new SolidColorBrush(Color.FromRgb(0x0F, 0x76, 0x6E));

        for (var p = 0; p < pages.Count; p++)
        {
            var page = new FixedPage { Width = pw, Height = ph, Background = Brushes.White };
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var y = margin;
                // 标题
                if (p == 0)
                {
                    var title = new FormattedText(t.Title, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tfBold, 17, ink, 1.0)
                    { MaxTextWidth=contentW*0.55,MaxLineCount=1,Trimming=TextTrimming.CharacterEllipsis };
                    dc.DrawText(title, new Point(margin, y));
                    var sub = new FormattedText(t.Subtitle, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, 9.5, muted, 1.0)
                    { MaxTextWidth=contentW,MaxLineCount=1,Trimming=TextTrimming.CharacterEllipsis };
                    dc.DrawText(sub, new Point(margin, y + 26));
                    var right = new FormattedText($"{SettingsService.BookName}   打印时间 {DateTime.Now:yyyy-MM-dd HH:mm}",
                        CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, 9, muted, 1.0)
                    { MaxTextWidth=contentW*0.4,MaxLineCount=1,Trimming=TextTrimming.CharacterEllipsis };
                    dc.DrawText(right, new Point(pw - margin - right.Width, y + 6));
                    y += firstTitleH;
                }
                else
                {
                    var title = new FormattedText(t.Title + "（续）", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tfBold, 11, ink, 1.0);
                    dc.DrawText(title, new Point(margin, y));
                    y += nextTitleH;
                }

                // 表头
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC)), null, new Rect(margin, y, contentW, headH));
                dc.DrawLine(new Pen(line, 1), new Point(margin, y + headH), new Point(margin + contentW, y + headH));
                var x = margin;
                for (var c = 0; c < t.Headers.Count; c++)
                {
                    var ft = new FormattedText(t.Headers[c], CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tfBold, 9, muted, 1.0)
                    { MaxTextWidth = Math.Max(10, widths[c] - 8), MaxLineCount=1, Trimming = TextTrimming.CharacterEllipsis };
                    var tx = t.RightAligned[c] ? x + widths[c] - 4 - ft.Width : x + 4;
                    dc.DrawText(ft, new Point(tx, y + (headH - ft.Height) / 2));
                    x += widths[c];
                }
                y += headH;

                // 数据行
                foreach (var row in pages[p])
                {
                    var isTotal = ReferenceEquals(row, t.TotalRow);
                    x = margin;
                    for (var c = 0; c < t.Headers.Count && c < row.Length; c++)
                    {
                        var ft = new FormattedText(row[c] ?? "", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                            isTotal ? tfBold : tf, 9, ink, 1.0)
                        { MaxTextWidth = Math.Max(10, widths[c] - 8), MaxLineCount=1, Trimming = TextTrimming.CharacterEllipsis };
                        var tx = t.RightAligned[c] ? x + widths[c] - 4 - ft.Width : x + 4;
                        dc.DrawText(ft, new Point(tx, y + (rowH - ft.Height) / 2));
                        x += widths[c];
                    }
                    if (isTotal)
                        dc.DrawLine(new Pen(line, 1), new Point(margin, y), new Point(margin + contentW, y));
                    else
                        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(0xEF, 0xF2, 0xF6)), 1),
                            new Point(margin, y + rowH), new Point(margin + contentW, y + rowH));
                    y += rowH;
                }

                // 页脚
                var fy = ph - margin - footerH + 6;
                dc.DrawLine(new Pen(line, 1), new Point(margin, fy - 6), new Point(margin + contentW, fy - 6));
                var left = new FormattedText(t.Note, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, 8, muted, 1.0)
                { MaxTextWidth = contentW - 140, MaxLineCount=1, Trimming = TextTrimming.CharacterEllipsis };
                dc.DrawText(left, new Point(margin, fy));
                var pageText = new FormattedText($"第 {p + 1} 页 / 共 {pages.Count} 页", CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, tf, 8.5, muted, 1.0);
                dc.DrawText(pageText, new Point(pw - margin - pageText.Width, fy));
                dc.DrawText(new FormattedText("金额单位：元", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, 8, brand, 1.0),
                    new Point(margin, fy + 12));
            }
            page.Children.Add(new VisualHost(visual));
            var pc = new PageContent();
            ((IAddChild)pc).AddChild(page);
            doc.Pages.Add(pc);
        }
        return doc;
    }
}

/// <summary>把 DrawingVisual 挂到 FixedPage 上。</summary>
public class VisualHost : FrameworkElement
{
    private readonly Visual _child;
    public VisualHost(Visual child)
    {
        _child = child;
        AddVisualChild(child);
        AddLogicalChild(child);
    }
    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _child;
}
