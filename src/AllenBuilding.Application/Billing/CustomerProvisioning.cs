using AllenBuilding.Application.Access;
using AllenBuilding.Application.Diagnostics;
using AllenBuilding.Domain.Billing;

namespace AllenBuilding.Application.Billing;

public sealed record CustomerProvisionPolicy(bool Enabled, string ProviderAccount, bool LiveMode);
public sealed record CustomerProvisionResult(CollectionCode Code, Guid? OperationId = null, CustomerProvisionState? State = null);
public interface ICustomerProvisionStore
{
    Task<CustomerProvisionResult> EnsureAsync(Actor actor, Guid accountId, CancellationToken cancellationToken);
}
// Pages and assistant use the same current-tenant admission; provider IDs are not returned to the model.
public sealed class CustomerProvisionActions(ICurrentActor current, ICustomerProvisionStore store, CustomerProvisionPolicy policy)
{
    public async Task<CustomerProvisionResult> EnsureAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!policy.Enabled || !PaymentReferences.Valid(policy.ProviderAccount, "acct_")) return new(CollectionCode.Unavailable);
        var actor = await current.GetAsync(cancellationToken);
        if (actor is null || (actor.ManagedBuildings.Count > 0 && !actor.ChallengedMfa)) return new(CollectionCode.Unavailable);
        if (accountId == Guid.Empty) return new(CollectionCode.ValidationFailed);
        return await store.EnsureAsync(actor, accountId, cancellationToken);
    }
}
public sealed record CustomerProvisionLease(CustomerProvision Original, Guid Token);
public sealed record CustomerObservation(CustomerProvisionOutcome Outcome, string? ProviderAccount = null, bool LiveMode = false, Guid AccountId = default,
    Guid OperationId = default, string? CustomerId = null);
public interface ICustomerProvisionQueue
{
    Task<CustomerProvisionLease?> ClaimAsync(CancellationToken cancellationToken);
    Task<bool> CompleteAsync(CustomerProvisionLease lease, CustomerObservation observation, CancellationToken cancellationToken);
}
public interface ICustomerProvisionGateway
{
    Task<CustomerObservation> CreateAsync(CustomerProvision original, CancellationToken cancellationToken);
}
public interface ICustomerProvisionGatewayFactory
{
    ICustomerProvisionGateway Open(CustomerProvision original);
}
public sealed class CustomerProvisionWorker(ICustomerProvisionQueue queue, ICustomerProvisionGatewayFactory gateways)
{
    public async Task<bool> ProcessOneAsync(CancellationToken cancellationToken = default)
    {
        var lease = await queue.ClaimAsync(cancellationToken); if (lease is null) return false;
        using var operation = OfficeTelemetry.Begin(OfficeOperationKind.CustomerProvision, lease.Original.Id,
            OfficeTelemetry.Origin(lease.Original.OriginTraceId, lease.Original.OriginSpanId));
        CustomerObservation observation; ICustomerProvisionGateway? gateway = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(30));
            gateway = gateways.Open(lease.Original);
            observation = await gateway.CreateAsync(lease.Original, deadline.Token).WaitAsync(deadline.Token);
        }
        catch (Exception) { observation = new(CustomerProvisionOutcome.Unknown); }
        finally { (gateway as IDisposable)?.Dispose(); }
        using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var completed = await queue.CompleteAsync(lease, observation, completion.Token);
        operation.Result = !completed ? OfficeOutcome.LeaseLost : observation.Outcome switch
        { CustomerProvisionOutcome.Verified => OfficeOutcome.Success, CustomerProvisionOutcome.Invalid => OfficeOutcome.Failure, _ => OfficeOutcome.Uncertain };
        return true;
    }
}
