using AllenBuilding.Application.Diagnostics;
using AllenBuilding.Domain.Billing;

namespace AllenBuilding.Application.Billing;

public sealed record CollectionLease(PaymentAttempt Original, Guid Token, CheckoutProfile Profile, string? OriginTraceId = null, string? OriginSpanId = null, bool ReadOnly = false);
public interface ICollectionWorkQueue
{
    Task<CollectionLease?> ClaimAsync(CancellationToken cancellationToken);
    Task<bool> CompleteAsync(CollectionLease lease, ProviderCollectionObservation observation, CancellationToken cancellationToken);
}
public interface ICollectionGatewayFactory
{
    ICollectionGateway Open(PaymentAttempt original, CheckoutProfile profile);
}

public sealed class CollectionWorker(ICollectionWorkQueue queue, ICollectionGatewayFactory gateways, ICashInventoryReader? cash = null)
{
    public async Task<bool> ProcessOneAsync(CancellationToken cancellationToken = default)
    {
        var lease = await queue.ClaimAsync(cancellationToken); if (lease is null) return false;
        using var operation = OfficeTelemetry.Begin(OfficeOperationKind.CollectionDispatch, lease.Original.OperationId,
            OfficeTelemetry.Origin(lease.OriginTraceId, lease.OriginSpanId));
        ProviderCollectionObservation observation;
        ICollectionGateway? gateway = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(30));
            gateway = gateways.Open(lease.Original, lease.Profile);
            if (lease.ReadOnly && lease.Original.SessionId is null) observation = new(ProviderCollectionState.Invalid);
            else observation = lease.Original.SessionId is { } session
                ? await gateway.ReadAsync(lease.Original, session, deadline.Token).WaitAsync(deadline.Token)
                : await gateway.CreateAsync(lease.Original, deadline.Token).WaitAsync(deadline.Token);
            if (lease.ReadOnly && lease.Original.State == PaymentAttemptState.Settled && observation.State == ProviderCollectionState.Paid)
                observation = observation with { CashInventory = cash is null ? new(CashObservationState.Invalid) : await cash.ReadInventoryAsync(lease.Original, deadline.Token).WaitAsync(deadline.Token) };
        }
        catch (OperationCanceledException) { observation = new(ProviderCollectionState.Unknown); }
        catch (Exception) { observation = new(ProviderCollectionState.Unknown); }
        finally { (gateway as IDisposable)?.Dispose(); }
        using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var completed = await queue.CompleteAsync(lease, observation, completion.Token);
        operation.Result = !completed ? OfficeOutcome.LeaseLost : observation.CashInventory?.State switch
        {
            CashObservationState.Unknown => OfficeOutcome.Uncertain,
            CashObservationState.Invalid => OfficeOutcome.Failure,
            CashObservationState.Pending => OfficeOutcome.Retry,
            _ => observation.State switch
            {
                ProviderCollectionState.Unknown => OfficeOutcome.Uncertain,
                ProviderCollectionState.Invalid => OfficeOutcome.Failure,
                _ => OfficeOutcome.Success
            }
        };
        return true;
    }
}
