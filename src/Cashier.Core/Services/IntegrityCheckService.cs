namespace Cashier.Core.Services;

/// <summary>只读检查旧版本可能遗留的关联异常，不自动修改账务。</summary>
public static class IntegrityCheckService
{
    public static List<string> FindIssues()
    {
        using var conn = AppSession.Db.Open();
        var issues = new List<string>();
        var partnerIssues = new SqlCmd(conn, @"SELECT v.doc_no FROM vouchers v LEFT JOIN partners p ON p.id=v.partner_id
            WHERE v.status<>'void' AND (
             ((v.kind='receivable' OR v.nature IN ('CustomerReceipt','CustomerPrepay','CustomerRefund')) AND (p.id IS NULL OR p.kind NOT IN ('customer','both')))
             OR ((v.kind='payable' OR v.nature IN ('SupplierPayment','SupplierPrepay','SupplierRefund')) AND (p.id IS NULL OR p.kind NOT IN ('supplier','both'))))")
            .List(r => R.S(r,"doc_no"));
        foreach (var no in partnerIssues) issues.Add("往来业务缺少单位或单位类型不匹配，请核对：" + no);
        var transfers = new SqlCmd(conn, @"SELECT p.doc_no FROM vouchers p
            WHERE p.kind='transfer' AND p.status<>'void' AND (
              (SELECT COUNT(*) FROM vouchers f WHERE f.ref_id=p.id AND f.status<>'void') <> CASE WHEN p.fee_cents>0 THEN 1 ELSE 0 END
              OR (p.fee_cents>0 AND NOT EXISTS (SELECT 1 FROM vouchers f WHERE f.ref_id=p.id AND f.status<>'void'
                AND f.amount_cents=p.fee_cents AND f.account_id=p.account_id AND f.biz_date=p.biz_date
                AND ((p.status='audited' AND f.status='audited') OR (p.status IN ('pending','draft') AND f.status IN ('pending','draft')))))
            ) ORDER BY p.doc_no").List(r => R.S(r,"doc_no"));
        foreach (var no in transfers) issues.Add("转账手续费状态或金额不一致：" + no);
        var strayAccounts = new SqlCmd(conn, @"SELECT doc_no FROM vouchers WHERE status<>'void' AND
            ((kind<>'transfer' AND to_account_id IS NOT NULL) OR (kind IN ('receivable','payable') AND account_id IS NOT NULL)) ORDER BY doc_no")
            .List(r => R.S(r,"doc_no"));
        foreach (var no in strayAccounts) issues.Add("旧单据存在多余账户信息（报表已忽略无关转入账户）：" + no);
        var invoices = new SqlCmd(conn, @"SELECT DISTINCT i.invoice_no FROM invoice_links il
            JOIN invoices i ON i.id=il.invoice_id JOIN vouchers v ON v.id=il.voucher_id
            WHERE i.partner_id<>v.partner_id OR
              (i.kind='out' AND NOT (v.kind='receivable' OR (v.kind='income' AND v.nature='SaleCash'))) OR
              (i.kind='in' AND NOT (v.kind='payable' OR (v.kind='expense' AND v.nature='PurchaseCash')))
            ORDER BY i.invoice_no").List(r => R.S(r,"invoice_no"));
        foreach (var no in invoices) issues.Add("发票与业务的往来单位或方向不一致：" + no);
        var invoiceAmounts = new SqlCmd(conn, @"SELECT i.invoice_no FROM invoices i JOIN invoice_links l ON l.invoice_id=i.id
            WHERE i.status='valid' GROUP BY i.id HAVING SUM(l.amount_cents)>i.gross_cents
            OR MIN(l.sign)<>i.sign OR MAX(l.sign)<>i.sign").List(r => R.S(r,"invoice_no"));
        foreach (var no in invoiceAmounts) issues.Add("发票关联金额或方向异常：" + no);
        var businessAmounts = new SqlCmd(conn, @"SELECT v.doc_no FROM vouchers v JOIN invoice_links l ON l.voucher_id=v.id
            JOIN invoices i ON i.id=l.invoice_id AND i.status='valid'
            GROUP BY v.id HAVING SUM(l.amount_cents*l.sign)<0 OR SUM(l.amount_cents*l.sign)>v.amount_cents
            OR v.status<>'audited' OR v.invoiceable<>1").List(r => R.S(r,"doc_no"));
        foreach (var no in businessAmounts) issues.Add("业务开票关联超额或业务不可开票：" + no);
        var payments = new SqlCmd(conn, @"SELECT v.doc_no FROM vouchers v JOIN settlements s ON s.pay_voucher_id=v.id
            GROUP BY v.id HAVING SUM(s.amount_cents)>v.amount_cents OR v.status<>'audited'
            UNION SELECT v.doc_no FROM vouchers v JOIN settlements s ON s.biz_voucher_id=v.id
            GROUP BY v.id HAVING SUM(s.amount_cents)>v.amount_cents OR v.status<>'audited'").List(r => R.S(r,"doc_no"));
        foreach (var no in payments) issues.Add("收付款核销超额或单据未审核：" + no);
        var settlementRoles = new SqlCmd(conn, @"SELECT DISTINCT p.doc_no FROM settlements s
            JOIN vouchers p ON p.id=s.pay_voucher_id JOIN vouchers b ON b.id=s.biz_voucher_id
            WHERE p.partner_id IS NOT b.partner_id OR
              NOT ((b.kind='receivable' AND p.kind='income' AND p.nature IN ('CustomerReceipt','CustomerPrepay'))
                OR (b.kind='payable' AND p.kind='expense' AND p.nature IN ('SupplierPayment','SupplierPrepay')))").List(r => R.S(r,"doc_no"));
        foreach (var no in settlementRoles) issues.Add("核销的往来单位或业务方向不一致：" + no);
        if (new SqlCmd(conn,"SELECT COUNT(*) FROM pragma_foreign_key_check").Long()>0)
            issues.Add("账套存在外键关联缺失，请先备份并核对。");
        return issues;
    }
}
