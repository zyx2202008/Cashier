using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Cashier.Core.Services;
using Microsoft.Data.Sqlite;

namespace Cashier.Core;

public sealed record DataPackageInfo(string BookName,string CreatedAt,string AppVersion,int SchemaVersion,
    long DatabaseBytes,string DatabaseSha256,IReadOnlyDictionary<string,long> TableCounts,string FileSha256);

public sealed partial class Database
{
    private sealed record PackageManifest(string Format,int FormatVersion,string BookName,string CreatedAt,string AppVersion,
        int SchemaVersion,long DatabaseBytes,string DatabaseSha256,Dictionary<string,long> TableCounts);
    private const long PackageLimit=1024L*1024*1024;
    private static string FileHash(string path){using var file=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(file));}

    /// <summary>Export an internally consistent full ledger snapshot, without changing its business data.</summary>
    public DataPackageInfo ExportDataPackage(string path)
    {
        RequireAccess(Perm.Backup);
        path=Path.GetFullPath(path);
        if(!path.EndsWith(".cashier",StringComparison.OrdinalIgnoreCase))throw new AppException("整账套文件扩展名应为 .cashier。");
        var parent=Path.GetDirectoryName(path)!;Directory.CreateDirectory(parent);
        var snapshot=Path.Combine(parent,".cashier-export-"+Guid.NewGuid().ToString("N")+".db");
        var temporary=Path.Combine(parent,".cashier-export-"+Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            using(var source=Open())
            using(var destination=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=snapshot,Pooling=false}.ToString()))
            {destination.Open();source.BackupDatabase(destination);}
            using var check=ReadOnly(snapshot);
            var version=checked((int)new SqlCmd(check,"PRAGMA user_version").Long());
            ValidateBackupStructure(check,version);
            var counts=Counts(check);
            var name=new SqlCmd(check,"SELECT v FROM settings WHERE k='book.name'").Str();
            var manifest=new PackageManifest("CashierLedger",1,string.IsNullOrEmpty(name)?"默认账套":name,DateTimeOffset.Now.ToString("O"),
                typeof(Database).Assembly.GetName().Version!.ToString(3),version,new FileInfo(snapshot).Length,FileHash(snapshot),counts);
            if(manifest.DatabaseBytes>PackageLimit)throw new AppException("账套超过1GB，请使用备份与恢复功能迁移。");
            using(var archive=ZipFile.Open(temporary,ZipArchiveMode.Create))
            {
                using(var writer=new StreamWriter(archive.CreateEntry("manifest.json").Open()))writer.Write(JsonSerializer.Serialize(manifest));
                archive.CreateEntryFromFile(snapshot,"ledger.db",CompressionLevel.Optimal);
            }
            // Verify the completed file before it replaces a user-selected export file.
            using(var verified=PreparePackage(temporary)) { }
            RequireAccess(Perm.Backup);
            File.Move(temporary,path,true);
            return Info(manifest,FileHash(path));
        }
        finally{TryDelete(snapshot);TryDelete(temporary);}
    }

    public DataPackageInfo PreviewDataPackage(string path)
    {
        RequireAccess(Perm.Restore);
        using var package=PreparePackage(path);
        return Info(package.Manifest,package.SourceHash);
    }

    /// <summary>Only creates a new ledger directory; it never merges into an existing ledger.</summary>
    public string ImportDataPackage(string path,string targetDirectory,string expectedFileSha256)
    {
        RequireAccess(Perm.Restore);
        var target=Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
        var current=Path.TrimEndingDirectorySeparator(DataDir);
        var parent=Path.GetDirectoryName(target);
        if(parent==null||target.Equals(current,StringComparison.OrdinalIgnoreCase)||
            target.StartsWith(current+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||
            current.StartsWith(target+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new AppException("请选择当前账套目录之外的新账套文件夹。");
        for(var ancestor=new DirectoryInfo(target);ancestor!=null;ancestor=ancestor.Parent)
            if(ancestor.Exists&&(ancestor.Attributes&FileAttributes.ReparsePoint)!=0)throw new AppException("账套目标目录不能经过符号链接或目录联接。");
        if(File.Exists(target)||(Directory.Exists(target)&&Directory.EnumerateFileSystemEntries(target).Any()))
            throw new AppException("目标文件夹必须为空，已有账套或文件不会被覆盖。");
        using var package=PreparePackage(path);
        if(string.IsNullOrWhiteSpace(expectedFileSha256)||!string.Equals(package.SourceHash,expectedFileSha256,StringComparison.Ordinal))
            throw new AppException("数据文件与预览时不一致，请重新选择并核对。");
        Directory.CreateDirectory(parent);
        var stage=Path.Combine(parent,".cashier-import-"+Guid.NewGuid().ToString("N"));
        if(Directory.Exists(stage))throw new AppException("临时目录冲突，请重试。");
        Directory.CreateDirectory(stage);
        try
        {
            File.Copy(package.DatabasePath,Path.Combine(stage,"cashier.db"),false);
            var database=new Database(stage);database.EnsureCreated();
            using(var conn=database.Open())
            {
                ValidateBackupStructure(conn,CurrentVersion);
                // A new path gets a new session epoch. Existing source sessions keep their own ledger.
                new SqlCmd(conn,"UPDATE meta SET v=$v WHERE k='session.epoch'").P("$v",Guid.NewGuid().ToString("N")).Exec();
                new SqlCmd(conn,"INSERT INTO audit_log(ts,username,action,entity,after_json,reason) VALUES($t,$u,'ledger-import','ledger','整账套导入为新账套',$r)")
                    .P("$t",SqlUtil.Now()).P("$u",AppSession.UserName).P("$r",package.Manifest.BookName).Exec();
            }
            RequireAccess(Perm.Restore);
            if(Directory.Exists(target))Directory.Delete(target,false); // succeeds only while the selected folder remains empty
            Directory.Move(stage,target);
            return Path.Combine(target,"cashier.db");
        }
        finally
        {
            // This exact GUID directory was exclusively created above and contains only staged import data.
            if(Directory.Exists(stage)&&Path.GetDirectoryName(Path.GetFullPath(stage))==parent&&Path.GetFileName(stage).StartsWith(".cashier-import-",StringComparison.Ordinal))
                Directory.Delete(stage,true);
        }
    }

    private static SqliteConnection ReadOnly(string path)
    {
        var conn=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());
        conn.Open();return conn;
    }
    private static Dictionary<string,long> Counts(SqliteConnection conn)=>
        new SqlCmd(conn,"SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name")
            .List(r=>r.GetString(0)).ToDictionary(name=>name,name=>new SqlCmd(conn,"SELECT COUNT(*) FROM \""+name.Replace("\"","\"\"")+"\"").Long());
    private static DataPackageInfo Info(PackageManifest m,string hash)=>new(m.BookName,m.CreatedAt,m.AppVersion,m.SchemaVersion,m.DatabaseBytes,m.DatabaseSha256,m.TableCounts,hash);
    private sealed class PreparedPackage(string file,PackageManifest manifest,string hash):IDisposable
    {
        public string DatabasePath{get;}=file;
        public PackageManifest Manifest{get;}=manifest;
        public string SourceHash{get;}=hash;
        public void Dispose()=>TryDelete(DatabasePath);
    }
    private PreparedPackage PreparePackage(string path)
    {
        // Read one locked source stream throughout hashing, manifest checks and extraction.
        var temporary=Path.Combine(DataDir,".package-check-"+Guid.NewGuid().ToString("N")+".db");
        try
        {
            using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            if(input.Length>PackageLimit)throw new AppException("数据文件超过允许大小。");
            var hash=Convert.ToHexString(SHA256.HashData(input));input.Position=0;
            using var archive=new ZipArchive(input,ZipArchiveMode.Read);
            if(archive.Entries.Count!=2||archive.Entries.Count(e=>e.FullName=="manifest.json")!=1||archive.Entries.Count(e=>e.FullName=="ledger.db")!=1)
                throw new AppException("整账套文件结构不正确，请选择本软件导出的 .cashier 文件。");
            var manifestEntry=archive.GetEntry("manifest.json")!;var databaseEntry=archive.GetEntry("ledger.db")!;
            if(manifestEntry.Length>1024*1024||databaseEntry.Length>PackageLimit)throw new AppException("数据文件内容超过允许大小。");
            PackageManifest? manifest;
            using(var json=manifestEntry.Open())manifest=JsonSerializer.Deserialize<PackageManifest>(json);
            if(manifest==null||manifest.Format!="CashierLedger"||manifest.FormatVersion!=1||manifest.SchemaVersion<1||manifest.SchemaVersion>CurrentVersion||
                manifest.DatabaseBytes!=databaseEntry.Length||manifest.TableCounts==null)
                throw new AppException("整账套文件版本或清单无效。");
            using(var destination=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write))
            using(var source=databaseEntry.Open())
            {
                var buffer=new byte[81920];long copied=0;int size;
                while((size=source.Read(buffer))>0){copied+=size;if(copied>PackageLimit||copied>manifest.DatabaseBytes)throw new AppException("账套解压大小与清单不一致。");destination.Write(buffer,0,size);}
            }
            if(new FileInfo(temporary).Length!=manifest.DatabaseBytes||FileHash(temporary)!=manifest.DatabaseSha256)
                throw new AppException("账套数据校验失败，文件可能损坏，请重新导出。");
            using(var check=ReadOnly(temporary))
            {
                if(new SqlCmd(check,"PRAGMA user_version").Long()!=manifest.SchemaVersion)throw new AppException("数据版本与清单不一致。");
                ValidateBackupStructure(check,manifest.SchemaVersion);
                if(manifest.SchemaVersion>=4&&string.IsNullOrWhiteSpace(new SqlCmd(check,"SELECT v FROM meta WHERE k='session.epoch'").Str()))
                    throw new AppException("账套缺少必要的会话控制信息。");
                if(manifest.SchemaVersion>=5)
                {
                    if(new SqlCmd(check,"SELECT COUNT(*) FROM opening_control WHERE id=1").Long()!=1)throw new AppException("账套缺少期初控制记录。");
                    var state=OpeningBalanceService.State(check);
                    if(state.IsLocked)SqlUtil.ValidateDate(state.StartDate,"账套启用日期");
                    else if(new SqlCmd(check,"SELECT COUNT(*) FROM balance_adjustments").Long()>0)throw new AppException("账套存在未锁定期初的异常调整记录。");
                }
                var actual=Counts(check);
                if(actual.Count!=manifest.TableCounts.Count||actual.Any(x=>!manifest.TableCounts.TryGetValue(x.Key,out var count)||count!=x.Value))
                    throw new AppException("数据记录数与清单不一致。");
            }
            return new PreparedPackage(temporary,manifest,hash);
        }
        catch(Exception e) when(e is InvalidDataException or JsonException or SqliteException)
        {TryDelete(temporary);throw new AppException("无法读取整账套文件："+e.Message);}
        catch{TryDelete(temporary);throw;}
    }
    private static void TryDelete(string path){try{File.Delete(path);}catch(IOException){}catch(UnauthorizedAccessException){}}
}
