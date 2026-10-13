using AllenBuilding.Domain.Billing;

namespace AllenBuilding.Application.Billing;

// Complete bounded original-charge inventory at a recorded observation time, not an atomic provider snapshot.
public sealed record VerifiedCashInventory(string ProviderAccount, bool LiveMode, Guid AccountId, Guid AttemptId, string CustomerId, string PaymentId,
    string ChargeId, long CapturedMinorUnits, long RefundedMinorUnits, IReadOnlyList<string> SourceIds, IReadOnlyList<CanonicalCashMovement> Movements, DateTimeOffset CheckedAt);
public sealed record CashInventoryObservation(CashObservationState State, VerifiedCashInventory? Inventory = null);
public interface ICashInventoryReader
{
    Task<CashInventoryObservation> ReadInventoryAsync(PaymentAttempt original, CancellationToken cancellationToken);
    // Existing readers safely decline pre-ledger capture until they implement this canonical-evidence port.
    Task<CashInventoryObservation> ReadCapturedInventoryAsync(PaymentAttempt original, CanonicalSettlement settlement, CancellationToken cancellationToken)
        => original.State == PaymentAttemptState.Settled ? ReadInventoryAsync(original, cancellationToken) : Task.FromResult(new CashInventoryObservation(CashObservationState.Invalid));
}
