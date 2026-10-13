using System.Net;
using System.Text;
using AllenBuilding.Application.Billing;
using AllenBuilding.Infrastructure.Payments;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AllenBuilding.IntegrationTests;

public sealed class PaymentWebhookTests
{
    [Theory]
    [InlineData("tampered")]
    [InlineData("old")]
    [InlineData("future")]
    [InlineData("duplicate-timestamp")]
    [InlineData("signature")]
    [InlineData("mode")]
    [InlineData("merchant")]
    [InlineData("resource")]
    [InlineData("duplicate-json")]
    [InlineData("encoding")]
    [InlineData("oversized")]
    public async Task Invalid_authenticated_envelopes_are_rejected_before_opening_database(string invalid)
    {
        await using var db = EmptyDatabase();
        var body = PaymentWebhookContract.Body("evt_invalid"); var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (invalid == "mode") body = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(body).Replace("\"livemode\":false", "\"livemode\":true"));
        if (invalid == "merchant") body = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(body).Replace("\"object\":\"event\"", "\"object\":\"event\",\"account\":\"acct_wrong\""));
        if (invalid == "resource") body = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(body).Replace("cs_webhook", "pi_wrong"));
        if (invalid == "duplicate-json") body = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(body).Replace("\"id\":\"evt_invalid\"", "\"id\":\"evt_invalid\",\"id\":\"evt_other\""));
        if (invalid == "encoding") body = [0xff, 0xfe];
        if (invalid == "oversized") body = new byte[StripeWebhookReceiver.MaximumBodyBytes + 1];
        var timestamp = invalid == "old" ? now - 301 : invalid == "future" ? now + 301 : now;
        var signature = PaymentWebhookContract.Sign(body, timestamp);
        if (invalid == "signature") signature = "t=" + now + ",v1=0000";
        if (invalid == "duplicate-timestamp") signature += ",t=" + now;
        if (invalid == "tampered") body = body.Concat(new byte[] { 32 }).ToArray();
        Assert.Equal(WebhookCode.Rejected, await new StripeWebhookReceiver(db, PaymentWebhookContract.Options(), TimeProvider.System).ReceiveAsync(body, signature));
        Assert.Equal(System.Data.ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    [Theory]
    [InlineData("charge.dispute.created", "du_example")]
    [InlineData("charge.dispute.updated", "du_example")]
    [InlineData("charge.dispute.closed", "du_example")]
    [InlineData("charge.dispute.funds_withdrawn", "du_example")]
    [InlineData("charge.dispute.funds_reinstated", "du_example")]
    [InlineData("charge.dispute.created", "dp_existing")]
    public async Task Documented_dispute_resource_shapes_are_durably_received(string eventType, string resourceId)
    {
        await using var db = EmptyDatabase(); await db.Database.OpenConnectionAsync(); await db.Database.MigrateAsync();
        var body = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(PaymentWebhookContract.Body("evt_dispute"))
            .Replace("checkout.session.completed", eventType).Replace("cs_webhook", resourceId).Replace("\"object\":\"checkout.session\"", "\"object\":\"dispute\""));
        var receiver = new StripeWebhookReceiver(db, PaymentWebhookContract.Options(), TimeProvider.System);
        Assert.Equal(WebhookCode.Accepted, await receiver.ReceiveAsync(body, PaymentWebhookContract.Sign(body, DateTimeOffset.UtcNow.ToUnixTimeSeconds())));
        var receipt = await db.PaymentEvents.SingleAsync(); Assert.Equal(resourceId, receipt.ResourceId); Assert.Equal(eventType, receipt.EventType);
    }

    [Fact]
    public async Task Missing_schema_or_canceled_persistence_never_acknowledges_a_financial_event()
    {
        await using var db = EmptyDatabase(); var body = PaymentWebhookContract.Body("evt_unavailable");
        var signature = PaymentWebhookContract.Sign(body, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var receiver = new StripeWebhookReceiver(db, PaymentWebhookContract.Options(), TimeProvider.System);
        Assert.Equal(WebhookCode.Unavailable, await receiver.ReceiveAsync(body, signature));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Equal(WebhookCode.Unavailable, await receiver.ReceiveAsync(body, signature, canceled.Token));
    }

    [Fact]
    public async Task Signing_secret_rotation_accepts_the_previous_secret_but_does_not_accept_unsigned_events()
    {
        await using var db = EmptyDatabase(); await db.Database.OpenConnectionAsync(); await db.Database.MigrateAsync();
        var options = PaymentWebhookContract.Options(); options.PreviousSigningSecret = "whsec_synthetic_previous";
        var body = PaymentWebhookContract.Body("evt_rotation"); var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var receiver = new StripeWebhookReceiver(db, options, TimeProvider.System);
        Assert.Equal(WebhookCode.Accepted, await receiver.ReceiveAsync(body, PaymentWebhookContract.Sign(body, timestamp, options.PreviousSigningSecret)));
        Assert.Equal(WebhookCode.Rejected, await receiver.ReceiveAsync(body, PaymentWebhookContract.Sign(body, timestamp, "whsec_unrelated")));
    }

    [Fact]
    public async Task Unknown_authenticated_nonfinancial_event_is_ignored_without_persistence()
    {
        await using var db = EmptyDatabase();
        var body = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(PaymentWebhookContract.Body("evt_ignored")).Replace("checkout.session.completed", "customer.created"));
        Assert.Equal(WebhookCode.Ignored, await new StripeWebhookReceiver(db, PaymentWebhookContract.Options(), TimeProvider.System)
            .ReceiveAsync(body, PaymentWebhookContract.Sign(body, DateTimeOffset.UtcNow.ToUnixTimeSeconds())));
        Assert.Equal(System.Data.ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task Webhook_http_boundary_uses_signatures_without_browser_antiforgery_and_acknowledges_only_durable_acceptance()
    {
        await using var foundation = new AccountTestHost();
        await using var host = foundation.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton(PaymentWebhookContract.Options());
            services.Replace(ServiceDescriptor.Scoped<IPaymentWebhookReceiver, StripeWebhookReceiver>());
        }));
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var body = PaymentWebhookContract.Body("evt_http"); var signature = PaymentWebhookContract.Sign(body, foundation.Clock.GetUtcNow().ToUnixTimeSeconds());
        async Task<HttpResponseMessage> Send(byte[] payload, string? signed, string contentType = "application/json", bool unknownSize = false)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/payments/stripe/webhook") { Content = unknownSize ? new UnknownLengthContent(payload) : new ByteArrayContent(payload) };
            request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
            if (signed is not null) request.Headers.Add("Stripe-Signature", signed);
            return await client.SendAsync(request);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(body, null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(body, signature, "text/plain")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(body, signature, "application/json; charset=iso-8859-1")).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Send(new byte[StripeWebhookReceiver.MaximumBodyBytes + 1], signature)).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Send(new byte[StripeWebhookReceiver.MaximumBodyBytes + 1], signature, unknownSize: true)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Send(body, signature)).StatusCode);
        using (var scope = host.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().Database.MigrateAsync();
        Assert.Equal(HttpStatusCode.OK, (await Send(body, signature, unknownSize: true)).StatusCode);
        var duplicate = await Send(body, signature); Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.Empty(await duplicate.Content.ReadAsStringAsync());
        using (var scope = host.Services.CreateScope()) Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().PaymentEvents.CountAsync());
    }

    [Fact]
    public async Task Default_webhook_is_unavailable_until_its_readiness_gate_is_enabled()
    {
        await using var host = new AccountTestHost(); using var client = host.Browser();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsync("/payments/stripe/webhook", new StringContent("{}"))).StatusCode);
    }

    [Theory]
    [InlineData("invoice.payment_action_required", "in_notice", "invoice")]
    [InlineData("refund.created", "re_notice", "refund")]
    [InlineData("charge.dispute.funds_withdrawn", "dp_notice", "dispute")]
    [InlineData("setup_intent.succeeded", "seti_notice", "setup_intent")]
    [InlineData("customer.subscription.created", "sub_notice", "subscription")]
    public async Task Financial_and_enrollment_changes_are_durable_not_silently_ignored(string eventType, string resourceId, string resourceType)
    {
        await using var db = EmptyDatabase(); await db.Database.OpenConnectionAsync(); await db.Database.MigrateAsync();
        var json = Encoding.UTF8.GetString(PaymentWebhookContract.Body("evt_notice"))
            .Replace("checkout.session.completed", eventType).Replace("cs_webhook", resourceId).Replace("checkout.session", resourceType);
        var body = Encoding.UTF8.GetBytes(json);
        Assert.Equal(WebhookCode.Accepted, await new StripeWebhookReceiver(db, PaymentWebhookContract.Options(), TimeProvider.System)
            .ReceiveAsync(body, PaymentWebhookContract.Sign(body, DateTimeOffset.UtcNow.ToUnixTimeSeconds())));
        var receipt = await db.PaymentEvents.AsNoTracking().SingleAsync(); Assert.Equal(eventType, receipt.EventType); Assert.Equal(resourceId, receipt.ResourceId);
    }

    private static SqliteOfficeDbContext EmptyDatabase()
    {
        var db = new SqliteOfficeDbContext(new DbContextOptionsBuilder<SqliteOfficeDbContext>().UseSqlite("Data Source=:memory:;Foreign Keys=True").Options);
        // Receiver rejection tests never open this connection. Persistent receiver tests open it explicitly.
        return db;
    }
    private sealed class UnknownLengthContent(byte[] body) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(body).AsTask();
    }
}
