using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Infrastructure.Payments;
using Stripe;

namespace AllenBuilding.IntegrationTests;

public sealed class StripeCustomerProvisionTests
{
    [Fact]
    public async Task Creation_and_readback_use_original_scope_and_stable_body_without_contact_or_consent_copying()
    {
        using var fixture = new Fixture();
        var first = await fixture.Gateway.CreateAsync(fixture.Original, CancellationToken.None);
        var second = await fixture.Gateway.CreateAsync(fixture.Original, CancellationToken.None);
        Assert.Equal(CustomerProvisionOutcome.Verified, first.Outcome); Assert.Equal(first, second);
        var posts = fixture.Requests.Where(x => x.Method == "POST").ToArray(); Assert.Equal(2, posts.Length); Assert.Equal(posts[0], posts[1]);
        Assert.Equal(fixture.Original.ProviderIdempotencyKey, posts[0].Key); Assert.Equal(CheckoutProfile.ApiContract, posts[0].Version); Assert.Null(posts[0].ConnectAccount);
        var body = Uri.UnescapeDataString(posts[0].Body); Assert.Contains("metadata[office_account_id]=" + fixture.Original.AccountId, body); Assert.Contains("metadata[office_customer_operation]=" + fixture.Original.Id, body);
        Assert.DoesNotContain("email=", body); Assert.DoesNotContain("name=", body); Assert.DoesNotContain("payment_method", body); Assert.DoesNotContain("balance=", body);
        Assert.Equal(2, fixture.Requests.Count(x => x.Method == "GET" && x.Path == "/v1/customers/cus_provision"));
        Assert.Equal(CustomerProvisionState.Leased, fixture.Original.State); // Adapter does not persist.
    }
    [Theory]
    [InlineData("merchant")]
    [InlineData("mode")]
    [InlineData("account")]
    [InlineData("operation")]
    [InlineData("deleted")]
    [InlineData("balance")]
    [InlineData("default-source")]
    [InlineData("default-method")]
    [InlineData("readback")]
    public async Task Canonical_mismatch_cannot_bind_or_share_a_customer(string mutation)
    {
        using var fixture = new Fixture();
        switch (mutation)
        {
            case "merchant": fixture.Merchant = "acct_wrong"; break;
            case "mode": fixture.Customer["livemode"] = true; break;
            case "account": fixture.Customer["metadata"]!["office_account_id"] = Guid.NewGuid().ToString("D"); break;
            case "operation": fixture.Customer["metadata"]!["office_customer_operation"] = Guid.NewGuid().ToString("D"); break;
            case "deleted": fixture.Customer["deleted"] = true; break;
            case "balance": fixture.Customer["balance"] = -200; break;
            case "default-source": fixture.Customer["default_source"] = "card_other"; break;
            case "default-method": fixture.Customer["invoice_settings"] = new JsonObject { ["default_payment_method"] = "pm_other" }; break;
            case "readback": fixture.ReadbackWrong = true; break;
        }
        var result = await fixture.Gateway.CreateAsync(fixture.Original, CancellationToken.None); Assert.Equal(CustomerProvisionOutcome.Invalid, result.Outcome); Assert.Null(result.CustomerId);
        if (mutation == "merchant") Assert.DoesNotContain(fixture.Requests, x => x.Method == "POST");
    }
    [Theory]
    [InlineData("network")]
    [InlineData("cancel")]
    [InlineData("429")]
    [InlineData("500")]
    [InlineData("read-lost")]
    public async Task Lost_create_or_read_acknowledgment_never_becomes_a_local_binding(string failure)
    {
        using var fixture = new Fixture { Failure = failure };
        var result = await fixture.Gateway.CreateAsync(fixture.Original, CancellationToken.None); Assert.Equal(CustomerProvisionOutcome.Unknown, result.Outcome); Assert.Null(result.CustomerId);
        Assert.Null(fixture.Original.CustomerId); Assert.Single(fixture.Requests, x => x.Method == "POST");
    }
    [Fact]
    public async Task Aged_unknown_operation_and_expired_lease_never_create_a_new_customer()
    {
        using var fixture = new Fixture(); fixture.Clock.Now = fixture.Clock.Now.AddHours(23);
        Assert.Equal(CustomerProvisionOutcome.Invalid, (await fixture.Gateway.CreateAsync(fixture.Original, CancellationToken.None)).Outcome); Assert.Empty(fixture.Requests);
    }
    [Fact]
    public void Factory_rejects_changed_mode_merchant_and_mismatched_credentials()
    {
        using var fixture = new Fixture(); var options = new StripeCustomerCredentials { ApiKey = "rk_test_synthetic", ProviderAccount = "acct_provision", LiveMode = false };
        var factory = new StripeCustomerProvisionGatewayFactory(options, fixture.Clock); using var adapter = (IDisposable)factory.Open(fixture.Original);
        options.LiveMode = true; Assert.Throws<InvalidOperationException>(() => factory.Open(fixture.Original));
        options.LiveMode = false; options.ProviderAccount = "acct_other"; Assert.Throws<InvalidOperationException>(() => factory.Open(fixture.Original));
        options.ProviderAccount = "acct_provision"; options.ApiKey = "sk_live_wrong"; Assert.Throws<InvalidOperationException>(() => factory.Open(fixture.Original));
    }
    private sealed record Request(string Method, string Path, string Body, string? Key, string? Version, string? ConnectAccount);
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now { get; set; } = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Fixture : HttpMessageHandler
    {
        public Clock Clock { get; } = new();
        public CustomerProvision Original { get; }
        public StripeCustomerProvisionGateway Gateway { get; }
        public List<Request> Requests { get; } = [];
        public JsonObject Customer { get; }
        public string Merchant { get; set; } = "acct_provision";
        public string? Failure { get; set; }
        public bool ReadbackWrong { get; set; }
        private readonly HttpClient _http;
        public Fixture()
        {
            Original = CustomerProvision.Enqueue(Guid.NewGuid(), "synthetic-payer", "acct_provision", false, Clock.Now); Original.Claim(Clock.Now); Original.BeginDispatch(Clock.Now);
            Customer = new()
            {
                ["id"] = "cus_provision",
                ["object"] = "customer",
                ["livemode"] = false,
                ["balance"] = 0,
                ["metadata"] = new JsonObject { ["office_account_id"] = Original.AccountId.ToString("D"), ["office_customer_operation"] = Original.Id.ToString("D") }
            };
            _http = new HttpClient(this, disposeHandler: false);
            var client = new StripeClient("rk_test_synthetic", httpClient: new SystemNetHttpClient(_http, maxNetworkRetries: 0, enableTelemetry: false));
            Gateway = new StripeCustomerProvisionGateway(new() { ApiKey = "rk_test_synthetic", ProviderAccount = "acct_provision" }, Clock, client);
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string? Header(string name) => request.Headers.TryGetValues(name, out var values) ? values.Single() : null;
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(new(request.Method.Method, path, request.Content is null ? "" : await request.Content.ReadAsStringAsync(token), Header("Idempotency-Key"), Header("Stripe-Version"), Header("Stripe-Account")));
            if ((request.Method == HttpMethod.Post && Failure is not null && Failure != "read-lost") || (path == "/v1/customers/cus_provision" && Failure == "read-lost"))
            {
                if (Failure is "network" or "read-lost") throw new HttpRequestException("Synthetic uncertainty");
                if (Failure == "cancel") throw new OperationCanceledException(token);
                return Json(new() { ["error"] = new JsonObject { ["type"] = "api_error", ["message"] = "Synthetic failure" } }, (HttpStatusCode)int.Parse(Failure!));
            }
            if (path == "/v1/account") return Json(new() { ["id"] = Merchant, ["object"] = "account" });
            if (path == "/v1/customers" && request.Method == HttpMethod.Post) return Json(Customer);
            if (path == "/v1/customers/cus_provision")
            {
                var copy = (JsonObject)Customer.DeepClone(); if (ReadbackWrong) copy["id"] = "cus_wrong"; return Json(copy);
            }
            throw new InvalidOperationException("Unexpected provider request.");
        }
        private static HttpResponseMessage Json(JsonObject body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        protected override void Dispose(bool disposing) { if (disposing) { Gateway.Dispose(); _http.Dispose(); } base.Dispose(disposing); }
    }
}
