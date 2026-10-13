using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Infrastructure.Payments;
using Stripe;

namespace AllenBuilding.IntegrationTests;

public sealed class StripePaymentEventResolverTests
{
    [Theory]
    [InlineData("checkout.session.completed", "cs_event")]
    [InlineData("checkout.session.expired", "cs_event")]
    [InlineData("payment_intent.succeeded", "pi_event")]
    [InlineData("payment_intent.processing", "pi_event")]
    [InlineData("charge.refunded", "ch_event")]
    [InlineData("refund.updated", "re_event")]
    [InlineData("charge.dispute.created", "dp_event")]
    public async Task Event_lookup_retrieves_current_objects_and_preserves_exact_original_lineage(string type, string id)
    {
        using var fixture = new Fixture(); var result = await fixture.Resolver.ResolveAsync(fixture.Event(type, id), CancellationToken.None);
        Assert.Equal(EventReferenceState.Known, result.State); Assert.Equal(fixture.AttemptId, result.AttemptId); Assert.Equal(fixture.AccountId, result.AccountId);
        Assert.Equal("cs_event", result.SessionId); Assert.Equal(new string('A', 64), result.ReviewHash); Assert.All(fixture.Methods, method => Assert.Equal("GET", method));
        if (id != "cs_event") Assert.Contains(fixture.Paths, path => path.Contains("payment_intent=pi_event", StringComparison.Ordinal) && path.Contains("limit=2", StringComparison.Ordinal));
    }
    [Theory]
    [InlineData("merchant")]
    [InlineData("mode")]
    [InlineData("metadata")]
    [InlineData("client-reference")]
    [InlineData("intent-lineage")]
    [InlineData("wrong-intent")]
    public async Task Provider_scope_or_lineage_mismatch_cannot_name_a_collection(string mismatch)
    {
        using var fixture = new Fixture(); fixture.Mismatch = mismatch;
        var result = await fixture.Resolver.ResolveAsync(fixture.Event("payment_intent.succeeded", "pi_event"), CancellationToken.None);
        Assert.Equal(EventReferenceState.Invalid, result.State); Assert.Null(result.AttemptId); Assert.Null(result.SessionId);
    }
    [Theory]
    [InlineData("invoice.finalized", "in_event")]
    [InlineData("setup_intent.succeeded", "seti_event")]
    [InlineData("customer.subscription.updated", "sub_event")]
    public async Task A_resource_without_an_enabled_projection_is_explicitly_unsupported(string type, string id)
    {
        using var fixture = new Fixture(); Assert.Equal(EventReferenceState.Unsupported, (await fixture.Resolver.ResolveAsync(fixture.Event(type, id), CancellationToken.None)).State);
    }
    [Theory]
    [InlineData("empty")]
    [InlineData("multiple")]
    [InlineData("has-more")]
    public async Task Ambiguous_intent_sessions_require_investigation(string mismatch)
    {
        using var fixture = new Fixture(); fixture.Mismatch = mismatch;
        Assert.Equal(EventReferenceState.Unsupported, (await fixture.Resolver.ResolveAsync(fixture.Event("payment_intent.succeeded", "pi_event"), CancellationToken.None)).State);
    }
    [Fact]
    public async Task Provider_outage_or_foreign_envelope_does_not_produce_a_target()
    {
        using var fixture = new Fixture(); fixture.Mismatch = "outage";
        Assert.Equal(EventReferenceState.Unknown, (await fixture.Resolver.ResolveAsync(fixture.Event("checkout.session.completed", "cs_event"), CancellationToken.None)).State);
        var foreign = PaymentEventInbox.Receive("acct_foreign", false, "evt_foreign", "checkout.session.completed", "cs_event", CheckoutProfile.ApiContract, new string('A', 64), true, DateTimeOffset.UtcNow);
        var count = fixture.Paths.Count; Assert.Equal(EventReferenceState.Invalid, (await fixture.Resolver.ResolveAsync(foreign, CancellationToken.None)).State); Assert.Equal(count, fixture.Paths.Count);
    }
    private sealed class Fixture : HttpMessageHandler
    {
        public Guid AttemptId { get; } = Guid.NewGuid(); public Guid AccountId { get; } = Guid.NewGuid();
        public string? Mismatch { get; set; }
        public List<string> Methods { get; } = []; public List<string> Paths { get; } = [];
        public StripePaymentEventResolver Resolver { get; }
        private readonly HttpClient _http;
        public Fixture()
        {
            _http = new HttpClient(this, disposeHandler: false);
            var client = new StripeClient(new StripeClientOptions { ApiKey = "sk_test_synthetic_resolver", HttpClient = new SystemNetHttpClient(_http, maxNetworkRetries: 0, enableTelemetry: false) });
            Resolver = new(new StripeCollectionOptions { ApiKey = "sk_test_synthetic_resolver", ProviderAccount = "acct_event", PublicOrigin = "https://office.example.test", PaymentMethodConfiguration = "pmc_event" }, client);
        }
        public PaymentEventInbox Event(string type, string resource) => PaymentEventInbox.Receive("acct_event", false, "evt_event", type, resource, CheckoutProfile.ApiContract, new string('A', 64), true, DateTimeOffset.UtcNow);
        private JsonObject Metadata() => new() { ["office_attempt_id"] = AttemptId.ToString("D"), ["office_account_id"] = AccountId.ToString("D"), ["office_review_hash"] = new string('A', 64) };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery; Paths.Add(path); Methods.Add(request.Method.Method);
            if (Mismatch == "outage") throw new HttpRequestException("private-provider-canary");
            JsonObject value;
            if (path == "/v1/account") value = new() { ["id"] = Mismatch == "merchant" ? "acct_wrong" : "acct_event", ["object"] = "account" };
            else if (path.StartsWith("/v1/payment_intents/", StringComparison.Ordinal))
            {
                value = new() { ["id"] = "pi_event", ["object"] = "payment_intent", ["livemode"] = false, ["customer"] = "cus_event", ["metadata"] = Metadata() };
                if (Mismatch == "intent-lineage") value["metadata"]!["office_review_hash"] = new string('B', 64);
            }
            else if (path.StartsWith("/v1/checkout/sessions", StringComparison.Ordinal))
            {
                var session = new JsonObject { ["id"] = "cs_event", ["object"] = "checkout.session", ["livemode"] = Mismatch == "mode", ["customer"] = "cus_event", ["payment_intent"] = Mismatch == "wrong-intent" ? "pi_other" : "pi_event", ["mode"] = "payment", ["metadata"] = Metadata(), ["client_reference_id"] = AttemptId.ToString("D") };
                if (Mismatch == "metadata") session["metadata"]!["office_attempt_id"] = "invalid";
                if (Mismatch == "client-reference") session["client_reference_id"] = Guid.NewGuid().ToString("D");
                if (path.Contains('?', StringComparison.Ordinal))
                {
                    var data = new JsonArray(); if (Mismatch != "empty") data.Add(session); if (Mismatch == "multiple") data.Add(session.DeepClone());
                    value = new() { ["object"] = "list", ["has_more"] = Mismatch == "has-more", ["data"] = data, ["url"] = "/v1/checkout/sessions" };
                }
                else value = session;
            }
            else if (path.StartsWith("/v1/refunds/", StringComparison.Ordinal)) value = new() { ["id"] = "re_event", ["object"] = "refund", ["payment_intent"] = "pi_event" };
            else if (path.StartsWith("/v1/disputes/", StringComparison.Ordinal)) value = new() { ["id"] = "dp_event", ["object"] = "dispute", ["livemode"] = false, ["payment_intent"] = "pi_event" };
            else if (path.StartsWith("/v1/charges/", StringComparison.Ordinal)) value = new() { ["id"] = "ch_event", ["object"] = "charge", ["livemode"] = false, ["payment_intent"] = "pi_event" };
            else throw new InvalidOperationException("Unexpected synthetic API path.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(value.ToJsonString(), Encoding.UTF8, "application/json") });
        }
        protected override void Dispose(bool disposing) { if (disposing) { Resolver.Dispose(); _http.Dispose(); } base.Dispose(disposing); }
    }
}
