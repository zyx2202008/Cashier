using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.Data.Sqlite;

namespace Cashier.Core.Services;

public enum MasterExchangeKind { Account,Partner,Subject,Dept,Project,Handler }
public sealed record MasterExchangeRow(int RowNo,string Action,string Name,string Detail,string? Error)
{
    public string StatusText=>Error==null?Action:"错误";
}
public sealed class MasterExchangePreview
{
    public IReadOnlyList<MasterExchangeRow> Rows{get;internal init;}=Array.Empty<MasterExchangeRow>();
    public int ErrorCount=>Rows.Count(r=>r.Error!=null);
    public int ChangeCount=>Rows.Count(r=>r.Error==null&&r.Action is "新增" or "修改");
    public int SkipCount=>Rows.Count(r=>r.Error==null&&r.Action=="不变（跳过）");
    internal string LedgerPath{get;init;}="";
    internal string Epoch{get;init;}="";
    internal string PayloadHash{get;init;}="";
    internal string FileName{get;init;}="";
    internal MasterExchangeKind Kind{get;init;}
    internal IReadOnlyList<PreparedMasterRow> Prepared{get;init;}=Array.Empty<PreparedMasterRow>();
    internal bool Committed{get;set;}
    internal MasterExchangePreview(){}
}
internal sealed record PreparedMasterRow(int RowNo,long Id,string OriginalHash,string[] Values,bool Skip);

