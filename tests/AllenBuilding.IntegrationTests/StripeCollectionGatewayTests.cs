using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Infrastructure.Payments;
using Stripe;

namespace AllenBuilding.IntegrationTests;

public sealed class StripeCollectionGatewayTests
{
    [Fact]
    public async Task Hosted_dispatch_preserves_exact_key_body_scope_and_stable_expiration_across_retry()
    {
        using var fixture = new Fixture();
        var first = await fixture.Gateway.CreateAsync(fixture.Attempt);
        Assert.Equal(ProviderCollectionState.Open, first.State); Assert.Equal("checkout.stripe.com", first.Handoff!.Host);
        fixture.Time.Now = fixture.Time.Now.AddMinutes(2);
        Assert.Equal(ProviderCollectionState.Open, (await fixture.Gateway.CreateAsync(fixture.Attempt)).State);
        var posts = fixture.Requests.Where(x => x.Method == "POST").ToArray(); Assert.Equal(2, posts.Length);
        Assert.Equal(posts[0].Body, posts[1].Body); Assert.Equal(fixture.Attempt.ProviderIdempotencyKey, posts[0].Key);
        Assert.Equal(posts[0].Key, posts[1].Key); Assert.Equal("2026-09-30.endive", posts[0].Version);
        Assert.All(fixture.Requests, x => Assert.Null(x.ConnectAccount));
        var fields = Form(posts[0].Body);
        Assert.Equal("payment", fields["mode"]); Assert.Equal("hosted", fields["ui_mode"]);
        Assert.Equal("cus_gateway", fields["customer"]); Assert.Equal("pmc_reviewed", fields["payment_method_configuration"]);
        Assert.Equal("false", fields["adaptive_pricing[enabled]"]); Assert.Equal("1000", fields["line_items[0][price_data][unit_amount]"]);
        Assert.DoesNotContain(fields.Keys, x => x.StartsWith("payment_method_types", StringComparison.Ordinal));
        Assert.Equal(fixture.Attempt.PayloadHash, fields["metadata[office_review_hash]"]);
        Assert.Equal(fixture.Attempt.PayloadHash, fields["payment_intent_data[metadata][office_review_hash]"]);
        Assert.DoesNotContain(fields.Keys, x => x.Contains("setup_future_usage", StringComparison.Ordinal));
        Assert.Equal("https://office.example.test/Billing/Return?attemptId=" + fixture.Attempt.Id.ToString("D"), fields["success_url"]);
        Assert.Equal(fixture.Attempt.CreatedAt.AddHours(24).ToUnixTimeSeconds().ToString(), fields["expires_at"]);
    }

    [Theory]
    [InlineData("merchant")]
    [InlineData("customer-owner")]
    [InlineData("customer-mode")]
    [InlineData("deleted-customer")]
    public async Task Wrong_provider_ownership_prevents_dispatch(string mismatch)
    {
        using var fixture = new Fixture();
        if (mismatch == "merchant") fixture.AccountId = "acct_wrong";
        if (mismatch == "customer-owner") fixture.Customer["metadata"]!["office_account_id"] = Guid.NewGuid().ToString("D");
        if (mismatch == "customer-mode") fixture.Customer["livemode"] = true;
        if (mismatch == "deleted-customer") fixture.Customer["deleted"] = true;
        Assert.Equal(ProviderCollectionState.Invalid, (await fixture.Gateway.CreateAsync(fixture.Attempt)).State);
        Assert.DoesNotContain(fixture.Requests, x => x.Method == "POST");
    }

