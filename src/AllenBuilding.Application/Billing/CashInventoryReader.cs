using AllenBuilding.Domain.Billing;

namespace AllenBuilding.Application.Billing;

// Complete bounded original-charge inventory at a recorded observation time, not an atomic provider snapshot.
public sealed record VerifiedCashInventory(string ProviderAccount, bool LiveMode, Guid AccountId, Guid AttemptId, string CustomerId, string PaymentId,
    string ChargeId, long CapturedMinorUnits, long RefundedMinorUnits, IReadOnlyList<string> SourceIds, IReadOnlyList<CanonicalCashMovement> Movements, DateTimeOffset CheckedAt);
public sealed record CashInventoryObservation(CashObservationState State, VerifiedCashInventory? Inventory = null);
public interface ICashInventoryReader
{
    Task<CashInventoryObservation> ReadInventoryAsync(PaymentAttempt original, CancellationToken cancellationToken);
}
