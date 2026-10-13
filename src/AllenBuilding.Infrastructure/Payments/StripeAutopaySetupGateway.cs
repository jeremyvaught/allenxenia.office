using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using Stripe;
using Stripe.Checkout;
using Session = Stripe.Checkout.Session;

namespace AllenBuilding.Infrastructure.Payments;

public sealed class StripeAutopaySetupGatewayFactory(StripeCustomerCredentials credentials, TimeProvider time) : IAutopaySetupGatewayFactory, IAutopayMethodGatewayFactory
{
    public IAutopaySetupGateway Open(AutopaySetupLease lease)
    {
        if (lease.Authorization.ProviderAccount != credentials.ProviderAccount || lease.Authorization.LiveMode != credentials.LiveMode ||
            lease.Original.Profile?.ApiVersion != StripeConfiguration.ApiVersion)
            throw new InvalidOperationException("The original setup scope or request profile is unavailable.");
        return new StripeAutopaySetupGateway(credentials, time);
    }
    public IAutopayMethodGateway Open(AutopayMethodSnapshot original) => (IAutopayMethodGateway)Open(new AutopaySetupLease(original.Original, original.Authorization, Guid.Empty, true));
}

// Prepared adapter. Registration requires sandbox proof and the activation/stop/reconciliation workers.
// It saves a method only: no subscription, invoice, PaymentIntent or collection request exists here.
public sealed class StripeAutopaySetupGateway : IAutopaySetupGateway, IAutopayMethodGateway, IDisposable
{
    private readonly StripeCustomerCredentials _credentials;
    private readonly TimeProvider _time;
    private readonly StripeClient _client;
    private readonly HttpClient? _ownedHttp;
    public StripeAutopaySetupGateway(StripeCustomerCredentials credentials, TimeProvider time) : this(credentials, time, null) { }
    internal StripeAutopaySetupGateway(StripeCustomerCredentials credentials, TimeProvider time, StripeClient? testClient)
    {
        credentials.Validate(); _credentials = new() { ApiKey = credentials.ApiKey, ProviderAccount = credentials.ProviderAccount, LiveMode = credentials.LiveMode }; _time = time;
        if (testClient is null) _ownedHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _client = testClient ?? new StripeClient(new StripeClientOptions
        {
            ApiKey = credentials.ApiKey,
            HttpClient = new SystemNetHttpClient(_ownedHttp, maxNetworkRetries: 0, enableTelemetry: false)
        });
    }

    public Task<AutopaySetupObservation> CreateAsync(AutopaySetupLease lease, CancellationToken cancellationToken)
        => Run(async token =>
        {
            if (!CanCreate(lease) || !await Owner(lease.Authorization, token)) return Invalid();
            var original = lease.Original; var auth = lease.Authorization; var profile = original.Profile!; var metadata = Metadata(lease);
            var options = new SessionCreateOptions
            {
                Mode = "setup",
                UiMode = "hosted_page",
                Currency = "usd",
                Customer = auth.CustomerId,
                ClientReferenceId = auth.Id.ToString("D"),
                Metadata = metadata,
                PaymentMethodConfiguration = profile.PaymentMethodConfiguration,
                // Filter dynamic eligibility; do not send the removed static payment_method_types parameter.
                AllowedPaymentMethodTypes = ["card"],
                SuccessUrl = profile.PublicOrigin.TrimEnd('/') + "/Autopay/Return?authorizationId=" + auth.Id.ToString("D"),
                CancelUrl = profile.PublicOrigin.TrimEnd('/') + "/Autopay/Return?authorizationId=" + auth.Id.ToString("D"),
                ExpiresAt = ExpectedExpiry(original).UtcDateTime,
                SetupIntentData = new() { Metadata = metadata }
            };
            // Ownership reads can consume a lease/window. Recheck immediately before the transport write.
            token.ThrowIfCancellationRequested(); if (!CanCreate(lease)) return Invalid();
            var created = await new SessionService(_client).CreateAsync(options, new RequestOptions { IdempotencyKey = original.ProviderOperationKey }, token);
            if (!Matches(lease, created)) return Invalid();
            return await Observe(lease, created.Id, token); // POST acknowledgment alone is never canonical evidence.
        }, cancellationToken);