public static class MasterDataExchangeService
{
    private sealed record Definition(string Table,string Title,string[] Columns,string[] Headers);
    private static Definition Define(MasterExchangeKind kind)=>kind switch
    {
        MasterExchangeKind.Account=>new("accounts","资金账户",["code","name","kind","bank_tail","subject_code","opening_cents","is_active"],["编码","名称","类型（bank或cash）","账号后四位","对应科目编码","期初余额（元）","启用（1或0）"]),
        MasterExchangeKind.Partner=>new("partners","客户与供应商",["code","name","kind","contact","phone","address","terms","level","opening_receivable_cents","opening_payable_cents","is_active"],["编码（新增留空）","名称","类型（customer或supplier或both）","联系人","电话","地址","账期","信用等级","期初应收（元）","期初应付（元）","启用（1或0）"]),
        MasterExchangeKind.Subject=>new("subjects","收入支出科目",["code","name","direction","is_active"],["编码","名称","方向（in或out）","启用（1或0）"]),
        MasterExchangeKind.Dept=>new("depts","部门",["code","name","is_active"],["编码","名称","启用（1或0）"]),
        MasterExchangeKind.Project=>new("projects","项目",["code","name","dept_code","owner","is_active"],["编码","名称","部门编码","负责人","启用（1或0）"]),
        MasterExchangeKind.Handler=>new("handlers","经办人",["name","is_active"],["姓名","启用（1或0）"]),
        _=>throw new AppException("基础资料类型无效。")
    };
    public static string Title(MasterExchangeKind kind)=>Define(kind).Title;
    private static void Require(SqliteConnection conn,SqliteTransaction? tx=null)
    {AppSession.Require(Perm.Import,conn,tx);AppSession.Require(Perm.MasterData,conn,tx);}
    private static string Hash(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string Signature(string[] fields)=>Hash(JsonSerializer.Serialize(fields));
    private static string BookIdentity(SqliteConnection conn,SqliteTransaction tx)
    {
        new SqlCmd(conn,"INSERT OR IGNORE INTO meta(k,v) VALUES('book.identity',$v)",tx).P("$v",Guid.NewGuid().ToString("N")).Exec();
        return new SqlCmd(conn,"SELECT v FROM meta WHERE k='book.identity'",tx).Str()!;
    }

    public static void Export(MasterExchangeKind kind,string path,bool blankTemplate=false)
    {
        var d=Define(kind);
        using var conn=AppSession.Db.Open();using var tx=conn.BeginTransaction(deferred:false);Require(conn,tx);
        var identity=blankTemplate?"":BookIdentity(conn,tx);
        if(new SqlCmd(conn,$"SELECT COUNT(*) FROM {d.Table}",tx).Long()>10000&&!blankTemplate)throw new AppException("该类资料超过一万条，请分批整理。");
        var rows=blankTemplate?new List<(long id,string[] fields)>():ReadAll(conn,tx,kind);
        using var workbook=new XLWorkbook();var sheet=workbook.Worksheets.Add("资料");
        sheet.Cell(1,1).Value="记录编号（新增留空）";
        for(int i=0;i<d.Headers.Length;i++)sheet.Cell(1,i+2).Value=d.Headers[i];
        sheet.Cell(1,d.Headers.Length+2).Value="校验标识（勿改）";
        int row=2;
        foreach(var data in rows)
        {
            sheet.Cell(row,1).Value=data.id.ToString(CultureInfo.InvariantCulture);
            for(int i=0;i<data.fields.Length;i++)
            {
                var value=data.fields[i];
                if(d.Columns[i].EndsWith("_cents"))value=Money.Format(long.Parse(value,CultureInfo.InvariantCulture));
                sheet.Cell(row,i+2).Value=value;
            }
            sheet.Cell(row,d.Headers.Length+2).Value=Signature(data.fields);row++;
        }
        sheet.Range(1,1,1,d.Headers.Length+2).Style.Font.Bold=true;
        sheet.Columns(1,d.Headers.Length+2).Width=23;sheet.Columns(1,d.Headers.Length+2).Style.NumberFormat.Format="@";
        sheet.Column(d.Headers.Length+2).Hide();sheet.SheetView.FreezeRows(1);
        var metadata=workbook.Worksheets.Add("_cashier");metadata.Cell(1,1).Value="CashierMaster/1";metadata.Cell(2,1).Value=kind.ToString();metadata.Cell(3,1).Value=identity;metadata.Visibility=XLWorksheetVisibility.VeryHidden;
        var help=workbook.Worksheets.Add("填写说明");
        var instructions=new[]{"基础资料类型："+d.Title,"修改已有记录：先从本账套导出，在原行编辑，不要更改记录编号及隐藏校验列。", "新增：在空白行填写资料，记录编号留空；客户供应商编码也留空，由软件自动生成。", "启用列填1表示启用，0表示停用；项目的部门编码应已存在。", "同一文件所有有效行整批提交，有错误时必须先修改；没有删除操作。", "锁定期初后，Excel也不能改期初。需要更正请登记余额调整。", "只有管理员、会计可整理基础资料；导出后资料发生变化需重新导出。", "编号、电话、代码按文本保存，保留前导零。"};
        for(int i=0;i<instructions.Length;i++)help.Cell(i+1,1).Value=instructions[i];help.Column(1).Width=110;
        SaveWorkbook(workbook,path);tx.Commit();
    }

    public static MasterExchangePreview Preview(MasterExchangeKind kind,string path)
    {
        var d=Define(kind);
        ValidateWorkbookContainer(path);
        using var workbook=new XLWorkbook(path);
        var metadata=workbook.Worksheets.FirstOrDefault(s=>s.Name=="_cashier");
        var sheet=workbook.Worksheets.FirstOrDefault(s=>s.Name=="资料");
        if(metadata==null||sheet==null||Enumerable.Range(1,3).Any(r=>metadata.Cell(r,1).HasFormula)||metadata.Cell(1,1).GetString()!="CashierMaster/1"||metadata.Cell(2,1).GetString()!=kind.ToString())
            throw new AppException("文件类型不匹配，请使用该类资料的模板或导出文件。");
        if(sheet.Row(1).CellsUsed().Any(c=>c.HasFormula))throw new AppException("资料表头不能使用公式，请使用原模板。");
        if(sheet.Cell(1,1).GetString()!="记录编号（新增留空）")throw new AppException("记录编号表头被修改，请重新导出。");
        for(int i=0;i<d.Headers.Length;i++)if(sheet.Cell(1,i+2).GetString()!=d.Headers[i])throw new AppException("资料表头被修改，请使用原模板。");
        int last=sheet.LastRowUsed()?.RowNumber()??1;if(last>10001)throw new AppException("单次基础资料导入最多一万条。");
        using var conn=AppSession.Db.Open();
        string identity;
        using(var setup=conn.BeginTransaction(deferred:false)){Require(conn,setup);identity=BookIdentity(conn,setup);setup.Commit();}
        using var tx=conn.BeginTransaction(deferred:false);Require(conn,tx);
        var rows=new List<MasterExchangeRow>();var prepared=new List<PreparedMasterRow>();var seen=new HashSet<long>();
        var payload=new List<string[]>();
        for(int rowNo=2;rowNo<=last;rowNo++)
        {
            var hasFormula=sheet.Row(rowNo).CellsUsed().Any(c=>c.HasFormula);
            string Read(int column)=>sheet.Cell(rowNo,column).HasFormula?"":sheet.Cell(rowNo,column).GetString().Trim();
            var values=Enumerable.Range(2,d.Headers.Length).Select(Read).ToArray();
            var rawId=Read(1);var fingerprint=Read(d.Headers.Length+2);
            if(values.All(string.IsNullOrEmpty)&&rawId.Length==0&&!hasFormula)continue;
            payload.Add([rawId,fingerprint,..values]);
            new SqlCmd(conn,"SAVEPOINT master_row",tx).Exec();
            try
            {
                if(hasFormula)throw new AppException("资料行请填写具体值，不支持公式。");
                long id=0;
                if(rawId.Length>0&&(!long.TryParse(rawId,out id)||id<=0))throw new AppException("记录编号应为原导出的正整数，新增请留空。");
                if(id>0&&!seen.Add(id))throw new AppException("同一记录编号在文件中重复；新增请清空编号。");
                var before=id>0?ReadOne(conn,tx,kind,id):null;
                if(id>0&&(metadata.Cell(3,1).GetString()!=identity||before==null||Signature(before)!=fingerprint))
                    throw new AppException("原资料已变化、编号无效或文件来自其他账套，请重新导出。");
                // New rows matching existing data are skipped; different data never silently overwrites by name/code.
                var duplicate=id==0?FindNewDuplicate(conn,tx,kind,values):null;
                var comparisonId=id>0?id:duplicate??0;
                var comparison=comparisonId>0?ReadOne(conn,tx,kind,comparisonId):null;
                if(comparisonId>0&&id==0)values=PrepareDuplicate(kind,values,comparison!);
                var saved=Apply(conn,tx,kind,comparisonId,values);
                var after=ReadOne(conn,tx,kind,saved)!;
                bool skip=comparison!=null&&Signature(comparison)==Signature(after);
                if(id==0&&comparisonId>0&&!skip)throw new AppException("同名或同编码资料已存在；修改请先从本账套导出原记录。");
                prepared.Add(new PreparedMasterRow(rowNo,id,fingerprint,values,skip));
                rows.Add(new MasterExchangeRow(rowNo,skip?"不变（跳过）":id==0?"新增":"修改",DisplayName(kind,values),skip?"与当前资料一致":id==0?"新增一条资料":"更新原记录 "+id,null));
                new SqlCmd(conn,"RELEASE master_row",tx).Exec();
            }
            catch(Exception e) when(e is AppException or SqliteException or FormatException or OverflowException)
            {
                new SqlCmd(conn,"ROLLBACK TO master_row; RELEASE master_row",tx).Exec();
                rows.Add(new MasterExchangeRow(rowNo,"错误",DisplayName(kind,values),e.Message,e.Message));
            }
        }
        tx.Rollback(); // Preview uses the same save rules, but persists none of the row changes or audit entries.
        return new MasterExchangePreview{Rows=rows.AsReadOnly(),Prepared=prepared.AsReadOnly(),Kind=kind,LedgerPath=AppSession.Db.FilePath,
            Epoch=AppSession.Current!.SessionEpoch!,PayloadHash=Hash(JsonSerializer.Serialize(new{kind,payload})),FileName=Path.GetFileName(path)};
    }

    public static int Commit(MasterExchangePreview preview)
    {
        using var conn=AppSession.Db.Open();using var tx=conn.BeginTransaction(deferred:false);Require(conn,tx);
        if(preview.Committed)throw new AppException("本次预览已经导入，请刷新查看。");
        if(preview.LedgerPath!=AppSession.Db.FilePath||preview.Epoch!=AppSession.Current!.SessionEpoch)throw new AppException("账套或会话已变化，请重新预览。");
        if(preview.ErrorCount>0)throw new AppException("文件仍有错误，请修改后重新预览；本次没有导入。");
        if(preview.ChangeCount==0)throw new AppException("没有需要新增或修改的资料。");
        var receipt=JsonSerializer.Serialize(new{payload_sha256=preview.PayloadHash});var kind="master_"+preview.Kind.ToString().ToLowerInvariant();
        if(new SqlCmd(conn,"SELECT COUNT(*) FROM import_batches WHERE kind=$k AND detail_json=$d",tx).P("$k",kind).P("$d",receipt).Long()>0)
            throw new AppException("这批资料已经导入，请查看记录，避免重复新增。");
        int changed=0;
        foreach(var row in preview.Prepared)
        {
            if(row.Id>0)
            {
                var current=ReadOne(conn,tx,preview.Kind,row.Id);
                if(current==null||Signature(current)!=row.OriginalHash)throw new AppException($"第{row.RowNo}行原资料已被修改，整批未导入，请重新导出。");
            }
            if(row.Skip)continue;
            if(row.Id==0&&FindNewDuplicate(conn,tx,preview.Kind,row.Values)!=null)throw new AppException($"第{row.RowNo}行资料已存在，请重新预览；整批未导入。");
            var id=Apply(conn,tx,preview.Kind,row.Id,row.Values);
            AuditLogService.Write(conn,tx,"master-import",Define(preview.Kind).Table,id,null,row.Id==0?null:row.OriginalHash,Signature(ReadOne(conn,tx,preview.Kind,id)!),"Excel整理："+preview.FileName);
            changed++;
        }
        new SqlCmd(conn,"INSERT INTO import_batches(ts,file_name,kind,total_rows,ok_rows,fail_rows,user_name,detail_json) VALUES($t,$f,$k,$total,$ok,0,$u,$detail)",tx)
            .P("$t",SqlUtil.Now()).P("$f",preview.FileName).P("$k",kind).P("$total",preview.Rows.Count).P("$ok",changed).P("$u",AppSession.UserName).P("$detail",receipt).Exec();
        tx.Commit();preview.Committed=true;return changed;
    }

    public static void ExportValidation(MasterExchangePreview preview,string path)=>ExcelService.ExportTable("基础资料校验结果",
        ["Excel行号","状态","名称","校验说明"],preview.Rows.Select(r=>new object?[]{r.RowNo.ToString(),r.StatusText,r.Name,r.Detail}),null,path);
    private static string DisplayName(MasterExchangeKind kind,string[] values)=>values.ElementAtOrDefault(kind==MasterExchangeKind.Handler?0:1)??"";
    private static List<(long id,string[] fields)> ReadAll(SqliteConnection conn,SqliteTransaction tx,MasterExchangeKind kind)
    {
        var d=Define(kind);var sql=kind==MasterExchangeKind.Project?"SELECT x.*,COALESCE(d.code,'') dept_code FROM projects x LEFT JOIN depts d ON d.id=x.dept_id ORDER BY x.id":$"SELECT * FROM {d.Table} ORDER BY id";
        return new SqlCmd(conn,sql,tx).List(r=>(R.L(r,"id"),d.Columns.Select(c=>R.S(r,c)).ToArray()));
    }
    private static string[]? ReadOne(SqliteConnection conn,SqliteTransaction tx,MasterExchangeKind kind,long id)
    {
        var d=Define(kind);var sql=kind==MasterExchangeKind.Project?"SELECT x.*,COALESCE(d.code,'') dept_code FROM projects x LEFT JOIN depts d ON d.id=x.dept_id WHERE x.id=$i":$"SELECT * FROM {d.Table} WHERE id=$i";
        return new SqlCmd(conn,sql,tx).P("$i",id).First(r=>d.Columns.Select(c=>R.S(r,c)).ToArray());
    }
    private static long? FindNewDuplicate(SqliteConnection conn,SqliteTransaction tx,MasterExchangeKind kind,string[] values)
    {
        var d=Define(kind);var key=kind is MasterExchangeKind.Partner or MasterExchangeKind.Handler?"name":"code";
        var value=kind==MasterExchangeKind.Partner?values.ElementAtOrDefault(1):values.ElementAtOrDefault(0);
        return new SqlCmd(conn,$"SELECT id FROM {d.Table} WHERE {key}=$v ORDER BY id LIMIT 1",tx).P("$v",value).First(r=>(long?)r.GetInt64(0));
    }
    private static string[] PrepareDuplicate(MasterExchangeKind kind,string[] values,string[] current)
    {
        var copy=(string[])values.Clone();if(kind==MasterExchangeKind.Partner)copy[0]=current[0];return copy;
    }
    private static long Apply(SqliteConnection conn,SqliteTransaction tx,MasterExchangeKind kind,long id,string[] v)
    {
        var d=Define(kind);if(v.Length!=d.Columns.Length)throw new AppException("资料列数不正确。");
        if(v.Any(x=>x.Length>2000))throw new AppException("资料字段过长，请缩短后导入。");
        string Text(int i)=>v[i].Trim();
        long MoneyAt(int i){var value=Money.ParseSignedOrZero(Text(i),d.Headers[i]);if(value < -Money.MaxCents||value>Money.MaxCents)throw new AppException("期初金额超出允许范围。");return value;}
        bool active=Text(v.Length-1) switch{"1" or "启用"=>true,"0" or "停用"=>false,_=>throw new AppException("启用列应填写1或0。")};
        long saved;
        switch(kind)
        {
            case MasterExchangeKind.Account:
                var accountKind=Text(2) switch{"银行"=>"bank","现金"=>"cash",var value=>value};
                if(accountKind is not ("bank" or "cash"))throw new AppException("账户类型应为bank或cash。");
                saved=MasterDataService.SaveAccountInTransaction(new(){Id=id,Code=Text(0),Name=Text(1),Kind=accountKind,BankTail=Text(3),SubjectCode=Text(4),OpeningCents=MoneyAt(5),IsActive=active},conn,tx);break;
            case MasterExchangeKind.Partner:
                if(id==0&&Text(0).Length>0)throw new AppException("新增客户供应商编码请留空，由软件自动生成。");
                if(id>0&&Text(0)!=ReadOne(conn,tx,kind,id)![0])throw new AppException("客户供应商编码不能手工修改。");
                var partnerKind=Text(2) switch{"客户"=>"customer","供应商"=>"supplier","客户与供应商"=>"both",var value=>value};
                saved=MasterDataService.SavePartnerInTransaction(new(){Id=id,Code=Text(0),Name=Text(1),Kind=partnerKind,Contact=Text(3),Phone=Text(4),Address=Text(5),Terms=Text(6),Level=Text(7),OpeningReceivableCents=MoneyAt(8),OpeningPayableCents=MoneyAt(9),IsActive=active},conn,tx);break;
            case MasterExchangeKind.Subject:
                var direction=Text(2) switch{"收入"=>"in","支出"=>"out",var value=>value};
                if(direction is not ("in" or "out"))throw new AppException("科目方向应为in或out。");
                saved=MasterDataService.SaveSubjectInTransaction(new(){Id=id,Code=Text(0),Name=Text(1),Direction=direction,IsActive=active},conn,tx);break;
            case MasterExchangeKind.Dept:saved=MasterDataService.SaveDeptInTransaction(new(){Id=id,Code=Text(0),Name=Text(1),IsActive=active},conn,tx);break;
            case MasterExchangeKind.Project:
                long? dept=null;
                if(Text(2).Length>0)
                {
                    dept=new SqlCmd(conn,"SELECT id FROM depts WHERE code=$c",tx).P("$c",Text(2)).First(r=>(long?)r.GetInt64(0));
                    if(!dept.HasValue)throw new AppException("部门编码不存在，请先导入部门资料。");
                }
                saved=MasterDataService.SaveProjectInTransaction(new(){Id=id,Code=Text(0),Name=Text(1),DeptId=dept,Owner=Text(3),IsActive=active},conn,tx);break;
            case MasterExchangeKind.Handler:saved=MasterDataService.SaveHandlerInTransaction(new(){Id=id,Name=Text(0),IsActive=active},conn,tx);break;
            default:throw new AppException("资料类型无效。");
        }
        if(!active)new SqlCmd(conn,$"UPDATE {d.Table} SET is_active=0 WHERE id=$i",tx).P("$i",saved).Exec();
        return saved;
    }
    private static void ValidateWorkbookContainer(string path)
    {
        if(new FileInfo(path).Length>20*1024*1024)throw new AppException("资料Excel超过20MB，请拆分文件。");
        using var archive=ZipFile.OpenRead(path);
        if(archive.Entries.Count>4096||archive.Entries.Sum(x=>x.Length)>256L*1024*1024)throw new AppException("Excel内容过大，请使用本软件模板分批整理。");
    }
    private static void SaveWorkbook(XLWorkbook workbook,string path)
    {
        path=Path.GetFullPath(path);if(!path.EndsWith(".xlsx",StringComparison.OrdinalIgnoreCase))throw new AppException("资料文件应为.xlsx。");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);var temp=Path.Combine(Path.GetDirectoryName(path)!,".master-export-"+Guid.NewGuid().ToString("N")+".xlsx");
        try{workbook.SaveAs(temp);File.Move(temp,path,true);}finally{if(File.Exists(temp))File.Delete(temp);}
    }
}
