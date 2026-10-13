namespace AllenBuilding.Domain.Billing;

// Public, immutable request settings. Credentials are resolved separately at execution.
public sealed record CheckoutProfile
{
    public const string ApiContract = "2026-09-30.endive";
    public string ApiVersion { get; }
    public string PublicOrigin { get; }
    public string PaymentMethodConfiguration { get; }
    public CheckoutProfile(string publicOrigin, string paymentMethodConfiguration, string apiVersion = ApiContract)
    {
        if (apiVersion != ApiContract || !PaymentReferences.Valid(paymentMethodConfiguration, "pmc_") ||
            !Uri.TryCreate(publicOrigin, UriKind.Absolute, out var origin) || origin.Scheme != "https" ||
            origin.UserInfo.Length > 0 || origin.AbsolutePath != "/" || origin.Query.Length > 0 || origin.Fragment.Length > 0 || publicOrigin.Length > 512)
            throw new ArgumentException("Checkout requires the supported API, a root HTTPS origin and a verified payment-method configuration.");
        ApiVersion = apiVersion; PublicOrigin = origin.GetLeftPart(UriPartial.Authority); PaymentMethodConfiguration = paymentMethodConfiguration;
    }
}

public enum CollectionWorkState { Queued, Leased, NeedsReview, Completed, Suppressed }
public enum CollectionWorkOutcome { Waiting, Retry, Review, Finished, Suppressed }

// One durable original operation. Polling and retries never mint another economic command.
public sealed class CollectionWork
{
    private CollectionWork() { }
    public Guid AttemptId { get; private set; }
    public string ApiVersion { get; private set; } = "";
    public string PublicOrigin { get; private set; } = "";
    public string PaymentMethodConfiguration { get; private set; } = "";
    public CollectionWorkState State { get; private set; }
    public Guid LeaseToken { get; private set; }
    public DateTimeOffset? LeaseUntil { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset CycleStartedAt { get; private set; }
    public bool ReadOnlyReconciliation { get; private set; }
    public int Attempts { get; private set; }
    public int Failures { get; private set; }
    public DateTimeOffset? FirstFailureAt { get; private set; }
    public string? LastError { get; private set; }
    public string? OriginTraceId { get; private set; }
    public string? OriginSpanId { get; private set; }
    public CheckoutProfile Profile => new(PublicOrigin, PaymentMethodConfiguration, ApiVersion);

    public static CollectionWork Enqueue(PaymentAttempt original, CheckoutProfile profile, string? traceId = null, string? spanId = null)
    {
        if (original is null || original.State != PaymentAttemptState.Admitted || profile is null ||
            (traceId is not null && (traceId.Length != 32 || !traceId.All(Uri.IsHexDigit))) ||
            (spanId is not null && (spanId.Length != 16 || !spanId.All(Uri.IsHexDigit)))) throw new ArgumentException("A new original operation and valid dispatch profile are required.");
        return new()
        {
            AttemptId = original.Id,
            ApiVersion = profile.ApiVersion,
            PublicOrigin = profile.PublicOrigin,
            PaymentMethodConfiguration = profile.PaymentMethodConfiguration,
            State = CollectionWorkState.Queued,
            CreatedAt = original.CreatedAt,
            CycleStartedAt = original.CreatedAt,
            NextAttemptAt = original.CreatedAt,
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
        if (Failures >= 5 || (Attempts > 0 && now >= CycleStartedAt.AddHours(24)) || (FirstFailureAt is { } first && now >= first.AddHours(24))) { Review("RetryLimit"); return false; }
        State = CollectionWorkState.Leased; LeaseToken = Guid.NewGuid(); LeaseUntil = now.AddMinutes(2); Attempts = checked(Attempts + 1); return true;
    }
    public bool Complete(Guid token, CollectionWorkOutcome outcome, DateTimeOffset now)
    {
        if (!Owns(token, now) || !Enum.IsDefined(outcome)) return false;
        LeaseUntil = null;
        switch (outcome)
        {
            case CollectionWorkOutcome.Finished: State = CollectionWorkState.Completed; LastError = null; break;
            case CollectionWorkOutcome.Suppressed: State = CollectionWorkState.Suppressed; LastError = "NotDispatched"; break;
            case CollectionWorkOutcome.Review: Review("CanonicalReviewRequired"); break;
            case CollectionWorkOutcome.Retry:
                Fail(now, "ProviderUnknown");
                if (Failures >= 5 || now >= FirstFailureAt!.Value.AddHours(24)) Review("RetryLimit");
                else { State = CollectionWorkState.Queued; NextAttemptAt = now.AddSeconds(30 * (1 << Math.Min(Failures - 1, 4))); }
                break;
            default: State = CollectionWorkState.Queued; LastError = null; NextAttemptAt = now.AddMinutes(1); break;
        }
        return true;
    }
    public void RequestReconciliation(PaymentAttempt original, DateTimeOffset now)
    {
        if (original.Id != AttemptId || original.SessionId is null || !PaymentReferences.Valid(original.SessionId, "cs_") || original.State == PaymentAttemptState.Admitted ||
            (State == CollectionWorkState.Leased && LeaseUntil > now)) throw new InvalidOperationException("Reconciliation requires the known original session and no active worker lease.");
        ReadOnlyReconciliation = true; CycleStartedAt = now.ToUniversalTime(); State = CollectionWorkState.Queued; NextAttemptAt = now.ToUniversalTime();
        LeaseToken = Guid.NewGuid(); LeaseUntil = null; Failures = 0; FirstFailureAt = null; LastError = null;
    }
    public bool FinishRecorded(DateTimeOffset now)
    {
        if (!Eligible(now)) return false;
        State = CollectionWorkState.Completed; LeaseUntil = null; LeaseToken = Guid.NewGuid(); LastError = null; return true;
    }
    public void Review(string reason)
    {
        if (reason is not ("RetryLimit" or "CanonicalReviewRequired" or "OriginalWindowEnded" or "AdmissionChanged")) throw new ArgumentException("Invalid bounded recovery reason.");
        State = CollectionWorkState.NeedsReview; LeaseUntil = null; LeaseToken = Guid.NewGuid(); LastError = reason;
    }
    private void Fail(DateTimeOffset now, string reason) { Failures = checked(Failures + 1); FirstFailureAt ??= now; LastError = reason; }
}
