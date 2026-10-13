using AllenBuilding.Application.Access;
using AllenBuilding.Domain.Billing;

namespace AllenBuilding.Application.Billing;

public sealed record PayerPaymentSummary(Guid AttemptId, PaymentAttemptState State, long TotalMinorUnits, DateTimeOffset CreatedAt);
public sealed record PayerPaymentItem(Guid ObligationId, string Description, long ReviewedMinorUnits, long CapturedMinorUnits, long ReturnedMinorUnits);
public sealed record PayerPaymentView(Guid AttemptId, Guid AccountId, string Suite, PaymentAttemptState State, long TotalMinorUnits,
    DateTimeOffset CreatedAt, long CapturedMinorUnits, long RefundedMinorUnits, long DisputeNetMinorUnits, bool AgreementPending,
    bool CanContinue, IReadOnlyList<PayerPaymentItem> Items);
// Provider references remain inside the application boundary; the HTTP view only receives View.
public sealed record PayerCollectionSnapshot(PayerPaymentView View, PaymentAttempt Original, CheckoutProfile Profile);
public interface IPayerCollectionReader
{
    Task<PayerCollectionSnapshot?> ReadAsync(Actor actor, Guid attemptId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PayerPaymentSummary>> RecentAsync(Actor actor, Guid accountId, CancellationToken cancellationToken);
}

public sealed class PayerCollections(ICurrentActor current, IPayerCollectionReader reader, CollectionPolicy policy, TimeProvider time, ICollectionGatewayFactory? gateways = null)
{
    public async Task<PayerPaymentView?> ReadAsync(Guid attemptId, CancellationToken token = default)
    {
        var actor = await Payer(attemptId, token);
        return actor is null ? null : (await reader.ReadAsync(actor, attemptId, token))?.View;
    }
    public async Task<IReadOnlyList<PayerPaymentSummary>> RecentAsync(Guid accountId, CancellationToken token = default)
    {
        var actor = await Payer(accountId, token);
        return actor is null ? [] : await reader.RecentAsync(actor, accountId, token);
    }
    // A browser POST requests a fresh, verified original handoff. Provider reads happen outside database transactions.
    public async Task<Uri?> ContinueAsync(Guid attemptId, CancellationToken token = default)
    {
        var actor = await Payer(attemptId, token); if (actor is null || gateways is null) return null;
        var before = await reader.ReadAsync(actor, attemptId, token);
        if (before is null || !before.View.CanContinue || before.Original.SessionId is null) return null;
        var originalVersion = before.Original.Version; var originalSession = before.Original.SessionId;
        ProviderCollectionObservation observed;
        ICollectionGateway? gateway = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
            gateway = gateways.Open(before.Original, before.Profile);
            observed = await gateway.ReadAsync(before.Original, before.Original.SessionId, deadline.Token).WaitAsync(deadline.Token);
        }
        catch (Exception) { return null; }
        finally { (gateway as IDisposable)?.Dispose(); }
        // Check generated/private handoff release after the remote call, including original membership and current identity.
        var fresh = await Payer(attemptId, token);
        if (fresh is null || fresh.UserId != actor.UserId) return null;
        var after = await reader.ReadAsync(fresh, attemptId, token);
        if (after is null || !after.View.CanContinue || after.Original.Version != originalVersion ||
            after.Original.SessionId != originalSession || observed.State != ProviderCollectionState.Open ||
            observed.SessionId != after.Original.SessionId || observed.ExpiresAt != after.Original.ExpiresAt || observed.ExpiresAt <= time.GetUtcNow() ||
            observed.Handoff is not { IsAbsoluteUri: true } uri || uri.Scheme != "https" || uri.Host != "checkout.stripe.com" ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.AbsoluteUri.Length > 4096) return null;
        return uri;
    }
    private async Task<Actor?> Payer(Guid attemptId, CancellationToken token)
    {
        if (!policy.Enabled || attemptId == Guid.Empty) return null;
        var actor = await current.GetAsync(token);
        return actor is null || (actor.ManagedBuildings.Count > 0 && !actor.ChallengedMfa) ? null : actor;
    }
}
