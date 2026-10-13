using AllenBuilding.Application.Access;
using AllenBuilding.Domain.Billing;

namespace AllenBuilding.Application.Billing;

// These provider-bearing snapshots stay inside application adapters, never view models or assistant context.
public sealed record AutopayMethodSnapshot(AutopayAuthorization Authorization, AutopayWork Original, Guid AccountVersion);
public sealed record AutopayMethodHandoff(string SessionId, DateTimeOffset ExpiresAt, Uri Destination);
public interface IAutopayMethodStore
{
    Task<AutopayMethodSnapshot?> ReadAsync(Actor actor, Guid authorizationId, CancellationToken cancellationToken);
}
public interface IAutopayMethodGateway
{
    Task<AutopayMethodHandoff?> ReadAsync(AutopayMethodSnapshot original, CancellationToken cancellationToken);
}
public interface IAutopayMethodGatewayFactory
{
    IAutopayMethodGateway Open(AutopayMethodSnapshot original);
}
public sealed class AutopayMethodHandoffs(ICurrentActor current, IAutopayMethodStore store, AutopayPolicy policy, TimeProvider time, IAutopayMethodGatewayFactory? gateways = null)
{
    public async Task<Uri?> ContinueAsync(Guid authorizationId, CancellationToken token = default)
    {
        if (!policy.Enabled || gateways is null || authorizationId == Guid.Empty) return null;
        var actor = await current.GetAsync(token); if (actor is null || actor.ManagedBuildings.Count > 0 && !actor.ChallengedMfa) return null;
        var before = await store.ReadAsync(actor, authorizationId, token); if (before is null) return null;
        var version = before.Authorization.Version; var session = before.Authorization.SetupSessionId; var accountVersion = before.AccountVersion; var workVersion = before.Original.Version;
        IAutopayMethodGateway? gateway = null; AutopayMethodHandoff? handoff;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
            gateway = gateways.Open(before); handoff = await gateway.ReadAsync(before, deadline.Token).WaitAsync(deadline.Token);
        }
        catch (Exception) { return null; }
        finally { (gateway as IDisposable)?.Dispose(); }
        // Original tenancy, terms, holds and versions are rechecked after the external GETs, before URL release.
        var fresh = await current.GetAsync(token); if (fresh is null || fresh.UserId != actor.UserId || fresh.ManagedBuildings.Count > 0 && !fresh.ChallengedMfa) return null;
        var after = await store.ReadAsync(fresh, authorizationId, token);
        if (after is null || handoff is null || session is null || after.Authorization.Version != version || after.AccountVersion != accountVersion || after.Original.Version != workVersion ||
            after.Authorization.SetupSessionId != session || handoff.SessionId != session || handoff.ExpiresAt != after.Authorization.SetupExpiresAt || handoff.ExpiresAt <= time.GetUtcNow() ||
            handoff.Destination is not { IsAbsoluteUri: true } uri || uri.Scheme != "https" || uri.Host != "checkout.stripe.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.AbsoluteUri.Length > 4096) return null;
        return uri;
    }
}
