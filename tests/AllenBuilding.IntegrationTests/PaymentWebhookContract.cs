using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Infrastructure.Payments;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.IntegrationTests;

public static class PaymentWebhookContract
{
    internal const string Secret = "whsec_synthetic_current";
    internal static StripeWebhookOptions Options() => new() { ProviderAccount = "acct_webhook", SigningSecret = Secret };
    internal static byte[] Body(string id, string version = "2026-09-30.endive") => Encoding.UTF8.GetBytes(new JsonObject
    {
        ["id"] = id,
        ["object"] = "event",
        ["type"] = "checkout.session.completed",
        ["api_version"] = version,
        ["livemode"] = false,
        ["data"] = new JsonObject
        {
            ["object"] = new JsonObject
            {
                ["id"] = "cs_webhook",
                ["object"] = "checkout.session",
                ["livemode"] = false,
                ["customer_email"] = "private-payer@example.test",
                ["metadata"] = new JsonObject { ["private_note"] = "Never persist this payload" }
            }
        }
    }.ToJsonString());
    internal static string Sign(byte[] body, long timestamp, string secret = Secret)
    {
        var prefix = Encoding.UTF8.GetBytes(timestamp + ".");
        var message = new byte[prefix.Length + body.Length]; prefix.CopyTo(message, 0); body.CopyTo(message, prefix.Length);
        return "t=" + timestamp + ",v1=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), message)).ToLowerInvariant();
    }

    public static async Task VerifyAsync(Func<OfficeDbContext> create)
    {
        var eventId = "evt_" + Guid.NewGuid().ToString("N"); var body = Body(eventId);
        var signature = Sign(body, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        async Task<WebhookCode> Receive(byte[] payload, string signed)
        {
            await using var db = create(); return await new StripeWebhookReceiver(db, Options(), TimeProvider.System).ReceiveAsync(payload, signed);
        }
        var contenders = await Task.WhenAll(Task.Run(() => Receive(body, signature)), Task.Run(() => Receive(body, signature)));
        Assert.Single(contenders, x => x == WebhookCode.Accepted); Assert.Single(contenders, x => x == WebhookCode.Duplicate);
        Assert.Equal(WebhookCode.Duplicate, await Receive(body, signature));
        // Delivery serialization may differ; immutable event/resource scope determines replay, canonical reads determine money.
        var delivery = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(body).Replace("private-payer@example.test", "other-private@example.test"));
        Assert.Equal(WebhookCode.Duplicate, await Receive(delivery, Sign(delivery, DateTimeOffset.UtcNow.ToUnixTimeSeconds())));
        var changed = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(body).Replace("cs_webhook", "cs_another"));
        Assert.Equal(WebhookCode.Rejected, await Receive(changed, Sign(changed, DateTimeOffset.UtcNow.ToUnixTimeSeconds())));
        var oldId = "evt_" + Guid.NewGuid().ToString("N"); var unsupported = Body(oldId, "2026-08-26.dahlia");
        Assert.Equal(WebhookCode.Accepted, await Receive(unsupported, Sign(unsupported, DateTimeOffset.UtcNow.ToUnixTimeSeconds())));
        var uppercaseId = eventId.Replace("evt_", "evt_A"); var lowercaseId = eventId.Replace("evt_", "evt_a");
        foreach (var id in new[] { uppercaseId, lowercaseId })
        {
            var distinct = Body(id); Assert.Equal(WebhookCode.Accepted, await Receive(distinct, Sign(distinct, DateTimeOffset.UtcNow.ToUnixTimeSeconds())));
        }
        await using (var db = create())
        {
            var original = await db.PaymentEvents.SingleAsync(x => x.ProviderAccount == "acct_webhook" && x.EventId == eventId);
            Assert.Equal(PaymentEventState.Pending, original.State); Assert.Equal("cs_webhook", original.ResourceId);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(body)), original.PayloadHash);
            Assert.Equal(1, await db.PaymentEvents.CountAsync(x => x.EventId == eventId));
            Assert.Equal(PaymentEventState.NeedsReview, (await db.PaymentEvents.SingleAsync(x => x.EventId == oldId)).State);
            Assert.Equal("UnsupportedApiVersion", (await db.PaymentEvents.SingleAsync(x => x.EventId == oldId)).ReviewReason);
            Assert.Equal(2, await db.PaymentEvents.CountAsync(x => x.EventId == uppercaseId || x.EventId == lowercaseId));
            var columns = db.Model.FindEntityType(typeof(PaymentEventInbox))!.GetProperties().Select(x => x.Name).ToArray();
            Assert.DoesNotContain(columns, x => x.Contains("Body", StringComparison.Ordinal) || x.Contains("Email", StringComparison.Ordinal) || x.Contains("Secret", StringComparison.Ordinal));
            if (db is MySqlOfficeDbContext)
            {
                await db.Database.OpenConnectionAsync();
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='payment_events' AND COLUMN_NAME IN ('event_id','resource_id','provider_account') AND COLLATION_NAME='utf8mb4_bin'";
                Assert.Equal(3, Convert.ToInt32(await command.ExecuteScalarAsync()));
            }
        }
    }
}
