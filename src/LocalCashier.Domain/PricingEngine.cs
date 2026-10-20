namespace LocalCashier.Domain;

public enum DiscountKind { None, Fixed, Percent }

public sealed record Discount(DiscountKind Kind, decimal Value)
{
    public static readonly Discount None = new(DiscountKind.None, 0m);
    public static Discount Fixed(decimal amount) => new(DiscountKind.Fixed, amount);
    public static Discount Percent(decimal pct) => new(DiscountKind.Percent, pct);
    public bool IsNone => Kind == DiscountKind.None || Value == 0m;
}

public sealed record PricingLineInput(decimal Quantity, decimal UnitPrice, Discount LineDiscount, decimal TaxRate);

public sealed record PricedLine(
    decimal Quantity, decimal UnitPrice, decimal Gross, decimal LineDiscount, decimal OrderDiscount,
    decimal Taxable, decimal Tax, decimal Total, decimal TaxRate);

public sealed record PricedDocument(IReadOnlyList<PricedLine> Lines)
{
    public decimal Gross => Lines.Sum(l => l.Gross);
    public decimal Discount => Lines.Sum(l => l.LineDiscount + l.OrderDiscount);
    public decimal Taxable => Lines.Sum(l => l.Taxable);
    public decimal Tax => Lines.Sum(l => l.Tax);
    public decimal Total => Lines.Sum(l => l.Total);
}

public static class PricingEngine
{
    /// <summary>Apply exact automatic allocations after approved manual discounts, before calculating tax.</summary>
    public static PricedDocument ApplyAdditionalDiscounts(PricedDocument original,IReadOnlyList<long> discounts,bool taxInclusive)
    {
        if(discounts.Count!=original.Lines.Count)throw new PosException(PosError.InvalidDiscount,"Promotion allocation count differs from cart.");
        return new(original.Lines.Select((line,i)=>
        {
            var amount=discounts[i];var basis=Money.ToCents(line.Gross-line.LineDiscount-line.OrderDiscount);
            if(amount<0||amount>basis)throw new PosException(PosError.InvalidDiscount,"Promotion exceeds the remaining line value.");
            var after=basis-amount;
            var tax=Money.ToCents(Money.FromCents(after)*line.TaxRate/(taxInclusive?1+line.TaxRate:1));
            var taxable=taxInclusive?after-tax:after;var total=taxInclusive?after:after+tax;
            return line with {LineDiscount=line.LineDiscount+Money.FromCents(amount),Taxable=Money.FromCents(taxable),Tax=Money.FromCents(tax),Total=Money.FromCents(total)};
        }).ToList());
    }
    public static PricedDocument Price(IReadOnlyList<PricingLineInput> lines, Discount? orderDiscount, bool taxInclusive)
    {
        if (lines.Count == 0) throw new PosException(PosError.EmptyCart, "The cart is empty.");

        var n = lines.Count;
        var gross = new long[n];
        var lineDisc = new long[n];
        var net = new long[n];

        for (var i = 0; i < n; i++)
        {
            var l = lines[i];
            if (l.Quantity <= 0) throw new PosException(PosError.InvalidQuantity, "Quantity must be positive.");
            if (l.UnitPrice < 0) throw new PosException(PosError.InvalidPrice, "Price cannot be negative.");
            if (l.TaxRate < 0) throw new PosException(PosError.InvalidPrice, "Tax rate cannot be negative.");
            var g = Money.Round(l.Quantity * l.UnitPrice);
            gross[i] = Money.ToCents(g);
            lineDisc[i] = Money.ToCents(DiscountAmount(l.LineDiscount, g));
            net[i] = gross[i] - lineDisc[i];
        }

        var sumNet = net.Sum();
        var orderCents = Money.ToCents(DiscountAmount(orderDiscount ?? Discount.None, Money.FromCents(sumNet)));
        var share = Allocate(orderCents, net);

        var result = new List<PricedLine>(n);
        for (var i = 0; i < n; i++)
        {
            var l = lines[i];
            var after = net[i] - share[i];
            long taxable, tax, total;
            if (taxInclusive)
            {
                total = after;
                tax = Money.ToCents(Money.FromCents(after) * l.TaxRate / (1m + l.TaxRate));
                taxable = total - tax;
            }
            else
            {
                taxable = after;
                tax = Money.ToCents(Money.FromCents(after) * l.TaxRate);
                total = taxable + tax;
            }

            result.Add(new PricedLine(
                l.Quantity, l.UnitPrice, Money.FromCents(gross[i]), Money.FromCents(lineDisc[i]),
                Money.FromCents(share[i]), Money.FromCents(taxable), Money.FromCents(tax),
                Money.FromCents(total), l.TaxRate));
        }

        return new PricedDocument(result);
    }

    static decimal DiscountAmount(Discount d, decimal basis)
    {
        if (d.IsNone) return 0m;
        if (d.Value < 0) throw new PosException(PosError.InvalidDiscount, "Discount cannot be negative.");
        decimal amount;
        if (d.Kind == DiscountKind.Percent)
        {
            if (d.Value > 100m) throw new PosException(PosError.InvalidDiscount, "Percentage discount cannot exceed 100.");
            amount = Money.Round(basis * d.Value / 100m);
        }
        else
        {
            amount = Money.Round(d.Value);
        }

        if (amount > basis) throw new PosException(PosError.InvalidDiscount, "Discount exceeds the eligible amount.");
        return amount;
    }

    /// <summary>Largest-remainder allocation of <paramref name="total"/> minor units across weights; ties go to the earlier line.</summary>
    public static long[] Allocate(long total, IReadOnlyList<long> weights)
    {
        var n = weights.Count;
        var result = new long[n];
        var sumW = weights.Sum();
        if (total == 0 || sumW == 0) return result;

        var fracs = new decimal[n];
        long assigned = 0;
        for (var i = 0; i < n; i++)
        {
            var exact = (decimal)total * weights[i] / sumW;
            var floor = Math.Floor(exact);
            result[i] = (long)floor;
            fracs[i] = exact - floor;
            assigned += result[i];
        }

        var leftover = total - assigned;
        foreach (var i in Enumerable.Range(0, n).OrderByDescending(i => fracs[i]).ThenBy(i => i).Take((int)leftover))
            result[i]++;
        return result;
    }
}
