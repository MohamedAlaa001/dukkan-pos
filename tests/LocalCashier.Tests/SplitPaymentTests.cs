using LocalCashier.Application;
using LocalCashier.Domain;
using LocalCashier.Persistence;

namespace LocalCashier.Tests;

public class SplitPaymentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Electronic_supplier_reversal_belongs_to_the_actual_reversal_shift(bool laterShift)
    {
        using var f=new PosFixture();var wallet=EnableWallet(f);
        var suppliers=new SupplierService(f.Db,f.Clock);var supplier=suppliers.Create(f.Owner,"Supplier");
        suppliers.AddOpeningBalance(Guid.NewGuid(),f.Owner,supplier,20);
        suppliers.Pay(new(Guid.NewGuid(),f.Owner,supplier,f.Clock.Now.ToString("yyyy-MM-dd"),10,PaymentMethod.Transfer,ShiftId:f.Shift,MethodId:wallet.Id));
        var reversalShift=f.Shift;
        if(laterShift){new CashService(f.Db,f.Clock).CloseShift(f.Owner,f.Shift,f.Shifts.ExpectedCash(f.Shift),null);reversalShift=f.Shifts.Open(f.Owner,0);}
        suppliers.ReversePayment(f.Owner,suppliers.Payments(supplier).Single().Id,"Correction",reversalShift);
        var reports=new ReportService(f.Db);var day=f.Clock.Now.ToString("yyyy-MM-dd");
        Assert.Equal(0m,reports.PaymentBreakdown(day,day).Single(x=>x.Code==wallet.Code).SupplierPayments);
        Assert.Equal(laterShift?10m:0m,reports.PaymentBreakdown(day,day,shiftId:f.Shift).Single(x=>x.Code==wallet.Code).SupplierPayments);
        if(laterShift)Assert.Equal(-10m,reports.PaymentBreakdown(day,day,shiftId:reversalShift).Single(x=>x.Code==wallet.Code).SupplierPayments);
    }
    [Fact]
    public void Schema13_migration_keeps_old_payments_change_and_refunds_and_rolls_back_interruption()
    {
        using var dir=new TempFolder();var path=dir.Combine("pos.db");var old=new PosDatabase(path,13,null);var clock=new SystemClock();
        var owner=new UserService(old,clock).Create("owner","Owner",Role.Owner,"owner-test-1");var shift=new ShiftService(old,clock).Open(owner,500);
        using(var c=old.Open())using(var seed=c.CreateCommand())
        {
            seed.CommandText="""
                INSERT INTO sales(id,number,operation_id,shift_id,cashier_id,created_at,business_date,tax_inclusive,gross_cents,discount_cents,taxable_cents,tax_cents,total_cents,paid_cents,change_cents,debt_cents)
                  VALUES(1,'S-OLD','old-sale',@sh,@a,'2026-01-01','2026-01-01',0,1000,0,1000,0,1000,1000,200,0);
                INSERT INTO payments(sale_id,method,applied_cents,tendered_cents) VALUES(1,'cash',1000,1200);
                INSERT INTO returns(id,number,operation_id,sale_id,shift_id,actor_id,approver_id,created_at,reason,taxable_cents,tax_cents,total_cents,debt_reduction_cents,refund_cents,refund_method)
                  VALUES(1,'R-OLD','old-return',1,@sh,@a,@a,'2026-01-02','Checked',200,0,200,0,200,'cash');
                INSERT INTO cash_movements(shift_id,kind,amount_cents,source_type,source_id,created_at)
                  VALUES(@sh,'sale_cash',1000,'sale',1,'2026-01-01'),(@sh,'refund_cash',-200,'return',1,'2026-01-02');
                """;seed.Parameters.AddWithValue("@sh",shift);seed.Parameters.AddWithValue("@a",owner);seed.ExecuteNonQuery();
        }
        Assert.Throws<IOException>(()=>new PosDatabase(path,14,step=>{if(step==14)throw new IOException("Interrupted");}));
        using(var c=new PosDatabase(path,13,null).Open())using(var q=c.CreateCommand())
        {q.CommandText="SELECT SUM(applied_cents) FROM payments";Assert.Equal(1000L,q.ExecuteScalar());q.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE name='payment_methods'";Assert.Equal(0L,q.ExecuteScalar());}
        var current=new PosDatabase(path);var receipt=new SaleService(current,clock).GetReceipt(1);
        Assert.Equal(2m,receipt.Change);Assert.Equal(12m,receipt.Payments.Single().Tendered);Assert.Equal("cash",receipt.Payments.Single().MethodCode);
        Assert.Equal(2m,new ReturnService(current,clock).RefundPayments(1).Single().Amount);Assert.Equal(508m,new ShiftService(current,clock).ExpectedCash(shift));
        Assert.NotEmpty(Directory.GetFiles(current.PreMigrationDirectory,"pos-v13-*.db"));
    }
    [Fact]
    public void Partial_original_method_refunds_allocate_every_penny_exactly_and_owner_choice_is_preserved()
    {
        using var f=new PosFixture();var req=f.Sale(3,0.01m,approver:f.Owner,extra:[new(PaymentMethod.Card,0.01m),new(PaymentMethod.Transfer,0.01m)]) with {Lines=[new(f.Widget,3,PriceOverride:0.01m)]};
        var sale=f.Sales.Post(req);var line=f.Sales.FindForReturn(sale.Number)!.Lines.Single();var returns=new ReturnService(f.Db,f.Clock);
        var amounts=new Dictionary<string,decimal>();
        for(var i=0;i<3;i++)
        {
            var result=returns.Post(new(Guid.NewGuid(),sale.SaleId,f.Shift,f.Cashier,f.Owner,"Partial",[new(line.SaleLineId,1,true)],null));
            var payment=Assert.Single(returns.RefundPayments(result.ReturnId));amounts[payment.Code]=amounts.GetValueOrDefault(payment.Code)+payment.Amount;
        }
        Assert.Equal(3,amounts.Count);Assert.All(amounts.Values,v=>Assert.Equal(0.01m,v));Assert.Equal(500m,f.Shifts.ExpectedCash(f.Shift));
        var next=f.Sales.Post(f.Sale(1,0,extra:[new(PaymentMethod.Card,10)]));var nextLine=f.Sales.FindForReturn(next.Number)!.Lines.Single();
        var chosen=returns.Post(new(Guid.NewGuid(),next.SaleId,f.Shift,f.Cashier,f.Owner,"Owner chose cash",[new(nextLine.SaleLineId,1,true)],PaymentMethod.Cash));
        Assert.Equal("cash",returns.RefundPayments(chosen.ReturnId).Single().Code);Assert.Equal(490m,f.Shifts.ExpectedCash(f.Shift));
    }
    [Fact]
    public void Split_posting_and_refund_faults_rollback_all_tenders_stock_and_cash_before_retry()
    {
        using var f=new PosFixture();var req=f.Sale(2,5,extra:[new(PaymentMethod.Card,15)]);
        f.Sales.FaultInjector=step=>{if(step=="after_money")throw new IOException("Power loss");};
        Assert.Throws<IOException>(()=>f.Sales.Post(req));Assert.Equal(0L,f.Count("payments"));Assert.Equal(500m,f.Shifts.ExpectedCash(f.Shift));Assert.Equal(10m,f.Catalog.OnHand(f.Widget));
        f.Sales.FaultInjector=null;var sale=f.Sales.Post(req);Assert.Equal(2L,f.Count("payments"));
        var returns=new ReturnService(f.Db,f.Clock);var line=f.Sales.FindForReturn(sale.Number)!.Lines.Single();var request=new PostReturnRequest(Guid.NewGuid(),sale.SaleId,f.Shift,f.Cashier,f.Owner,"Checked",[new(line.SaleLineId,1,true)],null);
        returns.FaultInjector=step=>{if(step=="after_return_stock")throw new IOException("Power loss");};
        Assert.Throws<IOException>(()=>returns.Post(request));Assert.Equal(0L,f.Count("refund_payments"));Assert.Equal(0L,f.Count("returns"));Assert.Equal(8m,f.Catalog.OnHand(f.Widget));
        returns.FaultInjector=null;var posted=returns.Post(request);Assert.Equal(10m,returns.RefundPayments(posted.ReturnId).Sum(p=>p.Amount));Assert.Equal(502.5m,f.Shifts.ExpectedCash(f.Shift));
    }
    [Fact]
    public void Original_method_refund_includes_collections_and_clears_remaining_debt_first()
    {
        using var f=new PosFixture();var customer=f.Customers.Add(f.Owner,"Customer",creditEnabled:true,creditLimit:100);var wallet=EnableWallet(f);
        var collections=new CollectionService(f.Db,f.Clock);var sale=f.Sales.Post(f.Sale(4,10,customer:customer));var collection=collections.Collect(new(Guid.NewGuid(),customer,10,PaymentMethod.Transfer,f.Shift,f.Cashier,MethodId:wallet.Id));
        var returns=new ReturnService(f.Db,f.Clock);var line=f.Sales.FindForReturn(sale.Number)!.Lines.Single();
        var first=returns.Post(new(Guid.NewGuid(),sale.SaleId,f.Shift,f.Cashier,f.Owner,"Part",[new(line.SaleLineId,1,true)],null));
        Assert.Equal(10m,first.DebtReduction);Assert.Empty(returns.RefundPayments(first.ReturnId));
        var final=returns.Post(new(Guid.NewGuid(),sale.SaleId,f.Shift,f.Cashier,f.Owner,"Rest",[new(line.SaleLineId,3,true)],null));
        Assert.Equal(10m,final.DebtReduction);var rows=returns.RefundPayments(final.ReturnId);
        Assert.Equal(10m,rows.Single(p=>p.Code=="cash").Amount);Assert.Equal(10m,rows.Single(p=>p.Code=="vodafone_cash").Amount);Assert.Equal(0m,f.Customers.Balance(customer));
        Assert.Throws<PosException>(()=>collections.Reverse(f.Owner,collection.CollectionId,"Already refunded",f.Shift));
    }
    [Fact]
    public void Encrypted_backup_retains_method_configuration_snapshots_and_split_refunds()
    {
        using var f=new PosFixture();using var dir=new TempFolder();var wallet=EnableWallet(f);
        var sale=f.Sales.Post(f.Sale(2,10,extra:[new(PaymentMethod.Transfer,10,MethodId:wallet.Id,Reference:"W-REF")]));
        var returns=new ReturnService(f.Db,f.Clock);var line=f.Sales.FindForReturn(sale.Number)!.Lines.Single();
        var result=returns.Post(new(Guid.NewGuid(),sale.SaleId,f.Shift,f.Cashier,f.Owner,"Checked",[new(line.SaleLineId,1,true)],null));
        var package=new BackupService(f.Db,f.Clock).CreatePackage(dir.Combine("backup"),"split-backup-secret",false);var path=dir.Combine("restored/pos.db");
        RestoreService.Restore(package,"split-backup-secret",path,dir.Combine("preserved"),f.Clock,f.Owner);var restored=new PosDatabase(path);
        Assert.True(new PaymentMethodService(restored,f.Clock).List().Single(p=>p.Id==wallet.Id).Enabled);
        Assert.Equal("W-REF",new SaleService(restored,f.Clock).GetReceipt(sale.SaleId).Payments.Single(p=>p.MethodCode==wallet.Code).Reference);
        Assert.Equal(10m,new ReturnService(restored,f.Clock).RefundPayments(result.ReturnId).Sum(p=>p.Amount));
    }
    static PaymentMethodRow EnableWallet(PosFixture f)
    {
        var methods=new PaymentMethodService(f.Db,f.Clock);var row=methods.List(false).Single(m=>m.Code=="vodafone_cash");
        methods.Update(f.Owner,row.Id,row.NameEn,row.NameAr,true,row.SortOrder);return row;
    }
    [Fact]
    public void Configured_methods_preserve_history_names_and_cashier_cannot_change_them()
    {
        using var f=new PosFixture();var methods=new PaymentMethodService(f.Db,f.Clock);
        Assert.Equal(4,methods.List().Count);var wallet=EnableWallet(f);
        Assert.Throws<PosException>(()=>methods.Update(f.Cashier,wallet.Id,"Changed","Changed",false,1));
        var sale=f.Sales.Post(f.Sale(1,0,extra:[new(PaymentMethod.Transfer,10,MethodId:wallet.Id,Reference:"REF-1")]));
        methods.Update(f.Owner,wallet.Id,"Renamed","اسم جديد",false,9);
        Assert.DoesNotContain(methods.List(),m=>m.Id==wallet.Id);
        var payment=Assert.Single(f.Sales.GetReceipt(sale.SaleId).Payments);
        Assert.Equal("vodafone_cash",payment.MethodCode);Assert.Equal(wallet.NameEn,payment.NameEn);Assert.Equal("REF-1",payment.Reference);
        Assert.Throws<PosException>(()=>f.Sales.Post(f.Sale(1,0,extra:[new(PaymentMethod.Transfer,10,MethodId:wallet.Id)])));
    }
    [Fact]
    public void Multiple_methods_post_once_and_only_applied_cash_enters_the_drawer()
    {
        using var f=new PosFixture();var wallet=EnableWallet(f);var before=f.Shifts.ExpectedCash(f.Shift);
        var req=f.Sale(10,40,extra:[new(PaymentMethod.Card,40,CardConfirmation:new(true,"APPROVED","1234")),new(PaymentMethod.Transfer,20),new(PaymentMethod.Transfer,10,MethodId:wallet.Id,Reference:"WALLET-1")]);
        var result=f.Sales.Post(req);Assert.True(f.Sales.Post(req).AlreadyPosted);
        Assert.Equal(10m,result.Change);Assert.Equal(0m,result.Debt);Assert.Equal(before+30,f.Shifts.ExpectedCash(f.Shift));
        var receipt=f.Sales.GetReceipt(result.SaleId);Assert.Equal(4,receipt.Payments.Count);Assert.Equal(100m,receipt.Payments.Sum(p=>p.Applied));
        var card=receipt.Payments.Single(p=>p.Method=="card");Assert.Equal("1234",card.Last4);Assert.Equal("APPROVED",card.ApprovalCode);
        var report=new ReportService(f.Db).Summary(f.Clock.Now.ToString("yyyy-MM-dd"),f.Clock.Now.ToString("yyyy-MM-dd"));
        Assert.Equal(10m,report.PaymentsByMethod["vodafone_cash"]);
    }
    [Fact]
    public void Method_kind_mismatch_unconfirmed_card_bad_last4_and_electronic_overpay_are_refused_atomically()
    {
        using var f=new PosFixture();var wallet=EnableWallet(f);
        Assert.Throws<PosException>(()=>f.Sales.Post(f.Sale(1,0,extra:[new(PaymentMethod.Cash,10,MethodId:wallet.Id)])));
        Assert.Throws<PosException>(()=>f.Sales.Post(f.Sale(1,0,extra:[new(PaymentMethod.Card,10,CardConfirmation:new(false))])));
        Assert.Throws<PosException>(()=>f.Sales.Post(f.Sale(1,0,extra:[new(PaymentMethod.Card,10,CardConfirmation:new(true,"OK","1234567890123456"))])));
        Assert.Throws<PosException>(()=>f.Sales.Post(f.Sale(1,0,extra:[new(PaymentMethod.Transfer,11,MethodId:wallet.Id)])));
        Assert.Equal(0L,f.Count("sales"));Assert.Equal(10m,f.Catalog.OnHand(f.Widget));Assert.Equal(500m,f.Shifts.ExpectedCash(f.Shift));
        ICardTerminal terminal=new ManualCardTerminal();Assert.False(terminal.Confirm(new(10,false)).Approved);Assert.True(terminal.Confirm(new(10,true,"OK","1234")).Approved);
    }
    [Fact]
    public void Split_credit_remainder_uses_existing_credit_rules_and_disabled_credit_is_refused()
    {
        using var f=new PosFixture();var customer=f.Customers.Add(f.Owner,"Customer",creditEnabled:true,creditLimit:100);
        var sale=f.Sales.Post(f.Sale(3,5,customer:customer,extra:[new(PaymentMethod.Card,10)]));
        Assert.Equal(15m,sale.Debt);Assert.Equal(15m,f.Customers.Balance(customer));
        var methods=new PaymentMethodService(f.Db,f.Clock);var credit=methods.List(false).Single(m=>m.Kind=="credit");
        methods.Update(f.Owner,credit.Id,credit.NameEn,credit.NameAr,false,credit.SortOrder);
        Assert.Throws<PosException>(()=>f.Sales.Post(f.Sale(1,0,customer:customer)));
    }
    [Fact]
    public void Original_method_refunds_split_exactly_and_keep_disabled_original_wallet_available()
    {
        using var f=new PosFixture();var wallet=EnableWallet(f);
        var sale=f.Sales.Post(f.Sale(10,30,extra:[new(PaymentMethod.Card,40),new(PaymentMethod.Transfer,20),new(PaymentMethod.Transfer,10,MethodId:wallet.Id)]));
        var methods=new PaymentMethodService(f.Db,f.Clock);methods.Update(f.Owner,wallet.Id,wallet.NameEn,wallet.NameAr,false,wallet.SortOrder);
        var returns=new ReturnService(f.Db,f.Clock);var line=f.Sales.FindForReturn(sale.Number)!.Lines.Single();var before=f.Shifts.ExpectedCash(f.Shift);
        var request=new PostReturnRequest(Guid.NewGuid(),sale.SaleId,f.Shift,f.Cashier,f.Owner,"Checked",[new(line.SaleLineId,2,true)],null);
        var returned=returns.Post(request);Assert.True(returns.Post(request).AlreadyPosted);
        var split=returns.RefundPayments(returned.ReturnId).ToDictionary(p=>p.Code,p=>p.Amount);
        Assert.Equal(6m,split["cash"]);Assert.Equal(8m,split["card"]);Assert.Equal(4m,split["transfer"]);Assert.Equal(2m,split["vodafone_cash"]);
        Assert.Equal(before-6,f.Shifts.ExpectedCash(f.Shift));
        var totals=new ReportService(f.Db).PaymentBreakdown(f.Clock.Now.ToString("yyyy-MM-dd"),f.Clock.Now.ToString("yyyy-MM-dd"),shiftId:f.Shift);
        Assert.Equal(8m,totals.Single(p=>p.Code==wallet.Code).Net);Assert.Equal(24m,totals.Single(p=>p.Code=="cash").Net);
        var final=returns.Post(request with {OperationId=Guid.NewGuid(),Lines=[new(line.SaleLineId,8,true)]});
        Assert.Equal(100m,split.Values.Sum()+returns.RefundPayments(final.ReturnId).Sum(p=>p.Amount));
        Assert.Equal(500m,f.Shifts.ExpectedCash(f.Shift));
    }
    [Fact]
    public void Manual_wallet_collection_and_supplier_payment_keep_the_selected_identity()
    {
        using var f=new PosFixture();var wallet=EnableWallet(f);var customer=f.Customers.Add(f.Owner,"Customer",creditEnabled:true,creditLimit:100);
        f.Sales.Post(f.Sale(2,0,customer:customer));var collections=new CollectionService(f.Db,f.Clock);
        var paid=collections.Collect(new(Guid.NewGuid(),customer,10,PaymentMethod.Transfer,f.Shift,f.Cashier,Reference:"W-1",MethodId:wallet.Id));
        Assert.Equal(wallet.Id,collections.History(customer).Single().MethodId);
        var suppliers=new SupplierService(f.Db,f.Clock);var supplier=suppliers.Create(f.Owner,"Supplier");suppliers.AddOpeningBalance(Guid.NewGuid(),f.Owner,supplier,20);
        suppliers.Pay(new(Guid.NewGuid(),f.Owner,supplier,"2026-01-10",10,PaymentMethod.Transfer,MethodId:wallet.Id));
        Assert.Equal(wallet.Id,suppliers.Payments(supplier).Single().MethodId);Assert.Equal(500m,f.Shifts.ExpectedCash(f.Shift));
    }
}
