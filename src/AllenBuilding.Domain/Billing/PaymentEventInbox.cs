namespace AllenBuilding.Domain.Billing;

public enum PaymentEventState { Pending, NeedsReview, Processed }

// Minimal authenticated envelope. Full provider payloads never enter persistence or diagnostics.
public sealed class PaymentEventInbox
{
    private PaymentEventInbox() { }
    public Guid Id { get; private set; }
    public string ProviderAccount { get; private set; } = "";
    public bool LiveMode { get; private set; }
    public string EventId { get; private set; } = "";
    public string EventType { get; private set; } = "";
    public string ResourceId { get; private set; } = "";
    public string ApiVersion { get; private set; } = "";
    public string PayloadHash { get; private set; } = "";
    public PaymentEventState State { get; private set; }
    public string? ReviewReason { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }
    public Guid Version { get; private set; }
    public static PaymentEventInbox Receive(string account, bool liveMode, string eventId, string eventType, string resourceId,
        string apiVersion, string payloadHash, bool supportedVersion, DateTimeOffset now)
    {
        if (!PaymentReferences.Valid(account, "acct_") || !PaymentReferences.Valid(eventId, "evt_") ||
            string.IsNullOrWhiteSpace(eventType) || eventType.Length > 100 || eventType.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(resourceId) || resourceId.Length > 255 || resourceId.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(apiVersion) || apiVersion.Length > 64 || apiVersion.Any(char.IsControl) ||
            payloadHash.Length != 64 || !payloadHash.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid authenticated payment envelope.");
        return new()
        {
            Id = Guid.NewGuid(),
            ProviderAccount = account,
            LiveMode = liveMode,
            EventId = eventId,
            EventType = eventType,
            ResourceId = resourceId,
            ApiVersion = apiVersion,
            PayloadHash = payloadHash,
            ReceivedAt = now.ToUniversalTime(),
            Version = Guid.NewGuid(),
            State = supportedVersion ? PaymentEventState.Pending : PaymentEventState.NeedsReview,
            ReviewReason = supportedVersion ? null : "UnsupportedApiVersion"
        };
    }
    public bool SameEnvelope(PaymentEventInbox other) => ProviderAccount == other.ProviderAccount && LiveMode == other.LiveMode &&
        EventId == other.EventId && EventType == other.EventType && ResourceId == other.ResourceId && ApiVersion == other.ApiVersion;
}
