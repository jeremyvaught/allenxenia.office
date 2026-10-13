using AllenBuilding.Application.Diagnostics;
using AllenBuilding.Domain.Billing;

namespace AllenBuilding.Application.Billing;

public enum EventReferenceState { Known, Unknown, Invalid, Unsupported }
public sealed record ProviderEventReference(EventReferenceState State, Guid? AttemptId = null, Guid? AccountId = null, string? ReviewHash = null, string? SessionId = null);
public sealed record PaymentEventLease(PaymentEventInbox Original, Guid Token);
public sealed record EventCollectionTarget(PaymentAttempt Original, CheckoutProfile Profile);
public interface IPaymentEventResolver
{
    Task<ProviderEventReference> ResolveAsync(PaymentEventInbox original, CancellationToken cancellationToken);
}
public interface IPaymentEventQueue
{
    Task<PaymentEventLease?> ClaimAsync(CancellationToken cancellationToken);
    Task<EventCollectionTarget?> LoadTargetAsync(PaymentEventLease lease, ProviderEventReference reference, CancellationToken cancellationToken);
    Task<bool> CompleteAsync(PaymentEventLease lease, ProviderEventReference reference, EventCollectionTarget? target,
        ProviderCollectionObservation? observation, CancellationToken cancellationToken);
}

// Event labels are triggers, never financial evidence. Every supported projection uses a current resource read.
public sealed class PaymentEventWorker(IPaymentEventQueue queue, IPaymentEventResolver resolver, ICollectionGatewayFactory gateways)
{
    public async Task<bool> ProcessOneAsync(CancellationToken cancellationToken = default)
    {
        var lease = await queue.ClaimAsync(cancellationToken); if (lease is null) return false;
        using var operation = OfficeTelemetry.Begin(OfficeOperationKind.PaymentEvent);
        ProviderEventReference reference = new(EventReferenceState.Unknown); EventCollectionTarget? target = null; ProviderCollectionObservation? observation = null;
        ICollectionGateway? gateway = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(45));
            reference = await resolver.ResolveAsync(lease.Original, deadline.Token).WaitAsync(deadline.Token);
            if (reference.State == EventReferenceState.Known)
            {
                target = await queue.LoadTargetAsync(lease, reference, deadline.Token);
                if (target is not null)
                {
                    gateway = gateways.Open(target.Original, target.Profile);
                    observation = await gateway.ReadAsync(target.Original, reference.SessionId!, deadline.Token).WaitAsync(deadline.Token);
                }
            }
        }
        catch (OperationCanceledException) { reference = new(EventReferenceState.Unknown); }
        catch (Exception) { reference = new(EventReferenceState.Unknown); }
        finally { (gateway as IDisposable)?.Dispose(); }
        using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var completed = await queue.CompleteAsync(lease, reference, target, observation, completion.Token);
        operation.Result = !completed ? OfficeOutcome.LeaseLost : reference.State == EventReferenceState.Unknown || observation?.State == ProviderCollectionState.Unknown
            ? OfficeOutcome.Uncertain : reference.State != EventReferenceState.Known || target is null || observation?.State == ProviderCollectionState.Invalid ? OfficeOutcome.Failure : OfficeOutcome.Success;
        return true;
    }
}
