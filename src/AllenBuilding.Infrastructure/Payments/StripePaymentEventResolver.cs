using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using Stripe;
using Stripe.Checkout;

namespace AllenBuilding.Infrastructure.Payments;

public sealed class StripePaymentEventResolver : IPaymentEventResolver, IDisposable
{
    private readonly string _account;
    private readonly bool _liveMode;
    private readonly StripeClient _client;
    private readonly HttpClient? _ownedHttp;
    public StripePaymentEventResolver(StripeCollectionOptions options) : this(options, null) { }
    internal StripePaymentEventResolver(StripeCollectionOptions options, StripeClient? testClient)
    {
        options.Validate(); _account = options.ProviderAccount; _liveMode = options.LiveMode;
        if (testClient is null) _ownedHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _client = testClient ?? new StripeClient(new StripeClientOptions
        {
            ApiKey = options.ApiKey,
            HttpClient = new SystemNetHttpClient(_ownedHttp, maxNetworkRetries: 0, enableTelemetry: false)
        });
    }
    public async Task<ProviderEventReference> ResolveAsync(PaymentEventInbox original, CancellationToken cancellationToken)
    {
        if (original.ProviderAccount != _account || original.LiveMode != _liveMode || original.ApiVersion != CheckoutProfile.ApiContract) return new(EventReferenceState.Invalid);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        try
        {
            if ((await new AccountService(_client).GetSelfAsync(cancellationToken: token)).Id != _account) return new(EventReferenceState.Invalid);
            Stripe.Checkout.Session? session;
            if (original.EventType.StartsWith("checkout.session.", StringComparison.Ordinal))
            {
                if (!PaymentReferences.Valid(original.ResourceId, "cs_")) return new(EventReferenceState.Invalid);
                session = await new SessionService(_client).GetAsync(original.ResourceId, cancellationToken: token);
                if (session.Id != original.ResourceId) return new(EventReferenceState.Invalid);
            }
            else
            {
                string? intentId;
                if (original.EventType.StartsWith("payment_intent.", StringComparison.Ordinal)) intentId = original.ResourceId;
                else if (original.EventType == "charge.refunded")
                {
                    var charge = await new ChargeService(_client).GetAsync(original.ResourceId, cancellationToken: token);
                    if (charge.Id != original.ResourceId || charge.Livemode != _liveMode) return new(EventReferenceState.Invalid); intentId = charge.PaymentIntentId;
                }
                else if (original.EventType.StartsWith("charge.dispute.", StringComparison.Ordinal))
                {
                    var dispute = await new DisputeService(_client).GetAsync(original.ResourceId, cancellationToken: token);
                    if (dispute.Id != original.ResourceId || dispute.Livemode != _liveMode) return new(EventReferenceState.Invalid); intentId = dispute.PaymentIntentId;
                }
                else if (original.EventType.StartsWith("refund.", StringComparison.Ordinal))
                {
                    var refund = await new RefundService(_client).GetAsync(original.ResourceId, cancellationToken: token);
                    if (refund.Id != original.ResourceId) return new(EventReferenceState.Invalid); intentId = refund.PaymentIntentId;
                }
                else return new(EventReferenceState.Unsupported);
                if (intentId is null || !PaymentReferences.Valid(intentId, "pi_")) return new(EventReferenceState.Unsupported);
                var intent = await new PaymentIntentService(_client).GetAsync(intentId, cancellationToken: token);
                if (intent.Id != intentId || intent.Livemode != _liveMode) return new(EventReferenceState.Invalid);
                var sessions = await new SessionService(_client).ListAsync(new SessionListOptions { PaymentIntent = intentId, Limit = 2 }, cancellationToken: token);
                if (sessions.HasMore || sessions.Data.Count != 1) return new(EventReferenceState.Unsupported);
                session = sessions.Data.Single();
                if (session.PaymentIntentId != intentId || session.CustomerId != intent.CustomerId || !SameLineage(session.Metadata, intent.Metadata)) return new(EventReferenceState.Invalid);
            }
            if (!PaymentReferences.Valid(session.Id, "cs_") || session.Livemode != _liveMode || session.Mode != "payment" ||
                !Guid.TryParse(session.Metadata?.GetValueOrDefault("office_attempt_id"), out var attemptId) || attemptId == Guid.Empty ||
                !Guid.TryParse(session.Metadata?.GetValueOrDefault("office_account_id"), out var accountId) || accountId == Guid.Empty ||
                session.Metadata?.GetValueOrDefault("office_review_hash") is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit) || session.ClientReferenceId != attemptId.ToString("D"))
                return new(EventReferenceState.Invalid);
            return new(EventReferenceState.Known, attemptId, accountId, hash, session.Id);
        }
        catch (StripeException) { return new(EventReferenceState.Unknown); }
        catch (HttpRequestException) { return new(EventReferenceState.Unknown); }
        catch (OperationCanceledException) { return new(EventReferenceState.Unknown); }
        catch (Newtonsoft.Json.JsonException) { return new(EventReferenceState.Invalid); }
    }
    private static bool SameLineage(Dictionary<string, string>? session, Dictionary<string, string>? intent) => session is not null && intent is not null &&
        new[] { "office_attempt_id", "office_account_id", "office_review_hash" }.All(key => session.ContainsKey(key) && session[key] == intent.GetValueOrDefault(key));
    public void Dispose() => _ownedHttp?.Dispose();
}