    [Theory]
    [InlineData("customer")]
    [InlineData("mode")]
    [InlineData("amount")]
    [InlineData("subtotal")]
    [InlineData("currency")]
    [InlineData("review")]
    [InlineData("reference")]
    [InlineData("handoff")]
    public async Task Mismatched_handoff_is_not_released_to_the_browser(string mismatch)
    {
        using var fixture = new Fixture();
        switch (mismatch)
        {
            case "customer": fixture.Session["customer"] = "cus_wrong"; break;
            case "mode": fixture.Session["livemode"] = true; break;
            case "amount": fixture.Session["amount_total"] = 999; break;
            case "subtotal": fixture.Session["amount_subtotal"] = 999; break;
            case "currency": fixture.Session["currency"] = "eur"; break;
            case "review": fixture.Session["metadata"]!["office_review_hash"] = "different"; break;
            case "reference": fixture.Session["client_reference_id"] = Guid.NewGuid().ToString("D"); break;
            case "handoff": fixture.Session["url"] = "https://checkout.stripe.com.attacker.test/"; break;
        }
        var result = await fixture.Gateway.CreateAsync(fixture.Attempt);
        Assert.Equal(ProviderCollectionState.Invalid, result.State); Assert.Null(result.Handoff); Assert.Null(result.Settlement);
    }

    [Fact]
    public async Task Canonical_capture_requires_expanded_exact_payment_and_charge_evidence()
    {
        using var fixture = new Fixture(); fixture.Paid();
        var result = await fixture.Gateway.ReadAsync(fixture.Attempt, "cs_gateway");
        Assert.Equal(ProviderCollectionState.Paid, result.State); Assert.Equal(1000, result.Settlement!.AmountMinorUnits);
        Assert.Equal("pi_gateway", result.Settlement.PaymentId);
        Assert.Contains(fixture.Requests, x => x.Path.Contains("expand", StringComparison.Ordinal));
        Assert.Equal(PaymentAttemptState.Dispatching, fixture.Attempt.State); // Adapter never commits local state.
    }

    [Theory]
    [InlineData("unexpanded")]
    [InlineData("received")]
    [InlineData("intent-owner")]
    [InlineData("intent-review")]
    [InlineData("captured")]
    [InlineData("over-refunded")]
    [InlineData("refund-flag")]
    [InlineData("charge-owner")]
    [InlineData("charge-mode")]
    [InlineData("charge-intent")]
    public async Task Paid_label_alone_cannot_create_a_receipt(string mismatch)
    {
        using var fixture = new Fixture(); fixture.Paid();
        var intent = fixture.Session["payment_intent"]!; var charge = intent["latest_charge"]!;
        switch (mismatch)
        {
            case "unexpanded": fixture.Session["payment_intent"] = "pi_gateway"; break;
            case "received": intent["amount_received"] = 999; break;
            case "intent-owner": intent["customer"] = "cus_wrong"; break;
            case "intent-review": intent["metadata"]!["office_review_hash"] = "different"; break;
            case "captured": charge["captured"] = false; break;
            case "over-refunded": charge["amount_refunded"] = 1001; break;
            case "refund-flag": charge["refunded"] = true; break;
            case "charge-owner": charge["customer"] = "cus_wrong"; break;
            case "charge-mode": charge["livemode"] = true; break;
            case "charge-intent": charge["payment_intent"] = "pi_wrong"; break;
        }
        var result = await fixture.Gateway.ReadAsync(fixture.Attempt, "cs_gateway");
        Assert.Equal(ProviderCollectionState.Invalid, result.State); Assert.Null(result.Settlement);
    }