    public Task<AutopaySetupObservation> ReadAsync(AutopaySetupLease lease, CancellationToken cancellationToken)
        => Run(async token =>
        {
            if (!Valid(lease) || !await Owner(lease.Authorization, token)) return Invalid();
            if (lease.Authorization.SetupSessionId is { } known) return await Observe(lease, known, token);
            var start = lease.Original.DispatchedAt!.Value;
            var catalog = await new SessionService(_client).ListAsync(new SessionListOptions
            {
                Customer = lease.Authorization.CustomerId,
                Limit = 26,
                Created = new DateRangeOptions { GreaterThanOrEqual = Floor(start).UtcDateTime, LessThanOrEqual = ExpectedExpiry(lease.Original).UtcDateTime }
            }, cancellationToken: token);
            if (catalog.Data is null || catalog.HasMore || catalog.Data.Count > 25 || catalog.Data.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != catalog.Data.Count ||
                catalog.Data.Any(x => !PaymentReferences.Valid(x.Id, "cs_") || x.CustomerId != lease.Authorization.CustomerId || x.Livemode != lease.Authorization.LiveMode ||
                    x.Created < Floor(start).UtcDateTime || x.Created > ExpectedExpiry(lease.Original).UtcDateTime)) return Invalid();
            var matches = catalog.Data.Where(x => MatchesMetadata(lease, x.Metadata)).ToArray();
            if (matches.Length == 0) return Unknown(); // Absence is not proof that an uncertain write failed.
            if (matches.Length != 1 || !Matches(lease, matches[0])) return Invalid();
            return await Observe(lease, matches[0].Id, token);
        }, cancellationToken);

    public async Task<AutopayMethodHandoff?> ReadAsync(AutopayMethodSnapshot original, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(20)); var token = deadline.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            var lease = new AutopaySetupLease(original.Original, original.Authorization, Guid.Empty, true); var auth = original.Authorization;
            if (!ValidOriginal(lease) || auth.State != AutopayState.AwaitingMethod || auth.StopOperationId is not null || auth.SetupState != AutopaySetupState.Waiting ||
                auth.SetupSessionId is null || auth.SetupExpiresAt is null || auth.SetupExpiresAt <= _time.GetUtcNow() || !await Owner(auth, token)) return null;
            var fact = await Observe(lease, auth.SetupSessionId, token);
            if (fact is not { Outcome: AutopaySetupOutcome.Verified, Setup.State: AutopaySetupState.Waiting }) return null;
            var final = await new SessionService(_client).GetAsync(auth.SetupSessionId, cancellationToken: token);
            if (!Matches(lease, final) || final.Status != "open" || auth.SetupExpiresAt != ExpectedExpiry(original.Original) || auth.SetupExpiresAt <= _time.GetUtcNow() ||
                !Uri.TryCreate(final.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "checkout.stripe.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.AbsoluteUri.Length > 4096) return null;
            return new(auth.SetupSessionId, auth.SetupExpiresAt!.Value, uri);
        }
        catch (Exception) { return null; } // Nothing private or credential-bearing is released on a failed read.
    }

