namespace AllenBuilding.Domain.Billing;

// Append-only reversal of part of an original capture's allocation, not a second refund instruction.
public sealed class RefundAllocation
{
    private RefundAllocation() { }
    public Guid MovementId { get; private set; }
    public Guid CaptureId { get; private set; }
    public Guid AccountId { get; private set; }
    public Guid ObligationId { get; private set; }
    public Guid ReviewedVersion { get; private set; }
    public long SignedMinorUnits { get; private set; }

    public static RefundAllocation Record(CashMovement movement, PaymentAllocation original, Guid reviewedVersion, long amount, long availableCapturedFunds)
    {
        if (movement is null || original is null || movement.Kind != CashMovementKind.Refund || movement.SignedMinorUnits >= 0 ||
            movement.CaptureId != original.PaymentId || movement.AccountId != original.AccountId || reviewedVersion == Guid.Empty ||
            amount is < 1 or > 99_999_999 || amount > -movement.SignedMinorUnits || amount > original.AmountMinorUnits ||
            amount > availableCapturedFunds || availableCapturedFunds > original.AmountMinorUnits)
            throw new ArgumentException("Refund allocation requires an exact original capture item and its still-available funds.");
        return new()
        {
            MovementId = movement.Id,
            CaptureId = movement.CaptureId,
            AccountId = movement.AccountId,
            ObligationId = original.ObligationId,
            ReviewedVersion = reviewedVersion,
            SignedMinorUnits = -amount
        };
    }
}