    [Theory]
    [InlineData("unchanged")]
    [InlineData("partial-refund")]
    [InlineData("full-refund")]
    [InlineData("disputed")]
    public async Task Gross_capture_survives_later_returns_but_requires_a_complete_inventory_before_releasing_collection(string state)
    {
        using var fixture = new Fixture(); fixture.Paid(); var version = fixture.Attempt.Version;
        var charge = fixture.Session["payment_intent"]!["latest_charge"]!;
        if (state == "partial-refund") charge["amount_refunded"] = 200;
        if (state == "full-refund") { charge["amount_refunded"] = 1000; charge["refunded"] = true; }
        if (state == "disputed") charge["disputed"] = true;
        var result = await fixture.Gateway.ReadAsync(fixture.Attempt, "cs_gateway");
        Assert.Equal(ProviderCollectionState.Paid, result.State); Assert.Equal(1000, result.Settlement!.AmountMinorUnits);
        Assert.Equal(CashObservationState.Invalid, result.CashInventory!.State); Assert.Null(result.CashInventory.Inventory);
        Assert.Equal(version, fixture.Attempt.Version); Assert.Equal(PaymentAttemptState.Dispatching, fixture.Attempt.State);
        Assert.Null(fixture.Attempt.PaymentId); Assert.All(fixture.Requests, x => Assert.Equal("GET", x.Method));
    }

    [Fact]
    public async Task Expiry_retrieves_before_and_after_original_idempotent_expiration()
    {
        using var fixture = new Fixture();
        var result = await fixture.Gateway.ExpireAsync(fixture.Attempt, "cs_gateway");
        Assert.Equal(ProviderCollectionState.Uncollected, result.State);
        var sessionRequests = fixture.Requests.Where(x => x.Path.StartsWith("/v1/checkout/sessions", StringComparison.Ordinal)).ToArray();
        Assert.Equal(new[] { "GET", "POST", "GET" }, sessionRequests.Select(x => x.Method));
        Assert.Equal(fixture.Attempt.ProviderIdempotencyKey + ":expire", sessionRequests[1].Key);
    }

    [Fact]
    public async Task A_payment_racing_expiration_is_preserved_as_paid()
    {
        using var fixture = new Fixture(); fixture.PayAfterExpire = true;
        Assert.Equal(ProviderCollectionState.Paid, (await fixture.Gateway.ExpireAsync(fixture.Attempt, "cs_gateway")).State);
    }

    [Fact]
    public async Task Delayed_processing_is_neither_cash_nor_released_debt()
    {
        using var fixture = new Fixture(); fixture.Paid();
        fixture.Session["payment_status"] = "unpaid";
        fixture.Session["payment_intent"]!["status"] = "processing";
        fixture.Session["payment_intent"]!["amount_received"] = 0;
        fixture.Session["payment_intent"]!["latest_charge"] = null;
        var result = await fixture.Gateway.ReadAsync(fixture.Attempt, "cs_gateway");
        Assert.Equal(ProviderCollectionState.Processing, result.State); Assert.Null(result.Settlement);
        fixture.Session["status"] = "expired";
        Assert.Equal(ProviderCollectionState.Invalid, (await fixture.Gateway.ReadAsync(fixture.Attempt, "cs_gateway")).State);
    }

    [Fact]
    public async Task Expiration_does_not_modify_another_attempts_session()
    {
        using var fixture = new Fixture(); fixture.Session["metadata"]!["office_attempt_id"] = Guid.NewGuid().ToString("D");
        Assert.Equal(ProviderCollectionState.Invalid, (await fixture.Gateway.ExpireAsync(fixture.Attempt, "cs_gateway")).State);
        Assert.DoesNotContain(fixture.Requests, x => x.Method == "POST");
    }

