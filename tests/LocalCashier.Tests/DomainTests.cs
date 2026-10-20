using LocalCashier.Domain;
using Xunit;

namespace LocalCashier.Tests;

public class PricingTests
{
    static PricingLineInput L(decimal q, decimal p, decimal rate = 0m, Discount? d = null) => new(q, p, d ?? Discount.None, rate);

    [Fact]
    public void Rounds_half_away_from_zero()
    {
        Assert.Equal(0.03m, Money.Round(0.025m));
        Assert.Equal(-0.03m, Money.Round(-0.025m));
    }

    [Fact]
    public void Exclusive_tax_adds_on_top()
    {
        var doc = PricingEngine.Price([L(2, 50m, 0.14m)], null, taxInclusive: false);
        Assert.Equal(100m, doc.Taxable);
        Assert.Equal(14m, doc.Tax);
        Assert.Equal(114m, doc.Total);
    }

    [Fact]
    public void Inclusive_tax_is_extracted_from_price()
    {
        var doc = PricingEngine.Price([L(1, 114m, 0.14m)], null, taxInclusive: true);
        Assert.Equal(114m, doc.Total);
        Assert.Equal(14m, doc.Tax);
        Assert.Equal(100m, doc.Taxable);
    }

    [Fact]
    public void Order_discount_allocation_sums_exactly_with_ties_to_earlier_line()
    {
        // 0.10 across three equal lines -> 0.04, 0.03, 0.03
        var doc = PricingEngine.Price([L(1, 10m), L(1, 10m), L(1, 10m)], Discount.Fixed(0.10m), false);
        Assert.Equal([0.04m, 0.03m, 0.03m], doc.Lines.Select(l => l.OrderDiscount).ToArray());
        Assert.Equal(29.90m, doc.Total);
    }

    [Fact]
    public void Line_totals_always_equal_document_total()
    {
        var doc = PricingEngine.Price([L(3, 3.33m, 0.14m), L(1.5m, 7.77m, 0.14m), L(7, 0.99m)], Discount.Percent(7.5m), false);
        Assert.Equal(doc.Lines.Sum(l => l.Total), doc.Total);
        Assert.Equal(doc.Lines.Sum(l => l.Taxable) + doc.Lines.Sum(l => l.Tax), doc.Total);
    }

    [Theory]
    [InlineData(150)]
    [InlineData(-1)]
    public void Excess_or_negative_discount_is_rejected(double v)
    {
        var ex = Assert.Throws<PosException>(() => PricingEngine.Price([L(1, 100m)], Discount.Fixed((decimal)v), false));
        Assert.Equal(PosError.InvalidDiscount, ex.Code);
    }

    [Fact]
    public void Quantity_beyond_three_decimals_is_rejected()
        => Assert.Equal(PosError.InvalidQuantity, Assert.Throws<PosException>(() => Money.ToMilli(1.2345m)).Code);
}

public class ReturnTests
{
    [Fact]
    public void Repeated_partial_returns_never_exceed_original()
    {
        // 3 units, taxable 10.00, tax 0.01 -> awkward thirds
        decimal pq = 0, pt = 0, ptax = 0;
        for (var i = 0; i < 3; i++)
        {
            var r = ReturnCalculator.Calculate(3, 10.00m, 0.01m, pq, pt, ptax, 1);
            pq += 1; pt += r.Taxable; ptax += r.Tax;
        }
        Assert.Equal(10.00m, pt);
        Assert.Equal(0.01m, ptax);
    }

    [Fact]
    public void Cannot_return_more_than_sold()
        => Assert.Equal(PosError.ReturnExceedsSold,
            Assert.Throws<PosException>(() => ReturnCalculator.Calculate(2, 10m, 0m, 1, 5m, 0m, 2)).Code);
}

public class CostTests
{
    [Fact]
    public void Weighted_average_blends_cost()
        => Assert.Equal(12.5m, CostCalculator.WeightedAverage(10, 10m, 10, 15m));

    [Fact]
    public void Empty_stock_takes_new_cost()
        => Assert.Equal(15m, CostCalculator.WeightedAverage(0, 10m, 5, 15m));
}
