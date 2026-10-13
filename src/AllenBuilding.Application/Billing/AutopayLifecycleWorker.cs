using AllenBuilding.Application.Diagnostics;
using AllenBuilding.Domain.Billing;

namespace AllenBuilding.Application.Billing;

public enum AutopayLifecycleOutcome { Verified, Pending, Unknown, Invalid }
public sealed record AutopayLifecycleObservation(AutopayLifecycleOutcome Outcome, VerifiedAutopaySubscription? Subscription = null, VerifiedAutopayStop? Stop = null);
public sealed record AutopayLifecycleLease(AutopayWork Original, AutopayAuthorization Authorization, Guid Token, bool ReadOnly);
public interface IAutopayLifecycleQueue
{
    Task<AutopayLifecycleLease?> ClaimAsync(CancellationToken cancellationToken);
    Task<bool> CompleteAsync(AutopayLifecycleLease lease, AutopayLifecycleObservation observation, CancellationToken cancellationToken);
}
public interface IAutopayLifecycleGateway
{
    Task<AutopayLifecycleObservation> ActivateAsync(AutopayLifecycleLease lease, CancellationToken cancellationToken);
    Task<AutopayLifecycleObservation> StopAsync(AutopayLifecycleLease lease, CancellationToken cancellationToken);
    Task<AutopayLifecycleObservation> ReadAsync(AutopayLifecycleLease lease, CancellationToken cancellationToken);
}
public interface IAutopayLifecycleGatewayFactory
{
    IAutopayLifecycleGateway Open(AutopayLifecycleLease lease);
}

// The port cannot authorize a new charge, settle rent, or create another original economic operation.
public sealed class AutopayLifecycleWorker(IAutopayLifecycleQueue queue, IAutopayLifecycleGatewayFactory gateways, TimeProvider time)
{
    public async Task<bool> ProcessOneAsync(CancellationToken token = default)
    {
        var lease = await queue.ClaimAsync(token); if (lease is null) return false;
        using var operation = OfficeTelemetry.Begin(lease.Original.Stage == AutopayWorkStage.Stop ? OfficeOperationKind.AutopayStop : OfficeOperationKind.AutopayActivation, lease.Original.OriginalOperationId);
        AutopayLifecycleObservation observation; IAutopayLifecycleGateway? gateway = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var now = time.GetUtcNow();
            if (!lease.Original.Owns(lease.Token, now) || lease.Original.Stage == AutopayWorkStage.Setup) observation = new(AutopayLifecycleOutcome.Unknown);
            else
            {
                gateway = gateways.Open(lease);
                var readOnly = lease.ReadOnly || !lease.Original.WithinOriginalWriteWindow(now) || lease.Original.Stage == AutopayWorkStage.Activate && lease.Authorization.SubscriptionId is not null;
                observation = await (readOnly ? gateway.ReadAsync(lease, deadline.Token) : lease.Original.Stage == AutopayWorkStage.Activate
                    ? gateway.ActivateAsync(lease, deadline.Token) : gateway.StopAsync(lease, deadline.Token)).WaitAsync(deadline.Token);
            }
        }
        catch (Exception) { observation = new(AutopayLifecycleOutcome.Unknown); }
        finally { (gateway as IDisposable)?.Dispose(); }
        using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var completed = await queue.CompleteAsync(lease, observation, completion.Token);
        operation.Result = !completed ? OfficeOutcome.LeaseLost : observation.Outcome switch
        { AutopayLifecycleOutcome.Verified => OfficeOutcome.Success, AutopayLifecycleOutcome.Pending => OfficeOutcome.Retry, AutopayLifecycleOutcome.Invalid => OfficeOutcome.Failure, _ => OfficeOutcome.Uncertain };
        return true;
    }
}