    [Fact]
    public async Task Pre_canceled_dispatch_never_contacts_provider()
    {
        using var fixture = new Fixture(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Equal(ProviderCollectionState.Unknown, (await fixture.Gateway.CreateAsync(fixture.Attempt, cancellation.Token)).State);
        Assert.Empty(fixture.Requests);
    }

    [Theory]
    [InlineData("network")]
    [InlineData("503")]
    [InlineData("429")]
    [InlineData("401")]
    [InlineData("cancel")]
    public async Task Provider_failure_or_lost_acknowledgment_preserves_uncertainty(string failure)
    {
        using var fixture = new Fixture(); fixture.Failure = failure;
        var result = await fixture.Gateway.CreateAsync(fixture.Attempt);
        Assert.Equal(ProviderCollectionState.Unknown, result.State); Assert.Null(result.SessionId); Assert.Null(result.Settlement);
        Assert.Single(fixture.Requests, x => x.Method == "POST"); // SDK retries disabled; durable coordinator owns retry.
        Assert.Equal(PaymentAttemptState.Dispatching, fixture.Attempt.State);
    }

    [Fact]
    public async Task Old_dispatch_requires_investigation_without_another_provider_write()
    {
        using var fixture = new Fixture(); fixture.Time.Now = fixture.Time.Now.AddHours(23);
        Assert.Equal(ProviderCollectionState.Invalid, (await fixture.Gateway.CreateAsync(fixture.Attempt)).State); Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task Known_original_session_cannot_be_replaced_and_wrong_session_cannot_be_expired()
    {
        using var fixture = new Fixture(); fixture.Attempt.AttachHandoff("cs_original", fixture.Time.Now.AddHours(1), fixture.Time.Now);
        Assert.Equal(ProviderCollectionState.Invalid, (await fixture.Gateway.ReadAsync(fixture.Attempt, "cs_other")).State);
        Assert.Equal(ProviderCollectionState.Invalid, (await fixture.Gateway.ExpireAsync(fixture.Attempt, "cs_other")).State); Assert.Empty(fixture.Requests);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("account")]
    [InlineData("origin")]
    [InlineData("config")]
    public void Invalid_configuration_fails_without_rendering_credential(string invalid)
    {
        var options = Fixture.Options();
        switch (invalid)
        {
            case "key": options.ApiKey = "rk_live_synthetic"; break;
            case "account": options.ProviderAccount = "wrong"; break;
            case "origin": options.PublicOrigin = "https://user:private@office.example.test/path"; break;
            case "config": options.PaymentMethodConfiguration = ""; break;
        }
        var error = Assert.Throws<InvalidOperationException>(options.Validate); Assert.DoesNotContain("synthetic", error.Message);
    }

    private static Dictionary<string, string> Form(string body) => body.Split('&').Select(x => x.Split('=', 2))
        .ToDictionary(x => WebUtility.UrlDecode(x[0]), x => WebUtility.UrlDecode(x.Length > 1 ? x[1] : ""));
    private sealed class Clock : TimeProvider { public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed record Request(string Method, string Path, string Body, string? Key, string? Version, string? ConnectAccount);
    private sealed class Fixture : HttpMessageHandler
    {
        public Clock Time { get; } = new();
        public PaymentAttempt Attempt { get; }
        public StripeCollectionGateway Gateway { get; }
        public List<Request> Requests { get; } = [];
        public string AccountId { get; set; } = "acct_gateway";
        public JsonObject Customer { get; }
        public JsonObject Session { get; }
        public string? Failure { get; set; }
        public bool PayAfterExpire { get; set; }
        private readonly HttpClient _http;
        public static StripeCollectionOptions Options() => new() { ApiKey = "rk_test_synthetic", ProviderAccount = "acct_gateway", PublicOrigin = "https://office.example.test", PaymentMethodConfiguration = "pmc_reviewed" };
        public Fixture()
        {
            var binding = BillingCustomerBinding.Create(Guid.NewGuid(), "acct_gateway", false, "cus_gateway", Time.Now);
            Attempt = PaymentAttempt.Admit(Guid.NewGuid(), "synthetic-payer", binding, [new(Guid.NewGuid(), binding.AccountId, Guid.NewGuid(), 1000)], Time.Now);
            Attempt.BeginDispatch(Time.Now);
            Customer = new()
            {
                ["id"] = "cus_gateway",
                ["object"] = "customer",
                ["livemode"] = false,
                ["metadata"] = new JsonObject { ["office_account_id"] = binding.AccountId.ToString("D") }
            };
            Session = new()
            {
                ["id"] = "cs_gateway",
                ["object"] = "checkout.session",
                ["customer"] = "cus_gateway",
                ["livemode"] = false,
                ["mode"] = "payment",
                ["status"] = "open",
                ["payment_status"] = "unpaid",
                ["amount_total"] = 1000,
                ["amount_subtotal"] = 1000,
                ["currency"] = "usd",
                ["client_reference_id"] = Attempt.Id.ToString("D"),
                ["metadata"] = Metadata(),
                ["url"] = "https://checkout.stripe.com/c/pay/synthetic",
                ["expires_at"] = Time.Now.AddHours(24).ToUnixTimeSeconds()
            };
            _http = new HttpClient(this, disposeHandler: false);
            var client = new StripeClient("rk_test_synthetic", httpClient: new SystemNetHttpClient(_http, maxNetworkRetries: 0, enableTelemetry: false));
            Gateway = new StripeCollectionGateway(Options(), Time, client);
        }
        private JsonObject Metadata() => new() { ["office_account_id"] = Attempt.AccountId.ToString("D"), ["office_attempt_id"] = Attempt.Id.ToString("D"), ["office_review_hash"] = Attempt.PayloadHash };
        public void Paid()
        {
            Session["status"] = "complete"; Session["payment_status"] = "paid";
            Session["payment_intent"] = new JsonObject
            {
                ["id"] = "pi_gateway",
                ["object"] = "payment_intent",
                ["livemode"] = false,
                ["customer"] = "cus_gateway",
                ["status"] = "succeeded",
                ["amount"] = 1000,
                ["amount_received"] = 1000,
                ["currency"] = "usd",
                ["metadata"] = Metadata(),
                ["latest_charge"] = new JsonObject
                {
                    ["id"] = "ch_gateway",
                    ["object"] = "charge",
                    ["payment_intent"] = "pi_gateway",
                    ["customer"] = "cus_gateway",
                    ["livemode"] = false,
                    ["paid"] = true,
                    ["captured"] = true,
                    ["status"] = "succeeded",
                    ["amount"] = 1000,
                    ["amount_captured"] = 1000,
                    ["amount_refunded"] = 0,
                    ["refunded"] = false,
                    ["disputed"] = false,
                    ["currency"] = "usd"
                }
            };
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? Header(string name) => request.Headers.TryGetValues(name, out var values) ? values.Single() : null;
            var path = request.RequestUri!.PathAndQuery;
            Requests.Add(new(request.Method.Method, path, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken), Header("Idempotency-Key"), Header("Stripe-Version"), Header("Stripe-Account")));
            if (request.Method == HttpMethod.Post && Failure is { } failure)
            {
                if (failure == "network") throw new HttpRequestException("Synthetic lost acknowledgment");
                if (failure == "cancel") throw new OperationCanceledException(cancellationToken);
                return Json(new JsonObject { ["error"] = new JsonObject { ["type"] = "api_error", ["message"] = "Synthetic provider failure" } }, (HttpStatusCode)int.Parse(failure));
            }
            if (path == "/v1/account") return Json(new JsonObject { ["id"] = AccountId, ["object"] = "account" });
            if (path.StartsWith("/v1/customers/", StringComparison.Ordinal)) return Json(Customer);
            if (path.StartsWith("/v1/checkout/sessions", StringComparison.Ordinal))
            {
                if (path.EndsWith("/expire", StringComparison.Ordinal))
                {
                    if (PayAfterExpire) Paid(); else { Session["status"] = "expired"; Session["url"] = null; }
                }
                return Json(Session);
            }
            throw new InvalidOperationException("Unexpected provider path.");
        }
        private static HttpResponseMessage Json(JsonObject value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(value.ToJsonString(), Encoding.UTF8, "application/json") };
        protected override void Dispose(bool disposing) { if (disposing) { Gateway.Dispose(); _http.Dispose(); } base.Dispose(disposing); }
    }
}
