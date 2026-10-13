using AllenBuilding.Application.Diagnostics;
using AllenBuilding.Domain.Billing;

namespace AllenBuilding.Application.Billing;

public sealed record CashSourceLease(Guid Id, PaymentAttempt Original, string SourceId, string ApiVersion, Guid Token, Guid OperationId,
    string? OriginTraceId = null, string? OriginSpanId = null);
public interface ICashSourceQueue
{
    Task<CashSourceLease?> ClaimAsync(CancellationToken cancellationToken);
    Task<bool> CompleteAsync(CashSourceLease lease, CashObservation observation, CancellationToken cancellationToken);
}

// Scoped worker: the reader can only retrieve evidence; durable queue completion must fence projection atomically.
public sealed class CashSourceWorker(ICashSourceQueue queue, ICashMovementReader reader)
{
    public async Task<bool> ProcessOneAsync(CancellationToken cancellationToken = default)
    {
        var lease = await queue.ClaimAsync(cancellationToken); if (lease is null) return false;
        using var operation = OfficeTelemetry.Begin(OfficeOperationKind.CashSource, lease.OperationId, OfficeTelemetry.Origin(lease.OriginTraceId, lease.OriginSpanId));
        CashObservation observation;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(30));
            observation = lease.Id == Guid.Empty || lease.Token == Guid.Empty || lease.ApiVersion != CheckoutProfile.ApiContract || lease.Original.State != PaymentAttemptState.Settled ||
                (!PaymentReferences.Valid(lease.SourceId, "re_") && !CashMovement.DisputeReference(lease.SourceId))
                ? new(CashObservationState.Invalid) : await reader.ReadAsync(lease.Original, lease.SourceId, deadline.Token).WaitAsync(deadline.Token);
        }
        catch (Exception) { observation = new(CashObservationState.Unknown); }
        if (observation is null || !Enum.IsDefined(observation.State) || (observation.State == CashObservationState.Verified &&
            (observation.Movements is not { Count: >= 1 and <= 25 } || observation.Movements.Any(x => x is null || x.SourceId != lease.SourceId ||
                x.ProviderAccount != lease.Original.ProviderAccount || x.LiveMode != lease.Original.LiveMode || x.AccountId != lease.Original.AccountId ||
                x.AttemptId != lease.Original.Id || x.CustomerId != lease.Original.CustomerId || x.PaymentId != lease.Original.PaymentId))))
            observation = new(CashObservationState.Invalid);
        using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var completed = await queue.CompleteAsync(lease, observation, completion.Token);
        operation.Result = !completed ? OfficeOutcome.LeaseLost : observation.State switch
        { CashObservationState.Unknown => OfficeOutcome.Uncertain, CashObservationState.Invalid => OfficeOutcome.Failure, CashObservationState.Pending => OfficeOutcome.Retry, _ => OfficeOutcome.Success };
        return true;
    }
}
