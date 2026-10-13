using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using Stripe;
using Stripe.Checkout;
using Session = Stripe.Checkout.Session;

namespace AllenBuilding.Infrastructure.Payments;

// A class, rather than a record: diagnostics must never render the API credential.
public sealed class StripeCollectionOptions
{
    public string ApiKey { get; set; } = "";
    public string ProviderAccount { get; set; } = "";
    public bool LiveMode { get; set; }
    public string PublicOrigin { get; set; } = "";
    public string PaymentMethodConfiguration { get; set; } = "";

    public void Validate()
    {
        var mode = LiveMode ? "live" : "test";
        if ((!ApiKey.StartsWith("rk_" + mode + "_", StringComparison.Ordinal) && !ApiKey.StartsWith("sk_" + mode + "_", StringComparison.Ordinal)) ||
            ApiKey.Length <= 8 || ApiKey.Any(char.IsWhiteSpace) || !PaymentReferences.Valid(ProviderAccount, "acct_") ||
            !PaymentReferences.Valid(PaymentMethodConfiguration, "pmc_") || !Uri.TryCreate(PublicOrigin, UriKind.Absolute, out var origin) ||
            origin.Scheme != "https" || origin.UserInfo.Length > 0 || origin.AbsolutePath != "/" || origin.Query.Length > 0 || origin.Fragment.Length > 0)
            throw new InvalidOperationException("Stripe requires a mode-matched credential, account, verified payment-method configuration and root HTTPS origin.");
    }
}

// Not registered/enabled until durable dispatch, signed webhooks and exception recovery are complete.
public sealed class StripeCollectionGateway : ICollectionGateway, IDisposable
{
    private readonly StripeCollectionOptions _options;
    private readonly StripeClient _client;
    private readonly TimeProvider _time;
    private readonly HttpClient? _ownedHttp;

    public StripeCollectionGateway(StripeCollectionOptions options, TimeProvider time) : this(options, time, null) { }
    internal StripeCollectionGateway(StripeCollectionOptions options, TimeProvider time, StripeClient? testClient)
    {
        options.Validate(); _options = new StripeCollectionOptions
        {
            ApiKey = options.ApiKey,
            ProviderAccount = options.ProviderAccount,
            LiveMode = options.LiveMode,
            PublicOrigin = options.PublicOrigin,
            PaymentMethodConfiguration = options.PaymentMethodConfiguration
        }; _time = time;
        // No global StripeConfiguration mutation. The SDK pins its stable API contract.
        if (testClient is null) _ownedHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _client = testClient ?? new StripeClient(new StripeClientOptions
        {
            ApiKey = options.ApiKey,
            HttpClient = new SystemNetHttpClient(_ownedHttp, maxNetworkRetries: 0, enableTelemetry: false)
        });
    }

    public Task<ProviderCollectionObservation> CreateAsync(PaymentAttempt original, CancellationToken cancellationToken = default)
        => Run(async token =>
        {
            if (!ValidOriginal(original) || original.State is not (PaymentAttemptState.Dispatching or PaymentAttemptState.NeedsReview) ||
                _time.GetUtcNow() < original.CreatedAt || _time.GetUtcNow() >= original.CreatedAt.AddHours(23)) return Invalid();
            if (!await VerifyOwner(original, token)) return Invalid();
            var metadata = Metadata(original);
            var session = await new SessionService(_client).CreateAsync(new SessionCreateOptions
            {
                Mode = "payment",
                UiMode = "hosted",
                Customer = original.CustomerId,
                ClientReferenceId = original.Id.ToString("D"),
                Metadata = metadata,
                IntegrationIdentifier = "allen_xenia_office_qmztrnva",
                PaymentMethodConfiguration = _options.PaymentMethodConfiguration,
                AdaptivePricing = new SessionAdaptivePricingOptions { Enabled = false },
                SuccessUrl = _options.PublicOrigin.TrimEnd('/') + "/Billing/Return?attemptId=" + original.Id.ToString("D"),
                CancelUrl = _options.PublicOrigin.TrimEnd('/') + "/Billing/Return?attemptId=" + original.Id.ToString("D"),
                // Stable across original-operation retries. A moving expiration breaks provider idempotency.
                ExpiresAt = original.CreatedAt.AddHours(24).UtcDateTime,
                PaymentIntentData = new SessionPaymentIntentDataOptions { Metadata = metadata },
                LineItems = [new SessionLineItemOptions { Quantity = 1, PriceData = new SessionLineItemPriceDataOptions
                {
                    Currency = "usd", UnitAmount = original.TotalMinorUnits,
                    ProductData = new SessionLineItemPriceDataProductDataOptions { Name = "Office account payment" }
                } }]
            }, new RequestOptions { IdempotencyKey = original.ProviderIdempotencyKey }, token);
            return Observe(original, session);
        }, cancellationToken);

