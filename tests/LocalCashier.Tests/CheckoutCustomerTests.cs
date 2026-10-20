using LocalCashier.Application;
using LocalCashier.Domain;
using LocalCashier.Persistence;
using LocalCashier.Printing;

namespace LocalCashier.Tests;

public class CheckoutCustomerTests
{
    [Fact]
    public void Checkout_capture_reuses_a_customer_by_phone_and_adds_a_new_one_otherwise()
    {
        using var f = new PosFixture();
        var first = f.Customers.FindOrAdd(f.Cashier, "Sara Ali", "010 1234 5678");
        Assert.Equal(first, f.Customers.FindOrAdd(f.Cashier, "Someone else", "01012345678"));       // same phone: same customer, no duplicate
        var second = f.Customers.FindOrAdd(f.Cashier, "Omar", "01099998888");
        Assert.NotEqual(first, second);
        Assert.Equal(2, f.Customers.List().Count);
        Assert.Equal("01012345678", f.Customers.List().Single(c => c.Id == first).Phone);
        Assert.Throws<PosException>(() => f.Customers.FindOrAdd(f.Cashier, " ", "01055556666"));      // a new phone needs a name
    }

    [Fact]
    public void The_receipt_carries_only_the_customer_name_and_walk_in_sales_have_none()
    {
        using var f = new PosFixture();
        var id = f.Customers.FindOrAdd(f.Cashier, "Sara Ali", "01012345678");
        var named = f.Sales.Post(f.Sale(1, 10m, customer: id));
        var walkIn = f.Sales.Post(f.Sale(1, 10m));

        var receipt = f.Sales.GetReceipt(named.SaleId);
        Assert.Equal("Sara Ali", receipt.Customer);
        Assert.Null(f.Sales.GetReceipt(walkIn.SaleId).Customer);

        // The phone number is never part of the printable receipt model.
        Assert.DoesNotContain(receipt.GetType().GetProperties(), p => p.Name.Contains("Phone"));
        Assert.NotNull(Sta.Run(() => DocumentBuilder.Receipt80(receipt, new StoreInfo("Shop", null, null, null, false), false)));
    }

    [Fact]
    public void Customer_statistics_cover_purchases_returns_average_and_favourites()
    {
        using var f = new PosFixture();
        var id = f.Customers.FindOrAdd(f.Cashier, "Sara Ali", "01012345678");
        Assert.Equal(0, f.Customers.Stats(id).Invoices);
        f.Sales.Post(f.Sale(2, 20m, customer: id));
        f.Sales.Post(f.Sale(1, 10m, customer: id));
        var s = f.Customers.Stats(id);
        Assert.Equal((2, 30m, 15m, 0, 30m), (s.Invoices, s.Purchases, s.AverageBasket, s.Returns, s.Net));
        Assert.Equal("Widget", Assert.Single(s.TopItems).Name);
        Assert.Equal(3m, s.TopItems[0].Quantity);
        Assert.NotNull(s.FirstPurchase);
    }
}
