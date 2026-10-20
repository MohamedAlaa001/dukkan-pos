using LocalCashier.Domain;

namespace LocalCashier.Application;

public sealed record CardTerminalRequest(decimal Amount,bool ConfirmedOnTerminal,string? ApprovalCode=null,string? Last4=null);
public sealed record CardConfirmation(bool Approved,string? ApprovalCode=null,string? Last4=null);

/// <summary>Extension point for a bank-approved adapter. The bundled adapter only records manual terminal confirmation.</summary>
public interface ICardTerminal
{
    CardConfirmation Confirm(CardTerminalRequest request);
}
public sealed class ManualCardTerminal : ICardTerminal
{
    public CardConfirmation Confirm(CardTerminalRequest request)
    {
        if(request.Amount<=0 || Money.FromCents(Money.ToCents(request.Amount))!=request.Amount)
            throw new PosException(PosError.PaymentMismatch,"Card amount must be positive piastres.");
        var last=request.Last4?.Trim();var approval=request.ApprovalCode?.Trim();
        if(!string.IsNullOrEmpty(last) && (last.Length!=4 || !last.All(char.IsAsciiDigit)))
            throw new PosException(PosError.PaymentMismatch,"Enter only the last four card digits.");
        if(approval?.Length>100)throw new PosException(PosError.PaymentMismatch,"Approval code is too long.");
        return new(request.ConfirmedOnTerminal,string.IsNullOrEmpty(approval)?null:approval,string.IsNullOrEmpty(last)?null:last);
    }
}
