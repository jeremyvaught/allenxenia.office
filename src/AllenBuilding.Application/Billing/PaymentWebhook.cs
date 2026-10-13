namespace AllenBuilding.Application.Billing;

public enum WebhookCode { Accepted, Duplicate, Ignored, Rejected, Unavailable }
public interface IPaymentWebhookReceiver
{
    Task<WebhookCode> ReceiveAsync(ReadOnlyMemory<byte> body, string signature, CancellationToken cancellationToken = default);
}
public sealed class DisabledPaymentWebhookReceiver : IPaymentWebhookReceiver
{
    public Task<WebhookCode> ReceiveAsync(ReadOnlyMemory<byte> body, string signature, CancellationToken cancellationToken = default) => Task.FromResult(WebhookCode.Unavailable);
}
