using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cashier.Core.Services;

public static partial class ExcelService
{
    private static string InvoiceBatchPayload(InvoiceImportPreview p)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{p.Kind,p.FileName,Rows=p.Rows.Select(r=>new{r.RowNo,r.Raw,r.Error,r.Duplicate,r.OriginalInvoiceNo,r.Invoice})}))));
    private static void InvoiceImportPermission(){AppSession.Require(Perm.Import);AppSession.Require(Perm.Invoice);}
    private static InvoiceImportPreview PreviewInvoiceBatch(InvoiceKind kind,string path)
    {
        InvoiceImportPermission();if(!Enum.IsDefined(kind))throw new AppException("发票导入类型不正确。");
        var preview=new InvoiceImportPreview{FileName=Path.GetFileName(path),Kind=kind,LedgerPath=AppSession.Db.FilePath};
        using var wb=new XLWorkbook(path);var ws=wb.Worksheets.First();
        for(int i=0;i<InvoiceHeaders.Length;i++)if(ws.Cell(1,i+1).GetString().Trim()!=InvoiceHeaders[i])throw new AppException($"第 {i+1} 列应为“{InvoiceHeaders[i]}”，请使用发票导入模板。");
        var last=ws.LastRowUsed()?.RowNumber()??1;if(last<2)throw new AppException("文件中没有发票数据行。");if(last>20001)throw new AppException("单次最多导入 20,000 行，请拆分文件。");
        var partners=MasterDataService.ListPartners(kind==InvoiceKind.Out?"customer":"supplier",false).ToDictionary(p=>p.Code);
        var existing=new InvoiceService().List(kind,null,null,null,null,false);
        // 与库内唯一索引一致按原文（区分大小写）判重，保证预览口径与保存口径相同。
        var seen=new HashSet<string>(StringComparer.Ordinal);
        for(int row=2;row<=last;row++)
        {
            var raw=Enumerable.Range(1,InvoiceHeaders.Length).Select(c=>ws.Cell(row,c).HasFormula ? "="+ws.Cell(row,c).FormulaA1 : ws.Cell(row,c).GetString().Trim()).ToArray();if(raw.All(string.IsNullOrWhiteSpace))continue;
            var item=new InvoiceImportRow{RowNo=row,Raw=raw,OriginalInvoiceNo=raw[10]};preview.Rows.Add(item);
            try
            {
                for(int c=1;c<=InvoiceHeaders.Length;c++)if(ws.Cell(row,c).HasFormula)throw new AppException("导入不支持公式，请先粘贴为实际票面值。");
                foreach(var c in new[]{3,4,5,11})if(!ws.Cell(row,c).IsEmpty()&&ws.Cell(row,c).DataType!=XLDataType.Text)throw new AppException($"{InvoiceHeaders[c-1]}须按文本填写，以保留前导零");
                var date=ReadDate(ws.Cell(row,1));if(PeriodService.IsClosed(SqlUtil.PeriodOf(date)))throw new AppException($"{SqlUtil.PeriodOf(date)} 期间已关闭");
                if(!partners.TryGetValue(raw[4],out var partner))throw new AppException($"往来单位编码 {raw[4]} 不存在、已停用或身份不符合当前发票方向");
                var gross=ReadInvoiceMoney(ws.Cell(row,6),"含税金额");
                if(gross<0&&raw[10].Length==0)throw new AppException("红字发票必须填写原发票号码。");
                if(gross>0&&raw[10].Length>0)throw new AppException("蓝字发票不能填写原发票号码。");
                var inv=new Invoice{AutoLink=true,Kind=kind,RegDate=date,InvoiceType=raw[1],InvoiceNo=raw[2],InvoiceCode=string.IsNullOrEmpty(raw[3])?null:raw[3],PartnerId=partner.Id,PartnerName=partner.Name,
                    GrossCents=gross,NetCents=ReadInvoiceMoney(ws.Cell(row,8),"不含税金额"),TaxCents=ReadInvoiceMoney(ws.Cell(row,9),"税额"),RateBp=ReadInvoiceRate(ws.Cell(row,7)),
                    DeliverDate=raw[9].Length==0?null:ReadDate(ws.Cell(row,10)),OriginalInvoiceId=gross<0?-1:null,Content=raw[11],Note=raw[12]};
                InvoiceService.Validate(inv);item.Invoice=inv;item.Duplicate=!seen.Add(inv.InvoiceNo)||existing.Any(i=>string.Equals(i.InvoiceNo,inv.InvoiceNo,StringComparison.Ordinal));
            }
            catch(Exception ex){item.Error=ex.Message;}
        }
        // Resolve blue invoices after parsing all rows, including red-before-blue files.
        var batchBlues=preview.Rows.Where(r=>r.Ok&&!r.Duplicate&&r.Invoice?.GrossCents>0).ToDictionary(r=>r.Invoice!.InvoiceNo,r=>r.Invoice!);
        var consumed=new Dictionary<(long partner,string number),(long gross,long net,long tax)>();
        foreach(var row in preview.Rows.Where(r=>r.Ok&&!r.Duplicate&&r.Invoice?.GrossCents<0))
        {
            try
            {
                var red=row.Invoice!;var blue=existing.FirstOrDefault(i=>i.GrossCents>0&&i.InvoiceNo==row.OriginalInvoiceNo&&i.PartnerId==red.PartnerId);
                if(blue==null&&batchBlues.TryGetValue(row.OriginalInvoiceNo,out var batch)&&batch.PartnerId==red.PartnerId)blue=batch;
                if(blue==null)throw new AppException("原票必须是同方向、同往来单位的有效蓝字发票（可在本批文件中）。");
                if(string.CompareOrdinal(red.RegDate,blue.RegDate)<0)throw new AppException("红字登记日期不能早于原蓝票。");
                if(red.RateBp!=blue.RateBp)throw new AppException("红字税率须与原蓝票一致。");
                var key=(red.PartnerId,blue.InvoiceNo);
                if(!consumed.TryGetValue(key,out var used))used=(existing.Where(i=>i.OriginalInvoiceId==blue.Id&&blue.Id>0).Sum(i=>Math.Abs(i.GrossCents)),existing.Where(i=>i.OriginalInvoiceId==blue.Id&&blue.Id>0).Sum(i=>Math.Abs(i.NetCents)),existing.Where(i=>i.OriginalInvoiceId==blue.Id&&blue.Id>0).Sum(i=>Math.Abs(i.TaxCents)));
                var next=(gross:used.gross+Math.Abs(red.GrossCents),net:used.net+Math.Abs(red.NetCents),tax:used.tax+Math.Abs(red.TaxCents));
                if(next.gross>blue.GrossCents||next.net>blue.NetCents||next.tax>blue.TaxCents)throw new AppException("本批及已有红票累计含税、不含税金额或税额超过原蓝票。");
                consumed[key]=next;red.OriginalInvoiceId=blue.Id>0?blue.Id:-1;
            }
            catch(Exception ex){row.Error=ex.Message;}
        }
        if(preview.Rows.Count==0)throw new AppException("文件中没有发票数据行。");
        preview.PayloadSnapshot=InvoiceBatchPayload(preview);return preview;
    }
    private static int CommitInvoiceBatch(InvoiceImportPreview preview)
    {
        InvoiceImportPermission();if(preview.LedgerPath!=AppSession.Db.FilePath||preview.PayloadSnapshot!=InvoiceBatchPayload(preview))throw new AppException("账套或预览数据已变化，请重新选择文件预览。");
        var rows=preview.Rows.Where(r=>r.Ok&&!r.Duplicate&&r.Invoice!=null).OrderBy(r=>r.Invoice!.GrossCents<0?1:0).ThenBy(r=>r.RowNo).ToArray();if(rows.Length==0)throw new AppException("没有可导入的发票行。");
        using var conn=AppSession.Db.Open();using var tx=conn.BeginTransaction(deferred:false);
        try
        {
            var svc=new InvoiceService();
            var unlinkedCount=0;
            var insertedInvoices=new List<(long id,long gross)>();
            foreach(var row in rows)
            {
                var inv=JsonSerializer.Deserialize<Invoice>(JsonSerializer.Serialize(row.Invoice))!;
                if(inv.Id!=0||inv.Kind!=preview.Kind)throw new AppException("导入只允许登记当前类型的新发票。");
                var active=new SqlCmd(conn,"SELECT is_active FROM partners WHERE id=$i",tx).P("$i",inv.PartnerId).Long();if(active!=1)throw new AppException($"第 {row.RowNo} 行的往来单位已停用，请重新预览。");
                if(inv.GrossCents<0)
                {
                    var id=new SqlCmd(conn,"SELECT id FROM invoices WHERE kind=$k AND partner_id=$p AND invoice_no=$n AND status='valid' AND sign=1",tx)
                        .P("$k",preview.Kind==InvoiceKind.Out?"out":"in").P("$p",inv.PartnerId).P("$n",row.OriginalInvoiceNo).Long();
                    if(id==0||(inv.OriginalInvoiceId>0&&inv.OriginalInvoiceId!=id))throw new AppException($"第 {row.RowNo} 行原票已变化或不可用，请重新预览。");inv.OriginalInvoiceId=id;
                }
                var invoiceId=svc.SaveInTransaction(inv,conn,tx,deferAutoLink:true);
                insertedInvoices.Add((invoiceId,inv.GrossCents));
            }
            foreach(var entry in insertedInvoices) InvoiceService.TryAutoLinkInvoice(conn,tx,entry.id);
            foreach(var entry in insertedInvoices)
                if(new SqlCmd(conn,"SELECT COALESCE(SUM(amount_cents),0) FROM invoice_links WHERE invoice_id=$i",tx).P("$i",entry.id).Long()<Math.Abs(entry.gross))unlinkedCount++;
            new SqlCmd(conn,@"INSERT INTO import_batches(ts,file_name,kind,total_rows,ok_rows,fail_rows,user_name,detail_json)
                VALUES($t,$f,$k,$total,$ok,$bad,$u,$detail)",tx)
                .P("$t",SqlUtil.Now()).P("$f",preview.FileName).P("$k",preview.Kind==InvoiceKind.Out?"invoice_out":"invoice_in").P("$total",preview.Rows.Count).P("$ok",rows.Length).P("$bad",preview.ErrorCount).P("$u",AppSession.UserName)
                .P("$detail",JsonSerializer.Serialize(preview.Rows.Where(r=>!r.Ok).Select(r=>new{row=r.RowNo,error=r.Error}))).Exec();
            tx.Commit();preview.UnlinkedInvoiceCount=unlinkedCount;return rows.Length;
        }
        catch(Exception ex){tx.Rollback();throw new AppException("本次发票导入已全部回滚："+ex.Message);}
    }
    public static void ExportInvoiceValidation(InvoiceImportPreview preview,string path)
    {
        InvoiceImportPermission();using var wb=new XLWorkbook();var ws=wb.AddWorksheet("校验结果");
        var headers=InvoiceHeaders.Concat(new[]{"校验状态","错误原因"}).ToArray();
        for(int i=0;i<headers.Length;i++){ws.Cell(1,i+1).Value=headers[i];ws.Cell(1,i+1).Style.Font.Bold=true;ws.Column(i+1).Width=i>=13?42:20;ws.Column(i+1).Style.NumberFormat.Format="@";}
        ws.Column(14).Width=22;ws.Column(15).Width=64;ws.Column(15).Style.Alignment.WrapText=true;
        int row=2;foreach(var item in preview.Rows){for(int c=0;c<InvoiceHeaders.Length;c++)ws.Cell(row,c+1).Value=item.Raw.ElementAtOrDefault(c)??"";ws.Cell(row,14).Value=item.StatusText;var error=item.Error??(item.Duplicate?"重复号码跳过，不覆盖已有发票":"");ws.Cell(row,15).Value=error;ws.Row(row).Height=Math.Clamp((error.Length/32+1)*15,20,90);row++;}
        ws.SheetView.FreezeRows(1);wb.SaveAs(path);
    }
}
