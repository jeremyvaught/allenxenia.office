namespace AllenBuilding.Domain.Billing;

public enum CustomerProvisionState { Queued, Leased, NeedsReview, Bound, Suppressed }
public enum CustomerProvisionOutcome { Verified, Unknown, Invalid }

// One permanent original operation per account/merchant/mode. Never reassign its customer.
public sealed class CustomerProvision
{
    private CustomerProvision() { }
    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }
    public string ActorId { get; private set; } = "";
    public string ProviderAccount { get; private set; } = "";
    public bool LiveMode { get; private set; }
    public string ApiVersion { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? DispatchedAt { get; private set; }
    public CustomerProvisionState State { get; private set; }
    public string? CustomerId { get; private set; }
    public Guid Version { get; private set; }
    public Guid LeaseToken { get; private set; }
    public DateTimeOffset? LeaseUntil { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public int Attempts { get; private set; }
    public int Failures { get; private set; }
    public string? LastError { get; private set; }
    public string? OriginTraceId { get; private set; }
    public string? OriginSpanId { get; private set; }
    public string ProviderIdempotencyKey => $"office:{(LiveMode ? "live" : "test")}:{Id:N}:customer:v1";

    public static CustomerProvision Enqueue(Guid accountId, string actorId, string providerAccount, bool liveMode, DateTimeOffset now,
        string? trace = null, string? span = null)
    {
        if (accountId == Guid.Empty || string.IsNullOrWhiteSpace(actorId) || actorId.Length > 450 || !PaymentReferences.Valid(providerAccount, "acct_") ||
            (trace is not null && (trace.Length != 32 || !trace.All(Uri.IsHexDigit))) || (span is not null && (span.Length != 16 || !span.All(Uri.IsHexDigit))))
            throw new ArgumentException("An authorized original account, actor and provider scope are required.");
        return new()
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            ActorId = actorId,
            ProviderAccount = providerAccount,
            LiveMode = liveMode,
            ApiVersion = CheckoutProfile.ApiContract,
            CreatedAt = now.ToUniversalTime(),
            NextAttemptAt = now.ToUniversalTime(),
            State = CustomerProvisionState.Queued,
            Version = Guid.NewGuid(),
            OriginTraceId = trace,
            OriginSpanId = span
        };
    }
    public bool Eligible(DateTimeOffset now) => (State == CustomerProvisionState.Queued && NextAttemptAt <= now) ||
        (State == CustomerProvisionState.Leased && LeaseUntil <= now);
    public bool Owns(Guid token, DateTimeOffset now) => State == CustomerProvisionState.Leased && token != Guid.Empty && LeaseToken == token && LeaseUntil > now;
    public bool CanCreate(DateTimeOffset now) => DispatchedAt is not null && now >= DispatchedAt && now < DispatchedAt.Value.AddHours(23);
    public bool Claim(DateTimeOffset now)
    {
        if (!Eligible(now)) return false;
        if (State == CustomerProvisionState.Leased) Failures = checked(Failures + 1);
        if (Failures >= 5 || (DispatchedAt is not null && !CanCreate(now))) { Review("OriginalWindowEnded"); return false; }
        State = CustomerProvisionState.Leased; LeaseToken = Guid.NewGuid(); LeaseUntil = now.AddMinutes(2);
        Attempts = checked(Attempts + 1); Version = Guid.NewGuid(); return true;
    }
    public void BeginDispatch(DateTimeOffset now)
    {
        if (!Owns(LeaseToken, now) || DispatchedAt is not null) throw new InvalidOperationException("Only the leased first dispatch can begin.");
        DispatchedAt = now.ToUniversalTime(); Version = Guid.NewGuid();
    }
    public void Suppress(DateTimeOffset now)
    {
        if (!Eligible(now) || DispatchedAt is not null) throw new InvalidOperationException("Only a never-dispatched operation can be suppressed.");
        State = CustomerProvisionState.Suppressed; LeaseUntil = null; LeaseToken = Guid.NewGuid(); LastError = "AccessEnded"; Version = Guid.NewGuid();
    }
    public bool Complete(Guid token, CustomerProvisionOutcome outcome, string? customerId, DateTimeOffset now)
    {
        if (!Owns(token, now) || !Enum.IsDefined(outcome)) return false;
        if (outcome == CustomerProvisionOutcome.Verified && (DispatchedAt is null || !PaymentReferences.Valid(customerId!, "cus_")))
            throw new ArgumentException("Binding requires verified canonical customer evidence.");
        LeaseUntil = null; Version = Guid.NewGuid();
        if (outcome == CustomerProvisionOutcome.Verified) { CustomerId = customerId; State = CustomerProvisionState.Bound; LastError = null; }
        else if (outcome == CustomerProvisionOutcome.Invalid) Review("CanonicalMismatch");
        else
        {
            Failures = checked(Failures + 1); LastError = "ProviderUnknown";
            if (Failures >= 5 || !CanCreate(now)) Review("RetryLimit");
            else { State = CustomerProvisionState.Queued; NextAttemptAt = now.AddSeconds(30 * (1 << Math.Min(Failures - 1, 4))); }
        }
        return true;
    }
    public void Review(string reason)
    {
        if (State is CustomerProvisionState.Bound or CustomerProvisionState.Suppressed || reason is not ("OriginalWindowEnded" or "RetryLimit" or "CanonicalMismatch" or "BindingConflict"))
            throw new InvalidOperationException("Invalid customer investigation transition.");
        State = CustomerProvisionState.NeedsReview; LeaseUntil = null; LeaseToken = Guid.NewGuid(); LastError = reason; Version = Guid.NewGuid();
    }
}
