namespace LocalCashier.Domain;

public sealed record ReturnAmounts(decimal Taxable, decimal Tax, decimal Total);

public static class ReturnCalculator
{
    /// <summary>
    /// Prices a (partial) return from the original posted line snapshot. The final return of a line receives
    /// whatever remains, so repeated partial returns can never refund more than the original.
    /// </summary>
    public static ReturnAmounts Calculate(
        decimal originalQty, decimal originalTaxable, decimal originalTax,
        decimal priorQty, decimal priorTaxable, decimal priorTax, decimal returnQty)
    {
        if (returnQty <= 0) throw new PosException(PosError.InvalidQuantity, "Return quantity must be positive.");
        if (priorQty + returnQty > originalQty)
            throw new PosException(PosError.ReturnExceedsSold, "Return exceeds the quantity originally sold.");

        var remTaxable = originalTaxable - priorTaxable;
        var remTax = originalTax - priorTax;

        if (priorQty + returnQty == originalQty)
            return new ReturnAmounts(remTaxable, remTax, remTaxable + remTax);

        var taxable = Math.Min(Money.Round(originalTaxable * returnQty / originalQty), remTaxable);
        var tax = Math.Min(Money.Round(originalTax * returnQty / originalQty), remTax);
        return new ReturnAmounts(taxable, tax, taxable + tax);
    }
}

public static class CostCalculator
{
    /// <summary>Moving weighted average: (Q*C + q*c) / (Q+q); an empty stock takes the new cost.</summary>
    public static decimal WeightedAverage(decimal onHand, decimal avgCost, decimal receivedQty, decimal receivedCost)
    {
        if (receivedQty <= 0) throw new PosException(PosError.InvalidQuantity, "Received quantity must be positive.");
        if (onHand <= 0) return Money.RoundCost(receivedCost);
        return Money.RoundCost((onHand * avgCost + receivedQty * receivedCost) / (onHand + receivedQty));
    }
}
