using AllenBuilding.Application.Access;
using AllenBuilding.Domain.Billing;

namespace AllenBuilding.Application.Billing;

public enum CollectionCode { Success, Unavailable, ValidationFailed, Conflict }
public sealed record CollectionResult(CollectionCode Code, Guid? AttemptId = null, PaymentAttemptState? State = null);
public sealed record CollectReviewedBalances(Guid OperationId, Guid AccountId, IReadOnlyList<ReviewedCollectionBalance> Items);
public sealed record CollectionPolicy(bool Enabled, string ProviderAccount, bool LiveMode, CheckoutProfile? Profile = null);

public interface ICollectionStore
{
    Task<IReadOnlyList<ReviewedCollectionBalance>> ReviewAsync(Actor actor, Guid accountId, IReadOnlyList<Guid> obligations, CancellationToken cancellationToken);
    Task<CollectionResult> AdmitAsync(Actor actor, CollectReviewedBalances command, CancellationToken cancellationToken);
}

// Shared page/assistant entry point. Provider dispatch is a separate durable operation.
public sealed class CollectionActions(ICurrentActor current, ICollectionStore store, CollectionPolicy policy)
{
    public async Task<IReadOnlyList<ReviewedCollectionBalance>> ReviewAsync(Guid accountId, IReadOnlyList<Guid> obligations, CancellationToken cancellationToken = default)
    {
        var actor = await Payer(cancellationToken);
        if (actor is null || accountId == Guid.Empty || !ValidIds(obligations)) return [];
        return await store.ReviewAsync(actor, accountId, obligations, cancellationToken);
    }

    public async Task<CollectionResult> AdmitAsync(CollectReviewedBalances command, CancellationToken cancellationToken = default)
    {
        var actor = await Payer(cancellationToken);
        if (actor is null) return new(CollectionCode.Unavailable);
        if (command.OperationId == Guid.Empty || command.AccountId == Guid.Empty || command.Items is null || command.Items.Count is < 1 or > 25 ||
            command.Items.Any(x => x is null) || !ValidIds(command.Items.Select(x => x.ObligationId).ToArray()) ||
            command.Items.Any(x => x.AccountId != command.AccountId || x.Version == Guid.Empty || x.RemainingMinorUnits <= 0 || x.Currency != "USD" || x.Held || x.HasProviderInstruction))
            return new(CollectionCode.ValidationFailed);
        return await store.AdmitAsync(actor, command, cancellationToken);
    }

    private async Task<Actor?> Payer(CancellationToken cancellationToken)
    {
        if (!policy.Enabled || policy.Profile is null || !PaymentReferences.Valid(policy.ProviderAccount, "acct_")) return null;
        var actor = await current.GetAsync(cancellationToken);
        return actor is null || (actor.ManagedBuildings.Count > 0 && !actor.ChallengedMfa) ? null : actor;
    }
    private static bool ValidIds(IReadOnlyList<Guid>? ids) => ids is { Count: >= 1 and <= 25 } &&
        ids.All(x => x != Guid.Empty) && ids.Distinct().Count() == ids.Count;
}
