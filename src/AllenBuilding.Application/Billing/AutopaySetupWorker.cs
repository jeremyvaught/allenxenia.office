using AllenBuilding.Application.Diagnostics;
using AllenBuilding.Domain.Billing;

namespace AllenBuilding.Application.Billing;

public enum AutopaySetupOutcome { Verified, Unknown, Invalid }
public sealed record AutopaySetupObservation(AutopaySetupOutcome Outcome, VerifiedAutopaySetup? Setup = null);
public sealed record AutopaySetupLease(AutopayWork Original, AutopayAuthorization Authorization, Guid Token, bool ReadOnly);
public interface IAutopaySetupQueue
{
    Task<AutopaySetupLease?> ClaimAsync(CancellationToken cancellationToken);
    Task<bool> CompleteAsync(AutopaySetupLease lease, AutopaySetupObservation observation, CancellationToken cancellationToken);
}
public interface IAutopaySetupGateway
{
    Task<AutopaySetupObservation> CreateAsync(AutopaySetupLease lease, CancellationToken cancellationToken);
    // No known session means read-only original-operation investigation, never another setup creation.
    Task<AutopaySetupObservation> ReadAsync(AutopaySetupLease lease, CancellationToken cancellationToken);
}
public interface IAutopaySetupGatewayFactory
{
    IAutopaySetupGateway Open(AutopaySetupLease lease);
}
public sealed class AutopaySetupWorker(IAutopaySetupQueue queue, IAutopaySetupGatewayFactory gateways, TimeProvider time)
{
    public async Task<bool> ProcessOneAsync(CancellationToken token = default)
    {
        var lease = await queue.ClaimAsync(token); if (lease is null) return false;
        using var operation = OfficeTelemetry.Begin(OfficeOperationKind.AutopaySetup, lease.Original.OriginalOperationId);
        AutopaySetupObservation observation; IAutopaySetupGateway? gateway = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var now = time.GetUtcNow();
            if (!lease.Original.Owns(lease.Token, now)) observation = new(AutopaySetupOutcome.Unknown);
            else
            {
                gateway = gateways.Open(lease);
                var readOnly = lease.ReadOnly || lease.Authorization.SetupSessionId is not null || !lease.Original.WithinOriginalWriteWindow(now);
                observation = await (readOnly ? gateway.ReadAsync(lease, deadline.Token) : gateway.CreateAsync(lease, deadline.Token)).WaitAsync(deadline.Token);
            }
        }
        catch (Exception) { observation = new(AutopaySetupOutcome.Unknown); }
        finally { (gateway as IDisposable)?.Dispose(); }
        // A provider response may arrive while shutdown is requested. Persist its outcome with an independent bounded token.
        using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var completed = await queue.CompleteAsync(lease, observation, completion.Token);
        operation.Result = !completed ? OfficeOutcome.LeaseLost : observation.Outcome switch { AutopaySetupOutcome.Verified => OfficeOutcome.Success, AutopaySetupOutcome.Invalid => OfficeOutcome.Failure, _ => OfficeOutcome.Uncertain };
        return true;
    }
}
