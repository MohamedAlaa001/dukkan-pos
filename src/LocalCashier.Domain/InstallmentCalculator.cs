namespace LocalCashier.Domain;

/// <summary>Pure installment arithmetic on integer cents.</summary>
public static class InstallmentCalculator
{
    /// <summary>Equal split; the rounding remainder is added explicitly to the final installment so the parts sum exactly.</summary>
    public static IReadOnlyList<long> EqualSplit(long totalCents, int count)
    {
        if (count < 1) throw new PosException(PosError.InvalidQuantity, "At least one installment is required.");
        if (totalCents <= 0) throw new PosException(PosError.InvalidPrice, "There is no balance to divide.");
        var each = totalCents / count;
        if (each <= 0) throw new PosException(PosError.InvalidPrice, "Too many installments for this amount.");
        var parts = Enumerable.Repeat(each, count).ToArray();
        parts[^1] += totalCents - each * count;
        return parts;
    }

    /// <summary>Monthly due dates starting at <paramref name="first"/> (yyyy-MM-dd); day-of-month is clamped for short months.</summary>
    public static IReadOnlyList<string> MonthlyDates(string first, int count)
    {
        var d = DateOnly.ParseExact(first, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return Enumerable.Range(0, count).Select(i => d.AddMonths(i).ToString("yyyy-MM-dd")).ToList();
    }

    /// <summary>Paid, Partially paid, Due, Upcoming. Overdue is a separate flag (see <see cref="IsOverdue"/>).</summary>
    public static string Status(string dueDate, long paidCents, long remainingCents, string today)
    {
        if (remainingCents <= 0) return "paid";
        if (paidCents > 0) return "partial";
        return string.CompareOrdinal(dueDate, today) <= 0 ? "due" : "upcoming";
    }

    public static bool IsOverdue(string dueDate, long remainingCents, string today) =>
        remainingCents > 0 && string.CompareOrdinal(dueDate, today) < 0;
}
