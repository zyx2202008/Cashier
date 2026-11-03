using Cashier.Core;
using Cashier.Core.Services;
var path=Path.GetFullPath(args[0]);
AppSession.Db=new Database(path); AppSession.Db.EnsureCreated();
AppSession.Current=new AuthService().Login("admin","admin888");
var p=MasterDataService.SavePartner(new Partner{Code="AUTO", Name="自动核销测试",Kind="customer"});
var bank=MasterDataService.ListAccounts(false).First(x=>x.Kind=="bank").Id;
var svc=new VoucherService();
long Post(Voucher v){var id=svc.Create(v);svc.Audit(id);return id;}
var day=DateTime.Today.AddMonths(-2).ToString("yyyy-MM-dd");
var before=DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd");
var a=Post(new Voucher{Kind=VoucherKind.Receivable,BizDate=day,AmountCents=10000,PartnerId=p,Summary="应收A"});
var b=Post(new Voucher{Kind=VoucherKind.Receivable,BizDate=day,AmountCents=5000,PartnerId=p,Summary="应收B"});
var pay=Post(new Voucher{Kind=VoucherKind.Income,Nature=VoucherNature.CustomerReceipt,BizDate=day,AmountCents=15000,PartnerId=p,AccountId=bank,Summary="回款"});
var s=new SettlementService();s.Allocate(pay,a,2000);
var r=s.AutoAllocate(pay);
void Check(string name,bool ok){Console.WriteLine($"{(ok?"PASS":"FAIL")} | {name}");if(!ok) Environment.ExitCode=1;}
Check("部分核销后自动补足并继续下一单",r.Sum(x=>x.amount)==13000 && s.UnallocatedOf(pay)==0 && s.UnsettledOf(a)==0 && s.UnsettledOf(b)==0);
var row=new ReportService().Build("receivable_detail",new ReportFilter{From=day,To=before,PartnerId=p}).Rows.First(x=>x.Cells[0]?.ToString()==day && x.Cells[4]?.ToString()=="应收A");
Check("追加核销不改写以前的历史",Convert.ToInt64(row.Cells[6])==2000);
var arow=s.ListByPay(pay).Single(x=>x.BizVoucherId==a); s.Unallocate(arow.Id,"自动测试撤销");
Check("撤销合并后的核销额度",s.UnsettledOf(a)==10000 && s.UnallocatedOf(pay)==10000);
try { s.Allocate(pay,b,1); Check("重复手工核销拦截",false); } catch(AppException) { Check("重复手工核销拦截",true); }
var p2=MasterDataService.SavePartner(new Partner{Code="AUTO2",Name="关闭期间测试",Kind="customer"});
var oldDate=DateTime.Today.AddMonths(-3).ToString("yyyy-MM-dd");
var oldBiz=Post(new Voucher{Kind=VoucherKind.Receivable,BizDate=oldDate,AmountCents=10000,PartnerId=p2,Summary="旧应收"});
var oldPay=Post(new Voucher{Kind=VoucherKind.Income,Nature=VoucherNature.CustomerReceipt,BizDate=oldDate,AmountCents=10000,PartnerId=p2,AccountId=bank,Summary="旧回款"});
s.Allocate(oldPay,oldBiz,2000);
PeriodService.Close(oldDate[..7].Replace("-",""));
var extra=s.AutoAllocate(oldPay);
Check("原期间关闭后在本期追加核销",extra.Sum(x=>x.amount)==8000 && s.UnsettledOf(oldBiz)==0);
var p3=MasterDataService.SavePartner(new Partner{Code="AUTO3",Name="核销回滚测试",Kind="customer"});
var dateA=DateTime.Today.AddMonths(-2).ToString("yyyy-MM-dd");
var dateB=DateTime.Today.AddMonths(-1).ToString("yyyy-MM-dd");
var bizA=Post(new Voucher{Kind=VoucherKind.Receivable,BizDate=dateA,AmountCents=10000,PartnerId=p3,Summary="先核销"});
var bizB=Post(new Voucher{Kind=VoucherKind.Receivable,BizDate=dateB,AmountCents=5000,PartnerId=p3,Summary="关闭期间"});
var pay3=Post(new Voucher{Kind=VoucherKind.Income,Nature=VoucherNature.CustomerReceipt,BizDate=dateA,AmountCents=15000,PartnerId=p3,AccountId=bank,Summary="待分配"});
s.Allocate(pay3,bizA,2000);
PeriodService.Close(dateB[..7].Replace("-",""));
try { s.AutoAllocate(pay3); Check("关闭期间阻止自动核销",false); } catch(AppException) { Check("关闭期间阻止自动核销",true); }
Check("自动核销失败整批回滚",s.UnallocatedOf(pay3)==13000 && s.UnsettledOf(bizA)==8000 && s.UnsettledOf(bizB)==5000 && s.ListByPay(pay3).Single().AmountCents==2000);

