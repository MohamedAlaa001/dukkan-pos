using LocalCashier.Application;
using LocalCashier.Domain;
using LocalCashier.Persistence;

namespace LocalCashier.Tests;

public class StockControlTests
{
    [Fact]
    public void Old_supplier_return_reversal_requires_a_physical_lot_after_tracking_is_enabled()
    {
        using var f=new PosFixture();var stock=new StockControlService(f.Db,f.Clock);var s=new SupplierService(f.Db,f.Clock);
        var supplier=s.Create(f.Owner,"Supplier");
        var returned=s.PostReturn(new(Guid.NewGuid(),f.Owner,supplier,"2026-01-10","other","Checked",null,[new(f.Widget,2,4)]));
        stock.ConfigureProduct(f.Owner,f.Widget,true,0,0,null,"OPEN",Day(f,10));
        Assert.Throws<PosException>(()=>s.ReverseReturn(Guid.NewGuid(),f.Owner,returned.Id,"Restore"));
        var lot=stock.Lots(f.Widget).Single().Id;
        s.ReverseReturn(Guid.NewGuid(),f.Owner,returned.Id,"Restore",new Dictionary<long,long>{{f.Widget,lot}});
        Assert.Equal(10m,stock.OnHand(f.Widget,1,lot));
    }
    [Fact]
    public void Cancelling_count_keeps_entries_and_opens_scope_without_stock_changes()
    {
        using var f=new PosFixture();var stock=new StockControlService(f.Db,f.Clock);var id=stock.CreateCount(Guid.NewGuid(),f.Owner,1,[f.Widget]);
        stock.RecordCount(Guid.NewGuid(),f.Cashier,stock.CountLines(id).Single().Id,8);
        Assert.Throws<PosException>(()=>stock.ConfigureProduct(f.Owner,f.Widget,true,0,0,null,"OPEN",Day(f,10)));
        var op=Guid.NewGuid();stock.CancelCount(op,f.Owner,id,"Wrong scope");stock.CancelCount(op,f.Owner,id,"Wrong scope");
        Assert.Throws<PosException>(()=>stock.CancelCount(op,f.Cashier,id,"Wrong scope"));Assert.Equal(10m,f.Catalog.OnHand(f.Widget));
        Assert.Equal(8m,stock.CountLines(id).Single().Counted);Assert.Equal("cancelled",stock.CountSessions().Single().State);
        Assert.True(stock.CreateCount(Guid.NewGuid(),f.Owner,1,[f.Widget])>id);
    }
    [Fact]
    public void Overlapping_open_counts_cannot_post_the_same_physical_shortage_twice()
    {
        using var f=new PosFixture();var stock=new StockControlService(f.Db,f.Clock);
        var first=stock.CreateCount(Guid.NewGuid(),f.Owner,1,[f.Widget]);
        Assert.Throws<PosException>(()=>stock.CreateCount(Guid.NewGuid(),f.Owner,1,[f.Widget]));
        stock.RecordCount(Guid.NewGuid(),f.Cashier,stock.CountLines(first).Single().Id,9);
        stock.ApproveCount(Guid.NewGuid(),f.Owner,first,"Checked");
        var next=stock.CreateCount(Guid.NewGuid(),f.Owner,1,[f.Widget]);
        stock.RecordCount(Guid.NewGuid(),f.Cashier,stock.CountLines(next).Single().Id,9);
        stock.ApproveCount(Guid.NewGuid(),f.Owner,next,"Rechecked");Assert.Equal(9m,f.Catalog.OnHand(f.Widget));
    }
    [Fact]
    public void Legacy_count_cannot_create_an_unlotted_deficit_after_opening_classification()
    {
        using var f=new PosFixture();var stock=new StockControlService(f.Db,f.Clock);
        stock.ClassifyLot(Guid.NewGuid(),f.Owner,f.Widget,1,10,"OPEN",Day(f,10));
        Assert.Throws<PosException>(()=>new StockAdjustmentService(f.Db,f.Clock).ApplyCount(f.Owner,"Legacy count",[new(f.Widget,5)]));
        var back=stock.CreateLocation(f.Owner,"Back");stock.Transfer(Guid.NewGuid(),f.Owner,f.Widget,1,back,10);
        Assert.Equal(0m,stock.OnHand(f.Widget,1));Assert.Equal(10m,stock.OnHand(f.Widget,back));
    }
    [Fact]
    public void Schema12_upgrade_preserves_every_movement_and_interruption_leaves_schema12_intact()
    {
        using var dir=new TempFolder(); var path=dir.Combine("pos.db");
        var old=new PosDatabase(path,12,null); var clock=new SystemClock();
        var owner=new UserService(old,clock).Create("owner","Owner",Role.Owner,"owner-test-1");
        var product=new CatalogService(old,clock).AddProduct(owner,"MIG","MIG","Migrated",null,10);
        using(var c=old.Open())using(var seed=c.CreateCommand())
        {
            seed.CommandText="INSERT INTO stock_movements(product_id,qty_milli,unit_cost_x10000,source_type,source_id,actor_id,created_at) VALUES(@p,10555,40000,'purchase',1,@a,'2026-01-01'),(@p,-2222,40000,'sale',1,@a,'2026-01-02')";
            seed.Parameters.AddWithValue("@p",product);seed.Parameters.AddWithValue("@a",owner);seed.ExecuteNonQuery();
        }
        Assert.Throws<IOException>(()=>new PosDatabase(path,13,step=>{if(step==13)throw new IOException("Interrupted");}));
        using(var c=new PosDatabase(path,12,null).Open())using(var q=c.CreateCommand())
        {q.CommandText="SELECT SUM(qty_milli) FROM stock_movements";Assert.Equal(8333L,q.ExecuteScalar());q.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE name='stock_locations'";Assert.Equal(0L,q.ExecuteScalar());}
        var upgraded=new PosDatabase(path);var stock=new StockControlService(upgraded,clock);
        Assert.Equal(8.333m,stock.OnHand(product,1));Assert.Single(stock.Locations());Assert.False(stock.ProductConfig(product).TrackExpiry);
        Assert.NotEmpty(Directory.GetFiles(upgraded.PreMigrationDirectory,"pos-v12-*.db"));
    }

    [Fact]
    public void Encrypted_backup_retains_counts_transfers_lots_and_fefo_after_restore()
    {
        using var f=new PosFixture();using var dir=new TempFolder();var stock=new StockControlService(f.Db,f.Clock);
        stock.ConfigureProduct(f.Owner,f.Widget,true,10,12,null,"OPEN",Day(f,20));
        var back=stock.CreateLocation(f.Owner,"Back");var lot=stock.Lots(f.Widget).Single().Id;
        stock.Transfer(Guid.NewGuid(),f.Owner,f.Widget,1,back,3,lot);
        var count=stock.CreateCount(Guid.NewGuid(),f.Owner,back,[f.Widget]);stock.RecordCount(Guid.NewGuid(),f.Cashier,stock.CountLines(count).Single().Id,2);
        stock.ApproveCount(Guid.NewGuid(),f.Owner,count,"Checked");
        var package=new BackupService(f.Db,f.Clock).CreatePackage(dir.Combine("backup"),"stock-backup-secret",false);
        var path=dir.Combine("restored/pos.db");RestoreService.Restore(package,"stock-backup-secret",path,dir.Combine("preserved"),f.Clock,f.Owner);
        var restored=new StockControlService(new PosDatabase(path),f.Clock);
        Assert.Equal(7m,restored.OnHand(f.Widget,1,lot));Assert.Equal(2m,restored.OnHand(f.Widget,back,lot));
        Assert.Equal("approved",restored.CountSessions().Single().State);Assert.Equal(-1m,restored.CountLines(count).Single().Variance);
        Assert.Equal(3m,restored.ReorderSuggestions().Single().Quantity);
    }

    [Fact]
    public void Count_approval_fault_rolls_back_and_gain_updates_weighted_cost()
    {
        using var f=new PosFixture();var stock=new StockControlService(f.Db,f.Clock);
        var count=stock.CreateCount(Guid.NewGuid(),f.Owner,1,[f.Widget]);
        stock.RecordCount(Guid.NewGuid(),f.Cashier,stock.CountLines(count).Single().Id,12);
        stock.AdjustStock(Guid.NewGuid(),f.Owner,f.Widget,1,null,10,8,"New receipt cost");
        stock.FaultInjector=_=>throw new IOException("Power loss");var op=Guid.NewGuid();
        Assert.Throws<IOException>(()=>stock.ApproveCount(op,f.Owner,count,"Checked"));
        Assert.Equal(20m,f.Catalog.OnHand(f.Widget));Assert.Equal("open",stock.CountSessions().Single().State);
        stock.FaultInjector=null;stock.ApproveCount(op,f.Owner,count,"Checked");
        Assert.Equal(22m,f.Catalog.OnHand(f.Widget));
        Assert.Equal(5.8182m,stock.Levels(1).Single(l=>l.ProductId==f.Widget).UnitCost);
    }

    [Fact]
    public void Opening_mixed_stock_can_be_classified_without_changing_quantity_or_cost()
    {
        using var f=new PosFixture();var stock=new StockControlService(f.Db,f.Clock);var op=Guid.NewGuid();
        var first=stock.ClassifyLot(op,f.Owner,f.Widget,1,4,"FIRST",Day(f,10));
        Assert.Equal(first,stock.ClassifyLot(op,f.Owner,f.Widget,1,4,"FIRST",Day(f,10)));
        stock.ClassifyLot(Guid.NewGuid(),f.Owner,f.Widget,1,6,"SECOND",Day(f,20));
        stock.ConfigureProduct(f.Owner,f.Widget,true,0,0,null);
        Assert.Equal(10m,f.Catalog.OnHand(f.Widget));Assert.Equal(4m,stock.OnHand(f.Widget,1,first));
        Assert.Throws<PosException>(()=>stock.ClassifyLot(Guid.NewGuid(),f.Owner,f.Widget,1,1,"EXCESS",Day(f,20)));
        Assert.Throws<PosException>(()=>stock.ClassifyLot(op,f.Cashier,f.Widget,1,4,"FIRST",Day(f,10)));
    }
    [Fact]
    public void Supplier_returns_follow_received_lots_and_locations_and_reverse_exact_movements()
    {
        using var f = new PosFixture();
        var stock = new StockControlService(f.Db, f.Clock);
        stock.ConfigureProduct(f.Owner, f.Widget, true, 0, 0, null, "OPEN", Day(f, 100));
        var s = new SupplierService(f.Db, f.Clock);
        var supplier = s.Create(f.Owner, "Supplier");
        var back = stock.CreateLocation(f.Owner, "Back");
        var buy = s.PostPurchase(new(Guid.NewGuid(), f.Owner, supplier, null, "2026-01-10", null,
            [new(f.Widget, 4, 5, LocationId: back, Batch: "DELIVERY", Expiry: Day(f, 10))]));
        var source = s.PurchaseLines(buy.InvoiceId).Single();
        var lot = stock.Lots(f.Widget).Single(l => l.Batch == "DELIVERY").Id;
        Assert.Throws<PosException>(() => s.PostReturn(new(Guid.NewGuid(), f.Owner, supplier, "2026-01-10", "other", "wrong lot", buy.InvoiceId,
            [new(f.Widget, 1, 5, source.Id, back, stock.Lots(f.Widget).Single(l => l.Batch == "OPEN").Id)])));
        stock.Transfer(Guid.NewGuid(), f.Owner, f.Widget, back, 1, 2, lot);
        var returned = s.PostReturn(new(Guid.NewGuid(), f.Owner, supplier, "2026-01-10", "other", "moved goods", buy.InvoiceId,
            [new(f.Widget, 2, 5, source.Id, 1, lot)]));
        Assert.Equal(0m, stock.OnHand(f.Widget, 1, lot));
        s.ReverseReturn(Guid.NewGuid(), f.Owner, returned.Id, "Correction");
        Assert.Equal(2m, stock.OnHand(f.Widget, 1, lot));
        Assert.Throws<PosException>(() => s.ReversePurchase(f.Owner, buy.InvoiceId, "Not in original location"));
        stock.Transfer(Guid.NewGuid(), f.Owner, f.Widget, 1, back, 2, lot);
        s.ReversePurchase(f.Owner, buy.InvoiceId, "Correction");
        Assert.Equal(0m, stock.OnHand(f.Widget, back, lot));
        Assert.Equal(10m, f.Catalog.OnHand(f.Widget));
    }

    [Fact]
    public void Adjustments_are_location_and_lot_aware_atomic_and_depleted_lots_do_not_set_earliest_expiry()
    {
        using var f = new PosFixture();
        var stock = new StockControlService(f.Db, f.Clock);
        stock.ConfigureProduct(f.Owner, f.Widget, true, 0, 0, null, "OPEN", Day(f, 100));
        var early = stock.ReceiveLot(Guid.NewGuid(), f.Owner, f.Widget, 1, 1, 4, "EARLY", Day(f, 10));
        stock.AdjustStock(Guid.NewGuid(), f.Owner, f.Widget, 1, early, -1, null, "Damaged");
        Assert.Equal(Day(f, 100), stock.Levels(1).Single(l => l.ProductId == f.Widget).EarliestExpiry);
        var op = Guid.NewGuid();
        stock.AdjustStock(op, f.Owner, f.Widget, 1, early, 2, 8, "Found");
        stock.AdjustStock(op, f.Owner, f.Widget, 1, early, 2, 8, "Found");
        Assert.Equal(2m, stock.OnHand(f.Widget, 1, early));
        Assert.Throws<PosException>(() => stock.AdjustStock(Guid.NewGuid(), f.Owner, f.Widget, 1, early, -3, null, "Missing"));
        Assert.Throws<PosException>(() => stock.AdjustStock(op, f.Cashier, f.Widget, 1, early, 2, 8, "Found"));
        stock.FaultInjector = _ => throw new InvalidOperationException("power loss");
        Assert.Throws<InvalidOperationException>(() => stock.Transfer(Guid.NewGuid(), f.Owner, f.Widget, 1, stock.CreateLocation(f.Owner, "Back"), 1));
        Assert.Equal(12m, f.Catalog.OnHand(f.Widget));
    }

    static string Day(PosFixture f, int offset) => DateOnly.FromDateTime(f.Clock.Now.LocalDateTime).AddDays(offset).ToString("yyyy-MM-dd");

    [Fact]
    public void Location_transfers_conserve_stock_post_once_and_pos_uses_sales_location()
    {
        using var f = new PosFixture();
        var stock = new StockControlService(f.Db, f.Clock);
        Assert.Equal(10m, stock.OnHand(f.Widget, 1));
        var back = stock.CreateLocation(f.Owner, "Back stock");
        var op = Guid.NewGuid();
        stock.Transfer(op, f.Owner, f.Widget, 1, back, 4m);
        stock.Transfer(op, f.Owner, f.Widget, 1, back, 4m);
        Assert.Equal(6m, stock.OnHand(f.Widget, 1));
        Assert.Equal(4m, stock.OnHand(f.Widget, back));
        Assert.Equal(10m, f.Catalog.OnHand(f.Widget));
        Assert.Throws<PosException>(() => stock.Transfer(Guid.NewGuid(), f.Owner, f.Widget, 1, back, 7));
        stock.SetSalesLocation(f.Owner, back);
        f.Sales.Post(f.Sale(3, 30));
        Assert.Equal(1m, stock.OnHand(f.Widget, back));
        Assert.Equal(6m, stock.OnHand(f.Widget, 1));
        Assert.Throws<PosException>(() => f.Sales.Post(f.Sale(2, 20)));
    }

    [Fact]
    public void Count_entry_variance_survives_sales_before_entry_and_after_entry()
    {
        using var f = new PosFixture();
        var stock = new StockControlService(f.Db, f.Clock);
        var count = stock.CreateCount(Guid.NewGuid(), f.Owner, 1, [f.Widget]);
        f.Sales.Post(f.Sale(2, 20)); // expected at entry = 8
        var line = stock.CountLines(count).Single();
        stock.RecordCount(Guid.NewGuid(), f.Cashier, line.Id, 7); // one missing unit
        Assert.Equal(-1m, stock.CountLines(count).Single().Variance);
        f.Sales.Post(f.Sale(1, 10)); // do not put this sale back at approval
        var op = Guid.NewGuid();
        stock.ApproveCount(op, f.Owner, count, "Checked");
        stock.ApproveCount(op, f.Owner, count, "Checked");
        Assert.Equal(6m, stock.OnHand(f.Widget, 1));
        Assert.Equal(6m, f.Catalog.OnHand(f.Widget));
        Assert.Throws<PosException>(() => stock.RecordCount(Guid.NewGuid(), f.Cashier, line.Id, 9));
    }

    [Fact]
    public void Count_cannot_make_current_location_negative_and_requires_every_line_counted()
    {
        using var f = new PosFixture();
        var stock = new StockControlService(f.Db, f.Clock);
        var count = stock.CreateCount(Guid.NewGuid(), f.Owner, 1, [f.Widget]);
        Assert.Throws<PosException>(() => stock.ApproveCount(Guid.NewGuid(), f.Owner, count, "Not complete"));
        stock.RecordCount(Guid.NewGuid(), f.Cashier, stock.CountLines(count).Single().Id, 0);
        f.Sales.Post(f.Sale(1, 10));
        Assert.Throws<PosException>(() => stock.ApproveCount(Guid.NewGuid(), f.Owner, count, "Would be negative"));
        Assert.Equal(9m, stock.OnHand(f.Widget, 1));
    }

    [Fact]
    public void Lots_consume_fefo_split_sales_and_returns_restore_the_original_lots()
    {
        using var f = new PosFixture();
        var stock = new StockControlService(f.Db, f.Clock);
        stock.ConfigureProduct(f.Owner, f.Widget, true, 2, 10, null, "OPEN", Day(f, 100));
        var second = stock.ReceiveLot(Guid.NewGuid(), f.Owner, f.Widget, 1, 5, 4, "SECOND", Day(f, 20));
        var first = stock.ReceiveLot(Guid.NewGuid(), f.Owner, f.Widget, 1, 3, 4, "FIRST", Day(f, 10));
        var sale = f.Sales.Post(f.Sale(6, 60));
        Assert.Equal(0m, stock.OnHand(f.Widget, 1, first));
        Assert.Equal(2m, stock.OnHand(f.Widget, 1, second));
        var source = f.Sales.FindForReturn(sale.Number)!;
        var returns = new ReturnService(f.Db, f.Clock);
        returns.Post(new(Guid.NewGuid(), sale.SaleId, f.Shift, f.Cashier, f.Owner, "Customer return", [new(source.Lines.Single().SaleLineId, 4, true)], PaymentMethod.Cash));
        Assert.Equal(3m, stock.OnHand(f.Widget, 1, first));
        Assert.Equal(3m, stock.OnHand(f.Widget, 1, second));
        Assert.Equal(f.Catalog.OnHand(f.Widget), stock.Levels().Where(l => l.ProductId == f.Widget).Sum(l => l.Quantity));
    }

    [Fact]
    public void Expired_stock_needs_a_named_owner_and_reason_and_near_expiry_is_configurable()
    {
        using var f = new PosFixture();
        var stock = new StockControlService(f.Db, f.Clock);
        stock.ConfigureProduct(f.Owner, f.Widget, true, 0, 0, null, "EXPIRED", Day(f, -1));
        Assert.Throws<PosException>(() => f.Sales.Post(f.Sale(1, 10)));
        Assert.Throws<PosException>(() => f.Sales.Post(f.Sale(1, 10) with { ApproverId = f.Cashier, ExpiredStockReason = "Approved" }));
        Assert.Throws<PosException>(() => f.Sales.Post(f.Sale(1, 10) with { ApproverId = f.Owner, ExpiredStockReason = "" }));
        f.Sales.Post(f.Sale(1, 10) with { ApproverId = f.Owner, ExpiredStockReason = "Owner checked item" });
        Assert.Equal(9m, stock.OnHand(f.Widget, 1));
        Assert.Contains(stock.NearExpiry(30), l => l.ProductId == f.Widget && l.Expired);
    }

    [Fact]
    public void Tracked_receipts_require_lot_metadata_and_transfers_preserve_lot_identity()
    {
        using var f = new PosFixture();
        var stock = new StockControlService(f.Db, f.Clock);
        stock.ConfigureProduct(f.Owner, f.Widget, true, 0, 0, null, "OPEN", Day(f, 10));
        Assert.Throws<PosException>(() => f.Inventory.Receive(f.Owner, f.Widget, 1, 4));
        var back = stock.CreateLocation(f.Owner, "Back");
        var lot = stock.Lots(f.Widget).Single().Id;
        stock.Transfer(Guid.NewGuid(), f.Owner, f.Widget, 1, back, 2, lot);
        Assert.Equal(8m, stock.OnHand(f.Widget, 1, lot));
        Assert.Equal(2m, stock.OnHand(f.Widget, back, lot));
        Assert.Throws<PosException>(() => stock.ReceiveLot(Guid.NewGuid(), f.Owner, f.Widget, 1, 1, 4, "", ""));
    }

    [Fact]
    public void Reorder_suggestions_use_threshold_target_and_sales_history_and_make_only_a_draft_order()
    {
        using var f = new PosFixture();
        var stock = new StockControlService(f.Db, f.Clock);
        var suppliers = new SupplierService(f.Db, f.Clock);
        var supplier = suppliers.Create(f.Owner, "Preferred");
        stock.ConfigureProduct(f.Owner, f.Widget, false, 5, 20, supplier);
        f.Sales.Post(f.Sale(6, 60));
        var suggestion = Assert.Single(stock.ReorderSuggestions());
        Assert.Equal(16m, suggestion.Quantity);
        Assert.Equal(supplier, suggestion.SupplierId);
        var before = f.Catalog.OnHand(f.Widget);
        var order = stock.CreateReorderOrder(Guid.NewGuid(), f.Owner, supplier);
        Assert.Equal("draft", suppliers.OrderList().Single().Status);
        Assert.Equal(16m, suppliers.OrderLines(order).Single().Quantity);
        Assert.Equal(before, f.Catalog.OnHand(f.Widget));
        Assert.Equal(0m, suppliers.OutstandingBalance(supplier));
    }

    [Fact]
    public void Cashiers_may_count_but_cannot_configure_transfer_or_approve_even_with_owner_operation_id()
    {
        using var f = new PosFixture();
        var stock = new StockControlService(f.Db, f.Clock);
        Assert.Throws<PosException>(() => stock.CreateLocation(f.Cashier, "Forbidden"));
        Assert.Throws<PosException>(() => stock.SetSalesLocation(f.Cashier, 1));
        Assert.Throws<PosException>(() => stock.ConfigureProduct(f.Cashier, f.Widget, false, 1, 5, null));
        Assert.Throws<PosException>(() => stock.CreateCount(Guid.NewGuid(),f.Cashier,1,[f.Widget]));
        Assert.Throws<PosException>(() => stock.SetExpiryWarningDays(f.Cashier,10));
        Assert.Throws<PosException>(() => stock.DefineLot(f.Cashier,f.Widget,"LOT",Day(f,10)));
        Assert.Throws<PosException>(() => stock.ReceiveLot(Guid.NewGuid(),f.Cashier,f.Widget,1,1,4,"LOT",Day(f,10)));
        Assert.Throws<PosException>(() => stock.CreateReorderOrder(Guid.NewGuid(),f.Cashier,1));
        var count = stock.CreateCount(Guid.NewGuid(), f.Owner, 1, [f.Widget]);
        stock.RecordCount(Guid.NewGuid(), f.Cashier, stock.CountLines(count).Single().Id, 10);
        var op = Guid.NewGuid();
        stock.ApproveCount(op, f.Owner, count, "Verified");
        Assert.Throws<PosException>(() => stock.ApproveCount(op, f.Cashier, count, "Verified"));
        Assert.Throws<PosException>(() => stock.Transfer(Guid.NewGuid(), f.Cashier, f.Widget, 1, 2, 1));
    }
}
