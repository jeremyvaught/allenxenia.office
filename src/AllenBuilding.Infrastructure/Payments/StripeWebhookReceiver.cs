using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Stripe;

namespace AllenBuilding.Infrastructure.Payments;

public sealed class StripeWebhookOptions
{
    public string ProviderAccount { get; set; } = "";
    public bool LiveMode { get; set; }
    public string SigningSecret { get; set; } = "";
    public string? PreviousSigningSecret { get; set; }
    public void Validate()
    {
        static bool Secret(string value) => value.Length > 6 && value.StartsWith("whsec_", StringComparison.Ordinal) && !value.Any(char.IsWhiteSpace);
        if (!PaymentReferences.Valid(ProviderAccount, "acct_") || !Secret(SigningSecret) ||
            (PreviousSigningSecret is { } previous && !Secret(previous))) throw new InvalidOperationException("A scoped payment webhook signing configuration is required.");
    }
}

public sealed class StripeWebhookReceiver : IPaymentWebhookReceiver
{
    public const int MaximumBodyBytes = 256 * 1024;
    private readonly OfficeDbContext _db;
    private readonly TimeProvider _time;
    private readonly string _account;
    private readonly bool _liveMode;
    private readonly string[] _secrets;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly IReadOnlyDictionary<string, (string Prefix, string Object)> Types = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
    {
        ["checkout.session.completed"] = ("cs_", "checkout.session"),
        ["checkout.session.expired"] = ("cs_", "checkout.session"),
        ["checkout.session.async_payment_succeeded"] = ("cs_", "checkout.session"),
        ["checkout.session.async_payment_failed"] = ("cs_", "checkout.session"),
        ["payment_intent.succeeded"] = ("pi_", "payment_intent"),
        ["payment_intent.payment_failed"] = ("pi_", "payment_intent"),
        ["payment_intent.processing"] = ("pi_", "payment_intent"),
        ["payment_intent.canceled"] = ("pi_", "payment_intent"),
        ["invoice.paid"] = ("in_", "invoice"),
        ["invoice.payment_failed"] = ("in_", "invoice"),
        ["invoice.payment_action_required"] = ("in_", "invoice"),
        ["invoice.voided"] = ("in_", "invoice"),
        ["invoice.marked_uncollectible"] = ("in_", "invoice"),
        ["invoice.finalized"] = ("in_", "invoice"),
        ["charge.refunded"] = ("ch_", "charge"),
        ["charge.dispute.created"] = ("dp_", "dispute"),
        ["charge.dispute.updated"] = ("dp_", "dispute"),
        ["charge.dispute.closed"] = ("dp_", "dispute"),
        ["charge.dispute.funds_withdrawn"] = ("dp_", "dispute"),
        ["charge.dispute.funds_reinstated"] = ("dp_", "dispute"),
        ["refund.created"] = ("re_", "refund"),
        ["refund.updated"] = ("re_", "refund"),
        ["refund.failed"] = ("re_", "refund"),
        ["setup_intent.succeeded"] = ("seti_", "setup_intent"),
        ["setup_intent.setup_failed"] = ("seti_", "setup_intent"),
        ["setup_intent.canceled"] = ("seti_", "setup_intent"),
        ["customer.subscription.created"] = ("sub_", "subscription"),
        ["customer.subscription.updated"] = ("sub_", "subscription"),
        ["customer.subscription.deleted"] = ("sub_", "subscription")
    };
    public StripeWebhookReceiver(OfficeDbContext db, StripeWebhookOptions options, TimeProvider time)
    {
        options.Validate(); _db = db; _time = time; _account = options.ProviderAccount; _liveMode = options.LiveMode;
        _secrets = options.PreviousSigningSecret is null ? [options.SigningSecret] : [options.SigningSecret, options.PreviousSigningSecret];
    }
    public async Task<WebhookCode> ReceiveAsync(ReadOnlyMemory<byte> body, string signature, CancellationToken cancellationToken = default)
    {
        if (body.Length is < 1 or > MaximumBodyBytes || string.IsNullOrWhiteSpace(signature) || signature.Length > 4096) return WebhookCode.Rejected;
        var now = _time.GetUtcNow();
        if (!FreshTimestamp(signature, now.ToUnixTimeSeconds())) return WebhookCode.Rejected;
        PaymentEventInbox? receipt;
        try
        {
            var json = StrictUtf8.GetString(body.Span);
            if (!_secrets.Any(secret => Signed(json, signature, secret, now.ToUnixTimeSeconds()))) return WebhookCode.Rejected;
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            // Reject duplicate JSON property names, which parsers otherwise interpret differently.
            if (!UniqueProperties(root) || Text(root, "object") != "event" || Flag(root, "livemode") != _liveMode) return WebhookCode.Rejected;
            var eventAccount = Text(root, "account");
            if (eventAccount is not null && eventAccount != _account) return WebhookCode.Rejected;
            var eventId = Text(root, "id"); var type = Text(root, "type"); var version = Text(root, "api_version");
            if (eventId is null || !PaymentReferences.Valid(eventId, "evt_") || type is null || version is null) return WebhookCode.Rejected;
            if (!Types.TryGetValue(type, out var shape)) return WebhookCode.Ignored;
            if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("object", out var resource)) return WebhookCode.Rejected;
            var id = Text(resource, "id");
            if (id is null || !PaymentReferences.Valid(id, shape.Prefix) || Text(resource, "object") != shape.Object ||
                (resource.TryGetProperty("livemode", out _) && Flag(resource, "livemode") != _liveMode)) return WebhookCode.Rejected;
            receipt = PaymentEventInbox.Receive(_account, _liveMode, eventId, type, id, version, Convert.ToHexString(SHA256.HashData(body.Span)), version == StripeConfiguration.ApiVersion, now);
        }
        catch (DecoderFallbackException) { return WebhookCode.Rejected; }
        catch (JsonException) { return WebhookCode.Rejected; }
        catch (ArgumentException) { return WebhookCode.Rejected; }
        catch (InvalidOperationException) { return WebhookCode.Rejected; }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var existing = await Existing(receipt, deadline.Token);
            if (existing is not null) return existing.SameEnvelope(receipt) ? WebhookCode.Duplicate : WebhookCode.Rejected;
            _db.PaymentEvents.Add(receipt);
            try { await _db.SaveChangesAsync(deadline.Token); }
            catch (DbUpdateException e) when (e.InnerException is MySql.Data.MySqlClient.MySqlException { Number: 1062 } or Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 })
            {
                _db.Entry(receipt).State = EntityState.Detached;
                var winner = await Existing(receipt, deadline.Token);
                return winner is not null && winner.SameEnvelope(receipt) ? WebhookCode.Duplicate : WebhookCode.Rejected;
            }
            return WebhookCode.Accepted; // Only after the database acknowledged the durable write.
        }
        catch (OperationCanceledException) { return WebhookCode.Unavailable; }
        catch (DbUpdateException) { return WebhookCode.Unavailable; }
        catch (System.Data.Common.DbException) { return WebhookCode.Unavailable; }
    }
    private Task<PaymentEventInbox?> Existing(PaymentEventInbox receipt, CancellationToken token) => _db.PaymentEvents.AsNoTracking()
        .SingleOrDefaultAsync(x => x.ProviderAccount == _account && x.LiveMode == _liveMode && x.EventId == receipt.EventId, token);
    private static bool Signed(string body, string signature, string secret, long now)
    {
        try { EventUtility.ValidateSignature(body, signature, secret, tolerance: 300, utcNow: now); return true; }
        catch (StripeException) { return false; }
        catch (ArgumentException) { return false; }
        catch (FormatException) { return false; }
    }
    private static bool FreshTimestamp(string header, long now)
    {
        var timestamps = header.Split(',').Select(x => x.Trim().Split('=', 2)).Where(x => x.Length == 2 && x[0] == "t").Select(x => x[1]).ToArray();
        return timestamps.Length == 1 && long.TryParse(timestamps[0], NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp) && timestamp >= now - 300 && timestamp <= now + 300;
    }
    private static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static bool? Flag(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False ? property.GetBoolean() : null;
    private static bool UniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject()) if (!seen.Add(property.Name) || !UniqueProperties(property.Value)) return false;
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) if (!UniqueProperties(item)) return false;
        return true;
    }
}
