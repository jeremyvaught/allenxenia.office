namespace AllenBuilding.Domain.Billing;

public enum PaymentEventState { Pending, Processing, NeedsReview, Processed }

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
    public Guid LeaseToken { get; private set; }
    public DateTimeOffset? LeaseUntil { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public int Attempts { get; private set; }
    public int Failures { get; private set; }
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
            NextAttemptAt = now.ToUniversalTime(),
            Version = Guid.NewGuid(),
            State = supportedVersion ? PaymentEventState.Pending : PaymentEventState.NeedsReview,
            ReviewReason = supportedVersion ? null : "UnsupportedApiVersion"
        };
    }
    public bool Eligible(DateTimeOffset now) => (State == PaymentEventState.Pending && NextAttemptAt <= now) || (State == PaymentEventState.Processing && LeaseUntil <= now);
    public bool Owns(Guid token, DateTimeOffset now) => State == PaymentEventState.Processing && token != Guid.Empty && LeaseToken == token && LeaseUntil > now;
    public bool Claim(DateTimeOffset now)
    {
        if (!Eligible(now)) return false;
        if (State == PaymentEventState.Processing) Failures = checked(Failures + 1);
        if (Failures >= 5 || now >= ReceivedAt.AddHours(24)) { Review("RetryLimit"); return false; }
        State = PaymentEventState.Processing; LeaseToken = Guid.NewGuid(); LeaseUntil = now.AddMinutes(2); Attempts = checked(Attempts + 1); Version = Guid.NewGuid(); return true;
    }
    public bool Complete(Guid token, bool processed, bool retry, string? reason, DateTimeOffset now)
    {
        if (!Owns(token, now)) return false;
        if (processed && retry) throw new ArgumentException("A processed event cannot retry.");
        if (processed) { State = PaymentEventState.Processed; ReviewReason = null; }
        else if (retry)
        {
            Failures = checked(Failures + 1);
            if (Failures >= 5 || now >= ReceivedAt.AddHours(24)) Review("RetryLimit");
            else { State = PaymentEventState.Pending; NextAttemptAt = now.AddSeconds(30 * (1 << Math.Min(Failures - 1, 4))); ReviewReason = "ProviderUnknown"; }
        }
        else Review(reason ?? "CanonicalReviewRequired");
        LeaseUntil = null; Version = Guid.NewGuid(); return true;
    }
    private void Review(string reason)
    {
        if (reason is not ("RetryLimit" or "ProviderUnknown" or "UnsupportedResource" or "UnmappedResource" or "ProviderMismatch" or "CanonicalReviewRequired")) throw new ArgumentException("Invalid event recovery reason.");
        State = PaymentEventState.NeedsReview; ReviewReason = reason; LeaseUntil = null; LeaseToken = Guid.NewGuid(); Version = Guid.NewGuid();
    }
    public bool SameEnvelope(PaymentEventInbox other) => ProviderAccount == other.ProviderAccount && LiveMode == other.LiveMode &&
        EventId == other.EventId && EventType == other.EventType && ResourceId == other.ResourceId && ApiVersion == other.ApiVersion;
}
