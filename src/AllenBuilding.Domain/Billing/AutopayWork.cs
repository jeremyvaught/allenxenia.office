namespace AllenBuilding.Domain.Billing;

public enum AutopayWorkStage { Setup, Activate, Stop }
public sealed class AutopaySlot
{
    public Guid AccountId { get; set; }
    public Guid AuthorizationId { get; set; }
}

// Original economic commands and request settings survive retries; external calls occur between transactions.
public sealed class AutopayWork
{
    private AutopayWork() { }
    public Guid Id { get; private set; }
    public Guid AuthorizationId { get; private set; }
    public Guid AccountId { get; private set; }
    public AutopayWorkStage Stage { get; private set; }
    public Guid OriginalOperationId { get; private set; }
    public Guid ReviewedAuthorizationVersion { get; private set; }
    public string ProviderOperationKey { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset DeadlineAt { get; private set; }
    public CollectionWorkState State { get; private set; }
    public string ApiVersion { get; private set; } = "";
    public string PublicOrigin { get; private set; } = "";
    public string PaymentMethodConfiguration { get; private set; } = "";
    public DateTimeOffset? DispatchedAt { get; private set; }
    public Guid LeaseToken { get; private set; }
    public DateTimeOffset? LeaseUntil { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public int Attempts { get; private set; }
    public int Failures { get; private set; }
    public string? LastError { get; private set; }
    public Guid Version { get; private set; }
    public CheckoutProfile? Profile
    {
        get { if (PublicOrigin.Length == 0 || PaymentMethodConfiguration.Length == 0 || ApiVersion != CheckoutProfile.ApiContract) return null; try { return new(PublicOrigin, PaymentMethodConfiguration, ApiVersion); } catch (ArgumentException) { return null; } }
    }
    public bool Eligible(DateTimeOffset now) => State == CollectionWorkState.Queued && NextAttemptAt <= now || State == CollectionWorkState.Leased && LeaseUntil <= now;
    public bool Owns(Guid token, DateTimeOffset now) => State == CollectionWorkState.Leased && token != Guid.Empty && LeaseToken == token && LeaseUntil > now;
    public bool WithinOriginalWriteWindow(DateTimeOffset now) => DispatchedAt is { } started && now >= started && now < started.AddHours(23);
    public static AutopayWork Enqueue(AutopayAuthorization authorization, AutopayWorkStage stage, DateTimeOffset now, CheckoutProfile? profile = null)
    {
        if (authorization is null || !Enum.IsDefined(stage) || now < authorization.ConsentedAt || (stage switch
        { AutopayWorkStage.Setup => authorization.State != AutopayState.AwaitingMethod, AutopayWorkStage.Activate => authorization.State != AutopayState.AwaitingProvider, _ => authorization.State != AutopayState.StopPending || authorization.StopOperationId is null }))
            throw new ArgumentException("Provider work requires the original authorization and matching durable stage.");
        return new()
        {
            Id = Guid.NewGuid(),
            AuthorizationId = authorization.Id,
            AccountId = authorization.AccountId,
            Stage = stage,
            OriginalOperationId = stage == AutopayWorkStage.Stop ? authorization.StopOperationId!.Value : authorization.OperationId,
            ReviewedAuthorizationVersion = authorization.Version,
            ProviderOperationKey = stage switch { AutopayWorkStage.Setup => authorization.SetupOperationKey, AutopayWorkStage.Activate => authorization.SubscriptionOperationKey, _ => authorization.StopOperationKey! },
            CreatedAt = now.ToUniversalTime(),
            NextAttemptAt = now.ToUniversalTime(),
            State = CollectionWorkState.Queued,
            Version = Guid.NewGuid(),
            DeadlineAt = stage switch { AutopayWorkStage.Setup => now.AddHours(48), AutopayWorkStage.Activate => authorization.FirstAnchorUtc < now.AddHours(48) ? authorization.FirstAnchorUtc : now.AddHours(48), _ => authorization.StopEffectiveAt!.Value.AddHours(24) > now.AddHours(48) ? authorization.StopEffectiveAt.Value.AddHours(24) : now.AddHours(48) },
            ApiVersion = profile?.ApiVersion ?? "",
            PublicOrigin = profile?.PublicOrigin ?? "",
            PaymentMethodConfiguration = profile?.PaymentMethodConfiguration ?? ""
        };
    }
    public bool Claim(DateTimeOffset now)
    {
        if (!Eligible(now)) return false;
        if (State == CollectionWorkState.Leased) Failures = checked(Failures + 1);
        if (Failures >= 5 || now >= DeadlineAt) { Review("RetryLimit"); return false; }
        State = CollectionWorkState.Leased; LeaseToken = Guid.NewGuid(); LeaseUntil = now.AddMinutes(2); Attempts = checked(Attempts + 1); Version = Guid.NewGuid(); return true;
    }
    public void BeginDispatch(DateTimeOffset now)
    {
        if (!Owns(LeaseToken, now) || DispatchedAt is not null) throw new InvalidOperationException("Only the first leased original operation can mark dispatch.");
        DispatchedAt = now.ToUniversalTime(); Version = Guid.NewGuid();
    }
    public void Suppress(DateTimeOffset now)
    {
        if (!Eligible(now) || DispatchedAt is not null) throw new InvalidOperationException("Uncertain provider work cannot be suppressed.");
        State = CollectionWorkState.Suppressed; LastError = "AdmissionChanged"; LeaseUntil = null; LeaseToken = Guid.NewGuid(); Version = Guid.NewGuid();
    }
    public bool Complete(Guid token, CollectionWorkOutcome outcome, DateTimeOffset now)
    {
        if (!Owns(token, now) || !Enum.IsDefined(outcome) || outcome == CollectionWorkOutcome.Suppressed) return false;
        LeaseUntil = null; Version = Guid.NewGuid();
        if (outcome == CollectionWorkOutcome.Finished) { State = CollectionWorkState.Completed; LastError = null; }
        else if (outcome == CollectionWorkOutcome.Review) Review("CanonicalReviewRequired");
        else if (outcome == CollectionWorkOutcome.Retry)
        {
            Failures = checked(Failures + 1); LastError = "ProviderUnknown";
            if (Failures >= 5 || now >= DeadlineAt) Review("RetryLimit");
            else { State = CollectionWorkState.Queued; NextAttemptAt = now.AddSeconds(30 * (1 << Math.Min(Failures - 1, 4))); }
        }
        else { State = CollectionWorkState.Queued; LastError = null; NextAttemptAt = now.AddMinutes(1); }
        return true;
    }
    public void Review(string reason)
    {
        if (State is CollectionWorkState.Completed or CollectionWorkState.Suppressed || reason is not ("RetryLimit" or "CanonicalReviewRequired" or "OriginalProfileMissing" or "AdmissionChanged")) throw new InvalidOperationException("Invalid investigation transition.");
        State = CollectionWorkState.NeedsReview; LeaseUntil = null; LeaseToken = Guid.NewGuid(); LastError = reason; Version = Guid.NewGuid();
    }
}
