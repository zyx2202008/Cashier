using Cashier.Core;
using Cashier.Core.Services;
using ClosedXML.Excel;

if (args.Length != 1 || Directory.Exists(args[0])) throw new Exception("Fresh isolated directory required");
AppSession.Db = new Database(args[0]); AppSession.Db.EnsureCreated();
AppSession.Current = new AuthService().Login("admin", "admin888");
var supplier=MasterDataService.SavePartner(new Partner{Name="月报供应商甲",Kind="supplier",OpeningPayableCents=50000});
var customer=MasterDataService.SavePartner(new Partner{Name="月报客户",Kind="customer"});
var other=MasterDataService.SavePartner(new Partner{Name="月报供应商乙",Kind="supplier"});
using(var conn=AppSession.Db.Open())
{
    new SqlCmd(conn,@"
      INSERT INTO vouchers(id,doc_no,kind,nature,biz_date,period,amount_cents,partner_id,summary,status,created_at,created_by)
      VALUES (100,'P1','payable','None','2024-01-10','202401',1000000,$s,'应付','audited','2024-01-10','test'),
      (101,'P2','payable','None','2024-02-05','202402',200000,$s,'应付','audited','2024-02-05','test'),
      (102,'A1','expense','SupplierPayment','2024-01-11','202401',300000,$s,'付款','audited','2024-01-11','test'),
      (103,'A2','expense','SupplierPayment','2024-02-10','202402',100000,$s,'付款','audited','2024-02-10','test'),
      (104,'A3','expense','SupplierPrepay','2024-02-11','202402',40000,$s,'预付','audited','2024-02-11','test'),
      (105,'A4','income','SupplierRefund','2024-02-12','202402',20000,$s,'退款','audited','2024-02-12','test'),
      (106,'R1','receivable','None','2024-01-10','202401',800000,$c,'应收','audited','2024-01-10','test'),
      (107,'R2','income','CustomerReceipt','2024-02-12','202402',100000,$c,'收款','audited','2024-02-12','test'),
      (108,'R3','income','CustomerPrepay','2024-02-13','202402',50000,$c,'预收','audited','2024-02-13','test'),
      (109,'DRAFT','payable','None','2024-02-15','202402',999999,$s,'草稿','draft','2024-02-15','test');
      INSERT INTO invoices(id,kind,invoice_type,invoice_no,partner_id,reg_date,gross_cents,net_cents,tax_cents,rate_bp,status,created_at,created_by)
      VALUES(10,'in','普通','I1',$s,'2024-01-15',400000,400000,0,0,'valid','2024-01-15','test'),
      (11,'in','普通','I2',$s,'2024-02-15',300000,300000,0,0,'valid','2024-02-15','test'),
      (12,'out','普通','O1',$c,'2024-01-15',200000,200000,0,0,'valid','2024-01-15','test');
      INSERT INTO invoice_links(invoice_id,voucher_id,amount_cents,sign,effective_date)
      VALUES(10,100,400000,1,'2024-01-15'),(11,100,300000,1,'2024-02-15'),(12,106,200000,1,'2024-01-15');")
      .P("$s",supplier).P("$c",customer).Exec();
}
int checks=0, failures=0;
void Check(string text,bool ok){checks++;Console.WriteLine($"{(ok?"PASS":"FAIL")} | {text}");if(!ok)failures++;}
ReportResult Build(string id,string month,long? p=null) {var d=DateTime.Parse(month+"-01");return new ReportService().Build(id,new ReportFilter{From=d.ToString("yyyy-MM-dd"),To=d.AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd"),PartnerId=p});}
long C(ReportResult r,int col)=>(long)r.Rows.Single().Cells[col]!;
var jan=Build("supplier_monthly_payable","2024-01",supplier);
Check("January payable payment carry invoice and unbilled",C(jan,3)==1000000&&C(jan,4)==300000&&C(jan,5)==750000&&C(jan,6)==400000&&C(jan,7)==600000);
var feb=Build("supplier_monthly_payable","2024-02",supplier);
Check("February carries balance, includes prepayment and refund",C(feb,3)==200000&&C(feb,4)==140000&&C(feb,5)==830000);
Check("linked invoices cumulative, unrelated draft excluded",C(feb,6)==700000&&C(feb,7)==500000);
var recv=Build("customer_monthly_receivable","2024-02",customer);
Check("customer prepayment and invoiced balances",C(recv,3)==0&&C(recv,4)==150000&&C(recv,5)==650000&&C(recv,6)==200000&&C(recv,7)==600000);
var all=Build("supplier_monthly_payable","2024-02");
Check("supplier filter and total",all.Rows.Count==2 && (long)all.TotalRow![5]! == 830000 && Build("supplier_monthly_payable","2024-02",other).Rows.Count==1);
var excel=Path.Combine(args[0],"monthly.xlsx");ExcelService.ExportReport(feb,excel);
using(var wb=new XLWorkbook(excel)){var ws=wb.Worksheet(1);Check("Excel headers values and totals",ws.Cell(5,3).GetString()=="供应商名称"&&ws.Cell(6,4).GetDouble()==2000&&ws.Cell(6,6).GetDouble()==8300&&ws.Cell(7,1).GetString()=="合计"&&ws.Cell(7,6).GetDouble()==8300);}
AppSession.Db=new Database(args[0]); AppSession.Db.EnsureCreated();
Check("reopen retains historical month",C(Build("supplier_monthly_payable","2024-01",supplier),5)==750000);
using(var conn=AppSession.Db.Open())new SqlCmd(conn,@"INSERT INTO invoice_link_history(invoice_id,voucher_id,amount_cents,sign,effective_date,ended_date,ended_at,ended_by,reason)
    VALUES(10,100,400000,1,'2024-01-15','2024-02-10','2024-02-10','test','解除'); DELETE FROM invoice_links WHERE invoice_id=10;").Exec();
Check("unlinked invoice preserves earlier month",C(Build("supplier_monthly_payable","2024-01",supplier),6)==400000);
Check("unlink reduces later invoice totals",C(Build("supplier_monthly_payable","2024-02",supplier),6)==300000);
using(var conn=AppSession.Db.Open())new SqlCmd(conn,"UPDATE vouchers SET amount_cents=250000 WHERE id=101").Exec();
Check("modified underlying voucher changes report",C(Build("supplier_monthly_payable","2024-02",supplier),3)==250000);
using(var conn=AppSession.Db.Open())new SqlCmd(conn,"UPDATE vouchers SET status='void' WHERE id=101").Exec();
Check("void underlying voucher excluded",C(Build("supplier_monthly_payable","2024-02",supplier),3)==0);
using(var conn=AppSession.Db.Open())new SqlCmd(conn,"UPDATE invoice_links SET amount_cents=1100000 WHERE invoice_id=11").Exec();
try{Build("supplier_monthly_payable","2024-02",supplier);Check("over-invoicing rejected",false);}catch(AppException){Check("over-invoicing rejected",true);}
Console.WriteLine($"RESULT checks={checks} failures={failures}");return failures==0?0:1;