    public Task<ProviderCollectionObservation> ReadAsync(PaymentAttempt original, string sessionId, CancellationToken cancellationToken = default)
        => Run(async token =>
        {
            if (!ValidOriginal(original) || !OriginalSession(original, sessionId) || !await VerifyOwner(original, token)) return Invalid();
            var session = await new SessionService(_client).GetAsync(sessionId, new SessionGetOptions { Expand = ["payment_intent.latest_charge"] }, cancellationToken: token);
            return session.Id == sessionId ? Observe(original, session) : Invalid();
        }, cancellationToken);

    public Task<ProviderCollectionObservation> ExpireAsync(PaymentAttempt original, string sessionId, CancellationToken cancellationToken = default)
        => Run(async token =>
        {
            if (!ValidOriginal(original) || !OriginalSession(original, sessionId) || !await VerifyOwner(original, token)) return Invalid();
            var service = new SessionService(_client);
            var before = await service.GetAsync(sessionId, new SessionGetOptions { Expand = ["payment_intent.latest_charge"] }, cancellationToken: token);
            var observation = before.Id == sessionId ? Observe(original, before) : Invalid();
            if (observation.State != ProviderCollectionState.Open) return observation;
            // Never expire an object before verifying it belongs to this exact original attempt.
            await service.ExpireAsync(sessionId, requestOptions: new RequestOptions { IdempotencyKey = original.ProviderIdempotencyKey + ":expire" }, cancellationToken: token);
            // Re-read canonical state. A competing payment cannot be classified from a stale read or a clock.
            var after = await service.GetAsync(sessionId, new SessionGetOptions { Expand = ["payment_intent.latest_charge"] }, cancellationToken: token);
            return after.Id == sessionId ? Observe(original, after) : Invalid();
        }, cancellationToken);

    private async Task<bool> VerifyOwner(PaymentAttempt original, CancellationToken token)
    {
        var account = await new AccountService(_client).GetSelfAsync(cancellationToken: token);
        if (account.Id != _options.ProviderAccount) return false;
        var customer = await new CustomerService(_client).GetAsync(original.CustomerId, cancellationToken: token);
        return customer.Id == original.CustomerId && customer.Deleted != true && customer.Livemode == original.LiveMode &&
            customer.Metadata?.GetValueOrDefault("office_account_id") == original.AccountId.ToString("D");
    }