    private async Task<AutopaySetupObservation> Observe(AutopaySetupLease lease, string sessionId, CancellationToken token)
    {
        if (!PaymentReferences.Valid(sessionId, "cs_")) return Invalid();
        var sessions = new SessionService(_client); var session = await sessions.GetAsync(sessionId, cancellationToken: token);
        if (session.Id != sessionId || !Matches(lease, session)) return Invalid();
        SetupIntent? intent = null; string? method = null; AutopaySetupState state;
        if (session.SetupIntentId is { } intentId)
        {
            if (!PaymentReferences.Valid(intentId, "seti_")) return Invalid();
            intent = await new SetupIntentService(_client).GetAsync(intentId, cancellationToken: token);
            if (!MatchesIntent(lease, intent, intentId)) return Invalid();
        }
        if (session.Status == "complete" && session.PaymentStatus == "no_payment_required")
        {
            if (intent is not { Status: "succeeded" } || !PaymentReferences.Valid(intent.PaymentMethodId!, "pm_")) return Invalid();
            var paymentMethod = await new PaymentMethodService(_client).GetAsync(intent.PaymentMethodId, cancellationToken: token);
            if (paymentMethod.Id != intent.PaymentMethodId || paymentMethod.CustomerId != lease.Authorization.CustomerId || paymentMethod.Livemode != lease.Authorization.LiveMode || paymentMethod.Type != "card" || paymentMethod.Card is null) return Invalid();
            var again = await new SetupIntentService(_client).GetAsync(intent.Id, cancellationToken: token);
            if (!MatchesIntent(lease, again, intent.Id) || again.Status != intent.Status || again.PaymentMethodId != intent.PaymentMethodId) return Invalid();
            method = paymentMethod.Id; state = AutopaySetupState.Ready;
        }
        else if (session.Status == "open" && session.PaymentStatus is "unpaid" or "no_payment_required" &&
            (intent is null || intent.Status is "requires_payment_method" or "requires_confirmation" or "requires_action" or "processing"))
        {
            if (ExpectedExpiry(lease.Original) <= _time.GetUtcNow()) return Invalid();
            state = AutopaySetupState.Waiting;
        }
        else if (session.Status == "expired" && session.PaymentStatus is "unpaid" or "no_payment_required" &&
            (intent is null || intent.Status is "canceled" or "requires_payment_method")) state = AutopaySetupState.Expired;
        else return Invalid();
        // Detect changed session/owner while its linked intent and method were being checked.
        var final = await sessions.GetAsync(sessionId, cancellationToken: token);
        if (final.Id != sessionId || !Matches(lease, final) || final.Status != session.Status || final.PaymentStatus != session.PaymentStatus || final.SetupIntentId != session.SetupIntentId ||
            !await Owner(lease.Authorization, token)) return Invalid();
        return new(AutopaySetupOutcome.Verified, new(lease.Authorization.ProviderAccount, lease.Authorization.LiveMode, lease.Authorization.AccountId,
            lease.Authorization.CustomerId, lease.Authorization.ConsentHash, sessionId, ExpectedExpiry(lease.Original), state, method, true, intent?.Usage == "off_session", _time.GetUtcNow()));
    }
    private bool CanCreate(AutopaySetupLease lease) => Valid(lease) && !lease.ReadOnly && lease.Authorization.SetupSessionId is null &&
        lease.Authorization.State == AutopayState.AwaitingMethod && lease.Authorization.StopOperationId is null && lease.Authorization.FirstAnchorUtc > _time.GetUtcNow() &&
        lease.Original.WithinOriginalWriteWindow(_time.GetUtcNow());
    private bool Valid(AutopaySetupLease lease) => lease.Original.Owns(lease.Token, _time.GetUtcNow()) && ValidOriginal(lease);
    private bool ValidOriginal(AutopaySetupLease lease) => lease.Original.Stage == AutopayWorkStage.Setup &&
        lease.Original.AuthorizationId == lease.Authorization.Id && lease.Original.AccountId == lease.Authorization.AccountId && lease.Original.OriginalOperationId == lease.Authorization.OperationId &&
        lease.Original.ProviderOperationKey == lease.Authorization.SetupOperationKey && lease.Original.Profile?.ApiVersion == StripeConfiguration.ApiVersion &&
        lease.Original.DispatchedAt is { } start && start >= lease.Authorization.ConsentedAt && start <= _time.GetUtcNow() &&
        lease.Authorization.ProviderAccount == _credentials.ProviderAccount && lease.Authorization.LiveMode == _credentials.LiveMode &&
        PaymentReferences.Valid(lease.Authorization.CustomerId, "cus_") && (lease.Authorization.SetupSessionId is null || PaymentReferences.Valid(lease.Authorization.SetupSessionId, "cs_"));
    private static DateTimeOffset Floor(DateTimeOffset value) => DateTimeOffset.FromUnixTimeSeconds(value.ToUnixTimeSeconds());
    private static DateTimeOffset ExpectedExpiry(AutopayWork original) => Floor(original.DispatchedAt!.Value.AddHours(24));
    private async Task<bool> Owner(AutopayAuthorization auth, CancellationToken token)
    {
        if ((await new AccountService(_client).GetSelfAsync(cancellationToken: token)).Id != auth.ProviderAccount) return false;
        var customer = await new CustomerService(_client).GetAsync(auth.CustomerId, cancellationToken: token);
        return customer.Id == auth.CustomerId && customer.Deleted != true && customer.Livemode == auth.LiveMode && customer.Metadata?.GetValueOrDefault("office_account_id") == auth.AccountId.ToString("D");
    }
    private static Dictionary<string, string> Metadata(AutopaySetupLease lease) => new()
    {
        ["office_account_id"] = lease.Authorization.AccountId.ToString("D"),
        ["office_autopay_id"] = lease.Authorization.Id.ToString("D"),
        ["office_operation_id"] = lease.Original.OriginalOperationId.ToString("D"),
        ["office_consent_hash"] = lease.Authorization.ConsentHash
    };
    private static bool MatchesMetadata(AutopaySetupLease lease, Dictionary<string, string>? metadata) => metadata is not null && Metadata(lease).All(x => metadata.GetValueOrDefault(x.Key) == x.Value);
    private static bool Matches(AutopaySetupLease lease, Session session) => PaymentReferences.Valid(session.Id, "cs_") && (lease.Authorization.SetupSessionId is null || lease.Authorization.SetupSessionId == session.Id) &&
        session.Mode == "setup" && session.UiMode == "hosted_page" && session.Livemode == lease.Authorization.LiveMode && session.CustomerId == lease.Authorization.CustomerId &&
        session.ClientReferenceId == lease.Authorization.Id.ToString("D") && session.ExpiresAt == ExpectedExpiry(lease.Original).UtcDateTime && MatchesMetadata(lease, session.Metadata) &&
        session.PaymentIntentId is null && session.SubscriptionId is null && session.InvoiceId is null && session.AmountTotal is null or 0 && session.AmountSubtotal is null or 0 &&
        (session.Currency is null or "usd") && session.PaymentMethodConfigurationDetails?.Id == lease.Original.PaymentMethodConfiguration;
    private static bool MatchesIntent(AutopaySetupLease lease, SetupIntent intent, string id) => intent.Id == id && intent.CustomerId == lease.Authorization.CustomerId &&
        intent.Livemode == lease.Authorization.LiveMode && intent.Usage == "off_session" && MatchesMetadata(lease, intent.Metadata);
    private static AutopaySetupObservation Invalid() => new(AutopaySetupOutcome.Invalid);
    private static AutopaySetupObservation Unknown() => new(AutopaySetupOutcome.Unknown);
    private static async Task<AutopaySetupObservation> Run(Func<CancellationToken, Task<AutopaySetupObservation>> operation, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try { deadline.Token.ThrowIfCancellationRequested(); return await operation(deadline.Token); }
        catch (StripeException) { return Unknown(); }
        catch (HttpRequestException) { return Unknown(); }
        catch (OperationCanceledException) { return Unknown(); }
        catch (Newtonsoft.Json.JsonException) { return Invalid(); }
        catch (System.Text.Json.JsonException) { return Invalid(); }
    }
    public void Dispose() => _ownedHttp?.Dispose();
}
