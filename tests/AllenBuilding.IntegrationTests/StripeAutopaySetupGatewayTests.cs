using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Infrastructure.Payments;
using Stripe;

namespace AllenBuilding.IntegrationTests;

public sealed class StripeAutopaySetupGatewayTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("host")]
    [InlineData("port")]
    [InlineData("userinfo")]
    [InlineData("scheme")]
    [InlineData("length")]
    [InlineData("ready")]
    [InlineData("stopping")]
    public async Task Browser_handoff_only_reads_the_known_setup_and_releases_a_valid_hosted_url(string state)
    {
        using var f = new Fixture(); f.KnownLease(); f.Session["url"] = "https://checkout.stripe.com/c/setup/synthetic";
        switch (state)
        {
            case "host": f.Session["url"] = "https://checkout.stripe.com.attacker.test/"; break;
            case "port": f.Session["url"] = "https://checkout.stripe.com:444/"; break;
            case "userinfo": f.Session["url"] = "https://private@checkout.stripe.com/"; break;
            case "scheme": f.Session["url"] = "http://checkout.stripe.com/"; break;
            case "length": f.Session["url"] = "https://checkout.stripe.com/" + new string('a', 4096); break;
            case "ready": f.Ready(); break;
            case "stopping": f.Auth.RequestStop(Guid.NewGuid(), "payer", AutopayStopTiming.Immediately, f.Time.Now); break;
        }
        var result = await f.Gateway.ReadAsync(new AutopayMethodSnapshot(f.Auth, f.Work, Guid.NewGuid()), default);
        if (state == "valid") { Assert.NotNull(result); Assert.Equal("cs_setup", result.SessionId); Assert.Equal("checkout.stripe.com", result.Destination.Host); }
        else Assert.Null(result);
        Assert.All(f.Requests, x => Assert.Equal("GET", x.Method)); Assert.Null(f.Auth.PaymentMethodId); Assert.Null(f.Auth.SubscriptionId);
    }
    [Fact]
    public async Task Setup_writes_only_a_stable_hosted_card_setup_then_reads_the_canonical_original()
    {
        using var f = new Fixture();
        var first = await f.Gateway.CreateAsync(f.Lease, default);
        Assert.Equal(AutopaySetupOutcome.Verified, first.Outcome); Assert.Equal(AutopaySetupState.Waiting, first.Setup!.State);
        Assert.Null(first.Setup.PaymentMethodId); Assert.Equal(AutopayState.AwaitingMethod, f.Auth.State);
        f.Time.Now = f.Time.Now.AddMinutes(1);
        Assert.Equal(AutopaySetupOutcome.Verified, (await f.Gateway.CreateAsync(f.Lease, default)).Outcome);
        var posts = f.Requests.Where(x => x.Method == "POST").ToArray(); Assert.Equal(2, posts.Length);
        Assert.Equal(posts[0].Body, posts[1].Body); Assert.All(posts, x => Assert.Equal(f.Work.ProviderOperationKey, x.Key));
        Assert.All(f.Requests, x => { Assert.Equal("2026-09-30.endive", x.Version); Assert.Null(x.ConnectAccount); });
        var fields = Form(posts[0].Body);
        Assert.Equal("setup", fields["mode"]); Assert.Equal("hosted_page", fields["ui_mode"]); Assert.Equal("usd", fields["currency"]);
        Assert.Equal("card", fields["allowed_payment_method_types[0]"]); Assert.Equal("pmc_reviewed", fields["payment_method_configuration"]);
        Assert.Equal("cus_setup", fields["customer"]); Assert.Equal(f.Auth.ConsentHash, fields["setup_intent_data[metadata][office_consent_hash]"]);
        Assert.Equal(f.Auth.ConsentHash, fields["metadata[office_consent_hash]"]); Assert.Equal(f.Work.DispatchedAt!.Value.AddHours(24).ToUnixTimeSeconds().ToString(), fields["expires_at"]);
        Assert.Equal("https://office.example.test/Autopay/Return?authorizationId=" + f.Auth.Id.ToString("D"), fields["success_url"]);
        Assert.Equal(fields["success_url"], fields["cancel_url"]);
        Assert.DoesNotContain(fields.Keys, x => x.StartsWith("line_items", StringComparison.Ordinal) || x.StartsWith("payment_intent", StringComparison.Ordinal) ||
            x.StartsWith("subscription", StringComparison.Ordinal) || x.StartsWith("payment_method_types", StringComparison.Ordinal));
        Assert.All(posts, x => Assert.Equal("/v1/checkout/sessions", x.Path));
        Assert.Equal(4, f.SessionGets); // Each acknowledgment followed by initial/final canonical GETs.
    }

    [Theory]
    [InlineData("merchant")]
    [InlineData("customer-owner")]
    [InlineData("customer-mode")]
    [InlineData("deleted")]
    public async Task A_wrong_dedicated_customer_or_merchant_prevents_all_writes(string mismatch)
    {
        using var f = new Fixture();
        switch (mismatch)
        {
            case "merchant": f.Merchant = "acct_other"; break;
            case "customer-owner": f.Customer["metadata"]!["office_account_id"] = Guid.NewGuid().ToString("D"); break;
            case "customer-mode": f.Customer["livemode"] = true; break;
            case "deleted": f.Customer["deleted"] = true; break;
        }
        Assert.Equal(AutopaySetupOutcome.Invalid, (await f.Gateway.CreateAsync(f.Lease, default)).Outcome);
        Assert.DoesNotContain(f.Requests, x => x.Method == "POST");
    }

    [Theory]
    [InlineData("read-only")]
    [InlineData("known-session")]
    [InlineData("expired-lease")]
    [InlineData("expired-window")]
    [InlineData("late-owner-read")]
    [InlineData("stopped")]
    [InlineData("wrong-fence")]
    public async Task Creation_rechecks_fence_and_write_admission_after_ownership_reads(string state)
    {
        using var f = new Fixture();
        switch (state)
        {
            case "read-only": f.Lease = f.Lease with { ReadOnly = true }; break;
            case "known-session": f.Auth.ObserveVerifiedSetup(f.Waiting(), false); break;
            case "expired-lease": f.Time.Now = f.Time.Now.AddMinutes(2); break;
            case "expired-window": f.ReclaimAfter(TimeSpan.FromHours(23)); break;
            case "late-owner-read": f.OnCustomer = () => f.Time.Now = f.Time.Now.AddMinutes(2); break;
            case "stopped": f.Auth.RequestStop(Guid.NewGuid(), "payer", AutopayStopTiming.Immediately, f.Time.Now); break;
            case "wrong-fence": f.Lease = f.Lease with { Token = Guid.NewGuid() }; break;
        }
        Assert.Equal(AutopaySetupOutcome.Invalid, (await f.Gateway.CreateAsync(f.Lease, default)).Outcome);
        Assert.DoesNotContain(f.Requests, x => x.Method == "POST");
    }

    [Theory]
    [InlineData("session-owner")]
    [InlineData("session-mode")]
    [InlineData("session-kind")]
    [InlineData("session-ui")]
    [InlineData("session-consent")]
    [InlineData("session-reference")]
    [InlineData("expiry")]
    [InlineData("amount")]
    [InlineData("payment")]
    [InlineData("subscription")]
    [InlineData("invoice")]
    [InlineData("configuration")]
    [InlineData("intent-owner")]
    [InlineData("intent-consent")]
    [InlineData("intent-mode")]
    [InlineData("intent-usage")]
    [InlineData("intent-status")]
    [InlineData("method-owner")]
    [InlineData("method-mode")]
    [InlineData("method-type")]
    [InlineData("method-details")]
    public async Task A_complete_label_needs_exact_original_setup_and_attached_off_session_card(string mismatch)
    {
        using var f = new Fixture(); f.Ready();
        switch (mismatch)
        {
            case "session-owner": f.Session["customer"] = "cus_other"; break;
            case "session-mode": f.Session["livemode"] = true; break;
            case "session-kind": f.Session["mode"] = "payment"; break;
            case "session-ui": f.Session["ui_mode"] = "elements"; break;
            case "session-consent": f.Session["metadata"]!["office_consent_hash"] = "wrong"; break;
            case "session-reference": f.Session["client_reference_id"] = Guid.NewGuid().ToString("D"); break;
            case "expiry": f.Session["expires_at"] = f.Time.Now.AddHours(1).ToUnixTimeSeconds(); break;
            case "amount": f.Session["amount_total"] = 1; break;
            case "payment": f.Session["payment_intent"] = "pi_unexpected"; break;
            case "subscription": f.Session["subscription"] = "sub_unexpected"; break;
            case "invoice": f.Session["invoice"] = "in_unexpected"; break;
            case "configuration": f.Session["payment_method_configuration_details"]!["id"] = "pmc_other"; break;
            case "intent-owner": f.Intent["customer"] = "cus_other"; break;
            case "intent-consent": f.Intent["metadata"]!["office_consent_hash"] = "wrong"; break;
            case "intent-mode": f.Intent["livemode"] = true; break;
            case "intent-usage": f.Intent["usage"] = "on_session"; break;
            case "intent-status": f.Intent["status"] = "processing"; break;
            case "method-owner": f.Method["customer"] = "cus_other"; break;
            case "method-mode": f.Method["livemode"] = true; break;
            case "method-type": f.Method["type"] = "customer_balance"; break;
            case "method-details": f.Method["card"] = null; break;
        }
        var result = await f.Gateway.ReadAsync(f.KnownLease(), default);
        Assert.Equal(AutopaySetupOutcome.Invalid, result.Outcome); Assert.Null(result.Setup); Assert.Null(f.Auth.PaymentMethodId);
        Assert.All(f.Requests, x => Assert.Equal("GET", x.Method));
    }

    [Fact]
    public async Task Canonical_ready_method_is_recorded_as_evidence_without_activating_or_charging()
    {
        using var f = new Fixture(); var lease = f.KnownLease(); f.Ready();
        var result = await f.Gateway.ReadAsync(lease, default);
        Assert.Equal(AutopaySetupOutcome.Verified, result.Outcome); Assert.Equal(AutopaySetupState.Ready, result.Setup!.State);
        Assert.Equal("pm_setup", result.Setup.PaymentMethodId); Assert.True(result.Setup.OffSessionUsage); Assert.True(result.Setup.SetupOnly);
        Assert.Null(f.Auth.PaymentMethodId); Assert.Null(f.Auth.SubscriptionId); Assert.Equal(AutopayState.AwaitingMethod, f.Auth.State);
        Assert.Equal(2, f.SessionGets); Assert.Equal(2, f.IntentGets); Assert.All(f.Requests, x => Assert.Equal("GET", x.Method));
    }

    [Theory]
    [InlineData("session")]
    [InlineData("intent")]
    [InlineData("owner")]
    public async Task Changed_evidence_during_linked_reads_is_rejected(string race)
    {
        using var f = new Fixture(); var lease = f.KnownLease(); f.Ready();
        f.OnMethod = () =>
        {
            if (race == "session") f.Session["setup_intent"] = "seti_other";
            if (race == "intent") f.Intent["payment_method"] = "pm_other";
            if (race == "owner") f.Customer["metadata"]!["office_account_id"] = Guid.NewGuid().ToString("D");
        };
        Assert.Equal(AutopaySetupOutcome.Invalid, (await f.Gateway.ReadAsync(lease, default)).Outcome);
    }

    [Fact]
    public async Task The_create_acknowledgment_is_not_enough_when_canonical_read_differs()
    {
        using var f = new Fixture(); f.OnFirstSessionGet = () => f.Session["customer"] = "cus_other";
        Assert.Equal(AutopaySetupOutcome.Invalid, (await f.Gateway.CreateAsync(f.Lease, default)).Outcome);
        Assert.Single(f.Requests, x => x.Method == "POST"); Assert.Null(f.Auth.SetupSessionId);
    }

    [Theory]
    [InlineData("network")]
    [InlineData("503")]
    [InlineData("429")]
    [InlineData("401")]
    [InlineData("cancel")]
    public async Task Lost_ack_or_provider_failure_stays_unknown_without_sdk_retry(string failure)
    {
        using var f = new Fixture(); f.Failure = failure;
        Assert.Equal(AutopaySetupOutcome.Unknown, (await f.Gateway.CreateAsync(f.Lease, default)).Outcome);
        Assert.Single(f.Requests, x => x.Method == "POST"); Assert.Null(f.Auth.SetupSessionId);
    }

    [Theory]
    [InlineData("one")]
    [InlineData("none")]
    [InlineData("two")]
    [InlineData("duplicates")]
    [InlineData("more")]
    [InlineData("sentinel")]
    [InlineData("foreign")]
    [InlineData("out-of-range")]
    public async Task Unknown_original_after_write_window_uses_bounded_read_only_investigation(string catalog)
    {
        using var f = new Fixture(); f.ReclaimAfter(TimeSpan.FromHours(23)); f.Lease = f.Lease with { ReadOnly = true };
        f.Catalog.Add(f.Session.DeepClone());
        switch (catalog)
        {
            case "none": f.Catalog.Clear(); break;
            case "two": var other = f.Session.DeepClone(); other["id"] = "cs_second"; f.Catalog.Add(other); break;
            case "duplicates": f.Catalog.Add(f.Session.DeepClone()); break;
            case "more": f.HasMore = true; break;
            case "sentinel": for (var i = 1; i < 26; i++) { var item = f.Session.DeepClone(); item["id"] = "cs_" + i; f.Catalog.Add(item); } break;
            case "foreign": f.Catalog[0]!["customer"] = "cus_other"; break;
            case "out-of-range": f.Catalog[0]!["created"] = f.Work.DispatchedAt!.Value.AddMinutes(-1).ToUnixTimeSeconds(); break;
        }
        var result = await f.Gateway.ReadAsync(f.Lease, default);
        Assert.Equal(catalog == "one" ? AutopaySetupOutcome.Verified : catalog == "none" ? AutopaySetupOutcome.Unknown : AutopaySetupOutcome.Invalid, result.Outcome);
        Assert.All(f.Requests, x => Assert.Equal("GET", x.Method));
        var list = Assert.Single(f.Requests, x => x.Path.StartsWith("/v1/checkout/sessions?", StringComparison.Ordinal));
        var query = Form(list.Path.Split('?', 2)[1]); Assert.Equal("26", query["limit"]); Assert.Equal("cus_setup", query["customer"]);
        Assert.Equal(f.Work.DispatchedAt!.Value.ToUnixTimeSeconds().ToString(), query["created[gte]"]);
        Assert.Equal(f.Work.DispatchedAt.Value.AddHours(24).ToUnixTimeSeconds().ToString(), query["created[lte]"]);
    }

    [Fact]
    public async Task Early_provider_expiration_is_canonical_but_elapsed_time_is_not()
    {
        using var early = new Fixture(); var lease = early.KnownLease(); early.Session["status"] = "expired";
        Assert.Equal(AutopaySetupState.Expired, (await early.Gateway.ReadAsync(lease, default)).Setup!.State);
        using var elapsed = new Fixture(); var known = elapsed.KnownLease(); elapsed.ReclaimAfter(TimeSpan.FromHours(24));
        Assert.Equal(AutopaySetupOutcome.Invalid, (await elapsed.Gateway.ReadAsync(elapsed.Lease, default)).Outcome);
        Assert.All(elapsed.Requests, x => Assert.Equal("GET", x.Method));
    }

    [Fact]
    public async Task Cancellation_and_unavailable_factory_scope_fail_without_transport()
    {
        using var f = new Fixture(); using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Equal(AutopaySetupOutcome.Unknown, (await f.Gateway.CreateAsync(f.Lease, canceled.Token)).Outcome); Assert.Empty(f.Requests);
        var error = Assert.Throws<InvalidOperationException>(() => new StripeAutopaySetupGatewayFactory(new() { ApiKey = "rk_test_synthetic", ProviderAccount = "acct_wrong" }, f.Time).Open(f.Lease));
        Assert.DoesNotContain("synthetic", error.Message);
    }

    private static Dictionary<string, string> Form(string body) => body.Split('&').Select(x => x.Split('=', 2)).ToDictionary(x => WebUtility.UrlDecode(x[0]), x => WebUtility.UrlDecode(x.Length > 1 ? x[1] : ""));
    private sealed class Clock : TimeProvider { public DateTimeOffset Now { get; set; } = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed record Request(string Method, string Path, string Body, string? Key, string? Version, string? ConnectAccount);
    private sealed class Fixture : HttpMessageHandler
    {
        public Clock Time { get; } = new();
        public AutopayAuthorization Auth { get; }
        public AutopayWork Work { get; }
        public AutopaySetupLease Lease { get; set; }
        public StripeAutopaySetupGateway Gateway { get; }
        public JsonObject Customer { get; }
        public JsonObject Session { get; }
        public JsonObject Intent { get; }
        public JsonObject Method { get; }
        public JsonArray Catalog { get; } = [];
        public List<Request> Requests { get; } = [];
        public string Merchant { get; set; } = "acct_setup";
        public bool HasMore { get; set; }
        public int SessionGets { get; private set; }
        public int IntentGets { get; private set; }
        public Action? OnCustomer { get; set; }
        public Action? OnMethod { get; set; }
        public Action? OnFirstSessionGet { get; set; }
        public string? Failure { get; set; }
        private readonly HttpClient _http;
        public Fixture()
        {
            var account = RentAccount.Open(Guid.NewGuid(), Guid.NewGuid()); var binding = BillingCustomerBinding.Create(account.Id, "acct_setup", false, "cus_setup", Time.Now);
            var terms = RentAgreement.Create(account.Id, new Money(100000), 1, new(2026, 1, 1), null, "payer", Time.Now);
            Auth = AutopayAuthorization.Consent(Guid.NewGuid(), "payer", account, binding, AutopaySchedule.Select(terms, Time.Now, []), true, Time.Now);
            Work = AutopayWork.Enqueue(Auth, AutopayWorkStage.Setup, Time.Now, new("https://office.example.test", "pmc_reviewed"));
            Work.Claim(Time.Now); Work.BeginDispatch(Time.Now); Lease = new(Work, Auth, Work.LeaseToken, false);
            Customer = new() { ["id"] = "cus_setup", ["object"] = "customer", ["livemode"] = false, ["metadata"] = new JsonObject { ["office_account_id"] = account.Id.ToString("D") } };
            Session = new()
            {
                ["id"] = "cs_setup",
                ["object"] = "checkout.session",
                ["customer"] = "cus_setup",
                ["livemode"] = false,
                ["mode"] = "setup",
                ["ui_mode"] = "hosted_page",
                ["status"] = "open",
                ["payment_status"] = "unpaid",
                ["client_reference_id"] = Auth.Id.ToString("D"),
                ["created"] = Time.Now.ToUnixTimeSeconds(),
                ["metadata"] = Metadata(),
                ["expires_at"] = Time.Now.AddHours(24).ToUnixTimeSeconds(),
                ["payment_method_configuration_details"] = new JsonObject { ["id"] = "pmc_reviewed" }
            };
            Intent = new() { ["id"] = "seti_setup", ["object"] = "setup_intent", ["customer"] = "cus_setup", ["livemode"] = false, ["usage"] = "off_session", ["status"] = "succeeded", ["payment_method"] = "pm_setup", ["metadata"] = Metadata() };
            Method = new() { ["id"] = "pm_setup", ["object"] = "payment_method", ["customer"] = "cus_setup", ["livemode"] = false, ["type"] = "card", ["card"] = new JsonObject { ["brand"] = "visa", ["last4"] = "4242" } };
            _http = new HttpClient(this, disposeHandler: false);
            var client = new StripeClient("rk_test_synthetic", httpClient: new SystemNetHttpClient(_http, maxNetworkRetries: 0, enableTelemetry: false));
            Gateway = new(new() { ApiKey = "rk_test_synthetic", ProviderAccount = "acct_setup" }, Time, client);
        }
        private JsonObject Metadata() => new() { ["office_account_id"] = Auth.AccountId.ToString("D"), ["office_autopay_id"] = Auth.Id.ToString("D"), ["office_operation_id"] = Work.OriginalOperationId.ToString("D"), ["office_consent_hash"] = Auth.ConsentHash };
        public VerifiedAutopaySetup Waiting() => new(Auth.ProviderAccount, Auth.LiveMode, Auth.AccountId, Auth.CustomerId, Auth.ConsentHash, "cs_setup", Time.Now.AddHours(24), AutopaySetupState.Waiting, null, true, false, Time.Now);
        public AutopaySetupLease KnownLease() { Auth.ObserveVerifiedSetup(Waiting(), false); return Lease with { ReadOnly = true }; }
        public void Ready() { Session["status"] = "complete"; Session["payment_status"] = "no_payment_required"; Session["setup_intent"] = "seti_setup"; }
        public void ReclaimAfter(TimeSpan delay) { Time.Now = Time.Now.Add(delay); Assert.True(Work.Claim(Time.Now)); Lease = Lease with { Token = Work.LeaseToken }; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); string? Header(string name) => request.Headers.TryGetValues(name, out var values) ? values.Single() : null;
            var path = request.RequestUri!.PathAndQuery;
            Requests.Add(new(request.Method.Method, path, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken), Header("Idempotency-Key"), Header("Stripe-Version"), Header("Stripe-Account")));
            if (request.Method == HttpMethod.Post && Failure is { } failure)
            {
                if (failure == "network") throw new HttpRequestException("Lost synthetic acknowledgment");
                if (failure == "cancel") throw new OperationCanceledException(cancellationToken);
                return Json(new JsonObject { ["error"] = new JsonObject { ["type"] = "api_error", ["message"] = "Synthetic failure" } }, (HttpStatusCode)int.Parse(failure));
            }
            if (path == "/v1/account") return Json(new JsonObject { ["id"] = Merchant, ["object"] = "account" });
            if (path == "/v1/customers/cus_setup") { OnCustomer?.Invoke(); return Json(Customer); }
            if (path.StartsWith("/v1/checkout/sessions?", StringComparison.Ordinal)) return Json(new JsonObject { ["object"] = "list", ["data"] = Catalog.DeepClone(), ["has_more"] = HasMore, ["url"] = "/v1/checkout/sessions" });
            if (path == "/v1/checkout/sessions/cs_setup") { SessionGets++; if (SessionGets == 1) OnFirstSessionGet?.Invoke(); return Json(Session); }
            if (path == "/v1/checkout/sessions" && request.Method == HttpMethod.Post) return Json(Session);
            if (path == "/v1/setup_intents/seti_setup") { IntentGets++; return Json(Intent); }
            if (path == "/v1/payment_methods/pm_setup") { OnMethod?.Invoke(); return Json(Method); }
            throw new InvalidOperationException("Unexpected synthetic provider path.");
        }
        private static HttpResponseMessage Json(JsonObject value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(value.ToJsonString(), Encoding.UTF8, "application/json") };
        protected override void Dispose(bool disposing) { if (disposing) { Gateway.Dispose(); _http.Dispose(); } base.Dispose(disposing); }
    }
}
