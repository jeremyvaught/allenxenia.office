using AllenBuilding.Application.Diagnostics;

namespace AllenBuilding.Application.Notifications;

public sealed record MessageLease(Guid Id, Guid Token, Guid? OperationId = null, string? OriginTraceId = null, string? OriginSpanId = null);
public sealed record EmailEnvelope(Guid MessageId, string Recipient, string Subject, string Text);
public enum DeliveryOutcome { Delivered, Retryable, Uncertain, PermanentFailure, Suppressed }
public sealed record DeliveryResult(DeliveryOutcome Outcome, string? Code = null);

public interface IEmailTransport
{
    Task<DeliveryResult> SendAsync(EmailEnvelope envelope, CancellationToken cancellationToken);
}

public interface INotificationQueue
{
    Task<MessageLease?> ClaimAsync(CancellationToken cancellationToken);
    Task<EmailEnvelope?> ComposeAsync(MessageLease lease, CancellationToken cancellationToken);
    Task<bool> CompleteAsync(MessageLease lease, DeliveryResult result, CancellationToken cancellationToken);
}

public sealed class NotificationDispatcher(INotificationQueue queue, IEmailTransport transport)
{
    public async Task<bool> ProcessOneAsync(CancellationToken cancellationToken = default)
    {
        var lease = await queue.ClaimAsync(cancellationToken);
        if (lease is null) return false;
        using var operation = OfficeTelemetry.Begin(OfficeOperationKind.NotificationDispatch, lease.OperationId, OfficeTelemetry.Origin(lease.OriginTraceId, lease.OriginSpanId), lease.Id);
        DeliveryResult result;
        try
        {
            var envelope = await queue.ComposeAsync(lease, cancellationToken);
            if (envelope is null) result = new(DeliveryOutcome.Suppressed, "NoLongerEligible");
            else
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                result = await transport.SendAsync(envelope, deadline.Token).WaitAsync(deadline.Token);
            }
        }
        catch (OperationCanceledException) { result = new(DeliveryOutcome.Uncertain, "DeliveryInterrupted"); }
        catch (System.Security.Cryptography.CryptographicException) { result = new(DeliveryOutcome.PermanentFailure, "ProtectedPayloadUnavailable"); }
        catch (System.Text.Json.JsonException) { result = new(DeliveryOutcome.PermanentFailure, "InvalidMessage"); }
        catch (Exception) { result = new(DeliveryOutcome.Uncertain, "DeliveryUnknown"); }
        // Use an independent bounded token so shutdown cannot abandon the known result in Sending.
        using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var completed = await queue.CompleteAsync(lease, result, completion.Token);
        operation.Result = !completed ? OfficeOutcome.LeaseLost : result.Outcome switch
        {
            DeliveryOutcome.Delivered => OfficeOutcome.Success,
            DeliveryOutcome.Retryable => OfficeOutcome.Retry,
            DeliveryOutcome.Suppressed => OfficeOutcome.Suppressed,
            DeliveryOutcome.Uncertain => OfficeOutcome.Uncertain,
            _ => OfficeOutcome.Failure
        };
        return true;
    }
}
