using AllenBuilding.Domain.Billing;

namespace AllenBuilding.Application.Billing;

public enum CashObservationState { Verified, Pending, Unknown, Invalid }
public sealed record CashObservation(CashObservationState State, IReadOnlyList<CanonicalCashMovement>? Movements = null);
public interface ICashMovementReader
{
    Task<CashObservation> ReadAsync(PaymentAttempt original, string sourceId, CancellationToken cancellationToken);
}
public interface ICashMovementStore
{
    Task<PaymentAttempt?> LoadAsync(Guid attemptId, CancellationToken cancellationToken);
    Task<ReconciliationResult> ApplyAsync(Guid attemptId, Guid version, CashObservation observation, CancellationToken cancellationToken);
}
// Internal read-only provider coordinator; leased jobs/scoped manager admission must own its invocation.
public sealed class CashMovementReconciliation(ICashMovementStore store, ICashMovementReader reader)
{
    public async Task<ReconciliationResult> ReconcileAsync(Guid attemptId, string sourceId, CancellationToken cancellationToken = default)
    {
        if (attemptId == Guid.Empty || (!PaymentReferences.Valid(sourceId, "re_") && !CashMovement.DisputeReference(sourceId))) return new(ReconciliationCode.Unavailable);
        var original = await store.LoadAsync(attemptId, cancellationToken); if (original is null || original.State != PaymentAttemptState.Settled) return new(ReconciliationCode.NeedsReview);
        var observation = await reader.ReadAsync(original, sourceId, cancellationToken);
        return await store.ApplyAsync(original.Id, original.Version, observation, cancellationToken);
    }
}