    private ProviderCollectionObservation Observe(PaymentAttempt original, Session session)
    {
        if (!PaymentReferences.Valid(session.Id, "cs_") || !OriginalSession(original, session.Id) || session.Livemode != original.LiveMode ||
            session.CustomerId != original.CustomerId || session.Mode != "payment" || session.Currency != "usd" || session.AmountTotal != original.TotalMinorUnits ||
            session.AmountSubtotal != original.TotalMinorUnits || session.ClientReferenceId != original.Id.ToString("D") || !Matches(original, session.Metadata)) return Invalid();
        var intent = session.PaymentIntent;
        if (intent is not null && (intent.Id != session.PaymentIntentId || !PaymentReferences.Valid(intent.Id, "pi_") || intent.Livemode != original.LiveMode ||
            intent.CustomerId != original.CustomerId || intent.Amount != original.TotalMinorUnits || intent.Currency != "usd" || !Matches(original, intent.Metadata))) return Invalid();
        if (session.Status == "open" && session.PaymentStatus == "unpaid")
        {
            if (session.ExpiresAt <= _time.GetUtcNow().UtcDateTime || !Uri.TryCreate(session.Url, UriKind.Absolute, out var url) ||
                url.Scheme != "https" || url.Host != "checkout.stripe.com" || !url.IsDefaultPort || url.UserInfo.Length > 0) return Invalid();
            return new(ProviderCollectionState.Open, session.Id, url, new DateTimeOffset(session.ExpiresAt, TimeSpan.Zero));
        }
        if (session.Status == "complete" && session.PaymentStatus == "paid")
        {
            var charge = intent?.LatestCharge;
            if (intent is null || intent.Status != "succeeded" || intent.AmountReceived != original.TotalMinorUnits || charge is null ||
                !PaymentReferences.Valid(charge.Id, "ch_") || charge.PaymentIntentId != intent.Id || charge.CustomerId != original.CustomerId || charge.Livemode != original.LiveMode ||
                !charge.Paid || !charge.Captured || charge.Status != "succeeded" || charge.Amount != original.TotalMinorUnits ||
                charge.AmountCaptured != original.TotalMinorUnits || charge.AmountRefunded != 0 || charge.Refunded || charge.Disputed || charge.Currency != "usd") return Invalid();
            return new(ProviderCollectionState.Paid, session.Id, Settlement: new(original.ProviderAccount, original.LiveMode, original.CustomerId,
                session.Id, intent.Id, intent.AmountReceived, original.Currency));
        }
        if (session.Status == "expired" && session.PaymentStatus == "unpaid" &&
            ((intent is null && session.PaymentIntentId is null) || (intent is { Status: "canceled", AmountReceived: 0 } && intent.LatestChargeId is null)))
            return new(ProviderCollectionState.Uncollected, session.Id);
        if (session.Status == "complete" && session.PaymentStatus == "unpaid" && intent is { Status: "processing", AmountReceived: 0 })
            return new(ProviderCollectionState.Processing, session.Id);
        return Invalid();
    }

    private bool ValidOriginal(PaymentAttempt original) => original is not null && original.ProviderAccount == _options.ProviderAccount && original.LiveMode == _options.LiveMode &&
        PaymentReferences.Valid(original.CustomerId, "cus_") && original.Currency == "USD" && original.TotalMinorUnits is >= 50 and <= 99_999_999 &&
        original.Items.Count is >= 1 and <= 25 && original.Items.Sum(x => x.AmountMinorUnits) == original.TotalMinorUnits;
    private static bool OriginalSession(PaymentAttempt original, string sessionId) => PaymentReferences.Valid(sessionId, "cs_") && (original.SessionId is null || original.SessionId == sessionId);
    private static Dictionary<string, string> Metadata(PaymentAttempt original) => new()
    {
        ["office_account_id"] = original.AccountId.ToString("D"),
        ["office_attempt_id"] = original.Id.ToString("D"),
        ["office_review_hash"] = original.PayloadHash
    };
    private static bool Matches(PaymentAttempt original, Dictionary<string, string>? metadata) => metadata is not null && Metadata(original).All(x => metadata.GetValueOrDefault(x.Key) == x.Value);
    private static ProviderCollectionObservation Invalid() => new(ProviderCollectionState.Invalid);
    public void Dispose() => _ownedHttp?.Dispose();
    private static async Task<ProviderCollectionObservation> Run(Func<CancellationToken, Task<ProviderCollectionObservation>> operation, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try { return await operation(deadline.Token); }
        // Responses, messages and credentials are never included in application diagnostics/results.
        catch (StripeException) { return new(ProviderCollectionState.Unknown); }
        catch (HttpRequestException) { return new(ProviderCollectionState.Unknown); }
        catch (OperationCanceledException) { return new(ProviderCollectionState.Unknown); }
        catch (System.Text.Json.JsonException) { return Invalid(); }
        catch (Newtonsoft.Json.JsonException) { return Invalid(); }
    }
}
