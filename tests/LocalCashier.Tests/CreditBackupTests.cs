using LocalCashier.Domain;
using LocalCashier.Application;
using LocalCashier.Persistence;

namespace LocalCashier.Tests;

public class CreditBackupTests
{
    [Fact]
    public void Restoring_a_backup_preserves_installment_collections_supplier_settlements_and_stock()
    {
        using var fixture = new PosFixture();
        using var folder = new TempFolder();
        var customer = fixture.Customers.Add(fixture.Owner, "Credit customer", null, true, 1000m);
        var sale = fixture.Sales.Post(fixture.Sale(3, 10m, customer: customer) with
        {
            Arrangement = new CreditArrangement(null, [new InstallmentInput("2026-11-01", 10m), new InstallmentInput("2026-12-01", 10m)])
        });
        new CollectionService(fixture.Db, fixture.Clock).Collect(new CollectRequest(Guid.NewGuid(), customer, 5m,
            PaymentMethod.Cash, fixture.Shift, fixture.Cashier, "2026-10-19"));
        var suppliers = new SupplierService(fixture.Db, fixture.Clock);
        var supplier = suppliers.Create(fixture.Owner, "Supplier", null, null, null);
        suppliers.PostPurchase(new PostPurchaseRequest(Guid.NewGuid(), fixture.Owner, supplier, "SUP-1", "2026-10-19", "2026-11-01",
            [new PurchaseLine(fixture.Widget, 5, 4m)], 0m, PaymentMethod.Transfer));
        suppliers.Pay(new SupplierPayRequest(Guid.NewGuid(), fixture.Owner, supplier, "2026-10-19", 8m, PaymentMethod.Transfer));
        var package = new BackupService(fixture.Db, fixture.Clock).CreatePackage(folder.Combine("backup"), "restore-check-secret", false);

        var restoredPath = folder.Combine("restored/pos.db");
        RestoreService.Restore(package, "restore-check-secret", restoredPath, folder.Combine("preserved"), fixture.Clock, fixture.Owner);
        var restored = new PosDatabase(restoredPath);

        Assert.Equal(15m, new CustomerService(restored, fixture.Clock).Balance(customer));
        var schedule = new InstallmentService(restored, fixture.Clock).Schedule(sale.SaleId);
        Assert.Equal(new[] { 5m, 10m }, schedule.Select(i => i.Remaining));
        Assert.Single(new CollectionService(restored, fixture.Clock).History(customer));
        var restoredSuppliers = new SupplierService(restored, fixture.Clock);
        Assert.Equal(12m, restoredSuppliers.Get(supplier)!.Balance);
        Assert.Single(restoredSuppliers.Payments(supplier));
        Assert.Equal(12m, new CatalogService(restored, fixture.Clock).OnHand(fixture.Widget));
        Assert.Equal(515m, new ShiftService(restored, fixture.Clock).ExpectedCash(fixture.Shift));
    }
}
