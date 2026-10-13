namespace AllenBuilding.Domain.Billing;

// A durable read of one untrusted evidence reference against one immutable original capture. It cannot issue a refund.
public sealed class CashSourceWork
{
    private CashSourceWork() { }
    public Guid Id { get; private set; }
    public Guid AttemptId { get; private set; }
    public Guid AccountId { get; private set; }
    public string ProviderAccount { get; private set; } = "";
    public bool LiveMode { get; private set; }
    public string SourceId { get; private set; } = "";
    public string ApiVersion { get; private set; } = CheckoutProfile.ApiContract;
    public Guid OperationId { get; private set; }
    public string ActorId { get; private set; } = "";
    public CollectionWorkState State { get; private set; }
    public Guid LeaseToken { get; private set; }
    public DateTimeOffset? LeaseUntil { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset CycleStartedAt { get; private set; }
    public int Attempts { get; private set; }
    public int Failures { get; private set; }
    public DateTimeOffset? FirstFailureAt { get; private set; }
    public string? LastError { get; private set; }
    public string? OriginTraceId { get; private set; }
    public string? OriginSpanId { get; private set; }

    public static CashSourceWork Enqueue(PaymentAttempt original, string sourceId, Guid operationId, string actorId, DateTimeOffset now, string? traceId = null, string? spanId = null)
    {
        if (original is null || original.State != PaymentAttemptState.Settled || !PaymentReferences.Valid(original.PaymentId!, "pi_") ||
            (!PaymentReferences.Valid(sourceId, "re_") && !CashMovement.DisputeReference(sourceId)) || operationId == Guid.Empty || string.IsNullOrWhiteSpace(actorId) || actorId.Length > 450 || now < original.CreatedAt ||
            (traceId is not null && (traceId.Length != 32 || !traceId.All(Uri.IsHexDigit))) || (spanId is not null && (spanId.Length != 16 || !spanId.All(Uri.IsHexDigit))))
            throw new ArgumentException("Source investigation requires an authorized original captured payment and a bounded refund/dispute reference.");
        return new()
        {
            Id = Guid.NewGuid(),
            AttemptId = original.Id,
            AccountId = original.AccountId,
            ProviderAccount = original.ProviderAccount,
            LiveMode = original.LiveMode,
            SourceId = sourceId,
            OperationId = operationId,
            ActorId = actorId,
            State = CollectionWorkState.Queued,
            CreatedAt = now.ToUniversalTime(),
            CycleStartedAt = now.ToUniversalTime(),
            NextAttemptAt = now.ToUniversalTime(),
            OriginTraceId = traceId,
            OriginSpanId = spanId
        };
    }
    public bool Eligible(DateTimeOffset now) => (State == CollectionWorkState.Queued && NextAttemptAt <= now) || (State == CollectionWorkState.Leased && LeaseUntil <= now);
    public bool Owns(Guid token, DateTimeOffset now) => State == CollectionWorkState.Leased && token != Guid.Empty && LeaseToken == token && LeaseUntil > now;
    public bool Claim(DateTimeOffset now)
    {
        if (!Eligible(now)) return false;
        if (State == CollectionWorkState.Leased) Fail(now, "LeaseExpired");
        if (Failures >= 5 || now >= CycleStartedAt.AddHours(24)) { Review("RetryLimit"); return false; }
        State = CollectionWorkState.Leased; LeaseToken = Guid.NewGuid(); LeaseUntil = now.AddMinutes(2); Attempts = checked(Attempts + 1); return true;
    }
    public bool Complete(Guid token, CollectionWorkOutcome outcome, DateTimeOffset now)
    {
        if (!Owns(token, now) || !Enum.IsDefined(outcome) || outcome == CollectionWorkOutcome.Suppressed) return false;
        LeaseUntil = null;
        switch (outcome)
        {
            case CollectionWorkOutcome.Finished: State = CollectionWorkState.Completed; LastError = null; break;
            case CollectionWorkOutcome.Review: Review("CanonicalReviewRequired"); break;
            case CollectionWorkOutcome.Retry:
                Fail(now, "ProviderUnknown");
                if (Failures >= 5 || now >= CycleStartedAt.AddHours(24)) Review("RetryLimit");
                else { State = CollectionWorkState.Queued; NextAttemptAt = now.AddSeconds(30 * (1 << Math.Min(Failures - 1, 4))); }
                break;
            default:
                if (now >= CycleStartedAt.AddHours(24)) Review("RetryLimit");
                else { State = CollectionWorkState.Queued; LastError = null; NextAttemptAt = now.AddMinutes(1); }
                break;
        }
        return true;
    }
    public void RequestAgain(DateTimeOffset now)
    {
        if ((State == CollectionWorkState.Leased && LeaseUntil > now) || now < CreatedAt) throw new InvalidOperationException("An active source reader cannot be replaced.");
        State = CollectionWorkState.Queued; CycleStartedAt = now.ToUniversalTime(); NextAttemptAt = now.ToUniversalTime(); LeaseToken = Guid.NewGuid(); LeaseUntil = null;
        Failures = 0; FirstFailureAt = null; LastError = null;
    }
    private void Review(string reason) { State = CollectionWorkState.NeedsReview; LeaseUntil = null; LeaseToken = Guid.NewGuid(); LastError = reason; }
    private void Fail(DateTimeOffset now, string reason) { Failures = checked(Failures + 1); FirstFailureAt ??= now.ToUniversalTime(); LastError = reason; }
}
