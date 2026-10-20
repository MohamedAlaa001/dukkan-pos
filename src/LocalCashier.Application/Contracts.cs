using System.Security.Cryptography;
using LocalCashier.Domain;

namespace LocalCashier.Application;

public enum Role { Owner, Cashier }

public enum PaymentMethod { Cash, Card, Transfer }

public sealed record SaleLineRequest(long ProductId, decimal Quantity, Discount? Discount = null, decimal? PriceOverride = null);

/// <summary>For cash, Amount is the amount tendered; for card/transfer it is the confirmed amount.</summary>
public sealed record PaymentRequest(PaymentMethod Method, decimal Amount, long? MethodId=null, string? Reference=null, CardConfirmation? CardConfirmation=null);

public sealed record PostSaleRequest(
    Guid OperationId, long ShiftId, long CashierId, long? CustomerId,
    IReadOnlyList<SaleLineRequest> Lines, Discount? OrderDiscount,
    IReadOnlyList<PaymentRequest> Payments, long? ApproverId = null, CreditArrangement? Arrangement = null, string? ExpiredStockReason = null,
    string? Coupon=null,IReadOnlyList<long>? SuppressedPromotionIds=null,string? PromotionOverrideReason=null,long RedeemPoints=0,
    decimal? ExpectedTotal=null,decimal? ExpectedRedemptionValue=null);

/// <summary>How the unpaid part of a sale is to be settled. Installments (yyyy-MM-dd, amount) must sum exactly to the unpaid amount; otherwise an optional due date applies.</summary>
public sealed record CreditArrangement(string? DueDate, IReadOnlyList<InstallmentInput>? Installments = null);

public sealed record InstallmentInput(string DueDate, decimal Amount);

public sealed record PostSaleResult(long SaleId, string Number, decimal Total, decimal Change, decimal Debt, bool AlreadyPosted);

public sealed record UserInfo(long Id, string Username, string DisplayName, Role Role);

public static class PasswordHasher
{
    const int Iterations = 210_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256") return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, int.Parse(parts[1]), HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
