namespace LocalCashier.Domain;

/// <summary>Exact decimal helpers. Money is 2dp, quantities 3dp, unit costs 4dp; storage uses scaled integers.</summary>
public static class Money
{
    public static decimal Round(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    public static decimal RoundCost(decimal v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);

    public static long ToCents(decimal v) => decimal.ToInt64(Round(v) * 100m);

    public static decimal FromCents(long cents) => cents / 100m;

    public static long ToMilli(decimal qty)
    {
        var m = qty * 1000m;
        if (m != decimal.Truncate(m))
            throw new PosException(PosError.InvalidQuantity, "Quantity supports at most three decimal places.");
        return decimal.ToInt64(m);
    }

    public static decimal FromMilli(long milli) => milli / 1000m;

    public static long ToCost4(decimal cost) => decimal.ToInt64(RoundCost(cost) * 10000m);

    public static decimal FromCost4(long c) => c / 10000m;
}

public enum PosError
{
    EmptyCart,
    InvalidQuantity,
    InvalidPrice,
    InvalidDiscount,
    ProductUnavailable,
    InsufficientStock,
    ShiftNotOpen,
    ShiftAlreadyOpen,
    ApprovalRequired,
    PermissionDenied,
    TaxNotConfigured,
    PaymentMismatch,
    CreditNotAllowed,
    CreditLimitExceeded,
    ReturnExceedsSold,
    InvalidCredentials,
    NotFound,
    Duplicate,
    BackupInvalid,
    WeakPassword,
    Locked,
    BackupFailed,
    StorageFull,
    ExpiredStock,
    StockCountOpen,
    StockLotRequired,
    StockCountUnsaved,
}

public sealed class PosException : Exception
{
    public PosError Code { get; }

    public PosException(PosError code, string message, Exception? innerException = null) : base(message, innerException) => Code = code;
}

public interface IClock
{
    DateTimeOffset Now { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
}
