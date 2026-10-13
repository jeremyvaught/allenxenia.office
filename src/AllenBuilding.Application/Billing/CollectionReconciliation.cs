using AllenBuilding.Domain.Billing;

namespace AllenBuilding.Application.Billing;

public enum ReconciliationCode { Applied, AlreadyApplied, Unavailable, Conflict, NeedsReview }
public sealed record ReconciliationResult(ReconciliationCode Code, Guid? PaymentId = null);
public interface ICollectionReconciliationStore
{
    Task<PaymentAttempt?> LoadAsync(Guid attemptId, CancellationToken cancellationToken);
    Task<ReconciliationResult> ApplyAsync(Guid attemptId, Guid version, ProviderCollectionObservation observation, CancellationToken cancellationToken);
}

// Internal original-operation coordinator, not a tenant/manager authority or an HTTP endpoint.
// Its callers must derive references from a leased event/job or a scoped management action.
public sealed class CollectionReconciliation(ICollectionReconciliationStore store, ICollectionGateway gateway)
{
    public async Task<ReconciliationResult> ReconcileAsync(Guid attemptId, string sessionId, CancellationToken cancellationToken = default)
    {
        if (attemptId == Guid.Empty || !PaymentReferences.Valid(sessionId, "cs_")) return new(ReconciliationCode.Unavailable);
        var original = await store.LoadAsync(attemptId, cancellationToken);
        if (original is null) return new(ReconciliationCode.Unavailable);
        var observation = await gateway.ReadAsync(original, sessionId, cancellationToken);
        // Gateway work has finished before the short projection transaction begins.
        return await store.ApplyAsync(original.Id, original.Version, observation, cancellationToken);
    }
}
