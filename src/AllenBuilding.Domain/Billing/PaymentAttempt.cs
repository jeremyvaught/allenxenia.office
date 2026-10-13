using System.Security.Cryptography;
using System.Text.Json;

namespace AllenBuilding.Domain.Billing;

public enum PaymentAttemptState { Admitted, Dispatching, HandoffReady, Processing, NeedsReview, Settled, Uncollected }
public sealed record ReviewedCollectionBalance(Guid ObligationId, Guid AccountId, Guid Version, long RemainingMinorUnits, string Currency = "USD", bool HasProviderInstruction = false, bool Held = false);
public sealed record CanonicalSettlement(string ProviderAccount, bool LiveMode, string CustomerId, string SessionId, string PaymentId, long AmountMinorUnits, string Currency);

public static class PaymentReferences
{
    public static bool Valid(string value, string prefix) => !string.IsNullOrWhiteSpace(value) && value.Length <= 255 && value.StartsWith(prefix, StringComparison.Ordinal) &&
        value.Length > prefix.Length && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}

// A permanent provider customer owner. A new tenancy gets a new binding; term replacements reuse it.
public sealed class BillingCustomerBinding
{
    private BillingCustomerBinding() { }
    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }
    public string ProviderAccount { get; private set; } = "";
    public bool LiveMode { get; private set; }
    public string CustomerId { get; private set; } = "";
    public DateTimeOffset BoundAt { get; private set; }
    public static BillingCustomerBinding Create(Guid accountId, string providerAccount, bool liveMode, string customerId, DateTimeOffset now)
    {
        if (accountId == Guid.Empty || !PaymentReferences.Valid(providerAccount, "acct_") || !PaymentReferences.Valid(customerId, "cus_"))
            throw new ArgumentException("A rent account and valid provider customer scope are required.");
        return new() { Id = Guid.NewGuid(), AccountId = accountId, ProviderAccount = providerAccount, LiveMode = liveMode, CustomerId = customerId, BoundAt = now.ToUniversalTime() };
    }
}

public sealed class PaymentAttempt
{
    private readonly List<PaymentAttemptItem> _items = [];
    private PaymentAttempt() { }
    public Guid Id { get; private set; }
    public Guid OperationId { get; private set; }
    public Guid AccountId { get; private set; }
    public string ActorId { get; private set; } = "";
    public Guid CustomerBindingId { get; private set; }
    public string ProviderAccount { get; private set; } = "";
    public bool LiveMode { get; private set; }
    public string CustomerId { get; private set; } = "";
    public long TotalMinorUnits { get; private set; }
    public string Currency { get; private set; } = "USD";
    public string PayloadHash { get; private set; } = "";
    public PaymentAttemptState State { get; private set; }
    public string? SessionId { get; private set; }
    public string? PaymentId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }
    public Guid Version { get; private set; }
    public IReadOnlyList<PaymentAttemptItem> Items => _items.AsReadOnly();
    public string ProviderIdempotencyKey => $"office:{(LiveMode ? "live" : "test")}:{Id:N}:checkout:v1";

    public static PaymentAttempt Admit(Guid operationId, string actorId, BillingCustomerBinding binding, IEnumerable<ReviewedCollectionBalance> balances, DateTimeOffset now)
    {
        if (operationId == Guid.Empty || string.IsNullOrWhiteSpace(actorId) || actorId.Length > 450 || binding is null || balances is null)
            throw new ArgumentException("An original operation, payer and customer binding are required.");
        // Limit materialization as well as the final item list; callers cannot supply an unbounded review.
        var selected = balances.Take(26).ToArray();
        if (selected.Any(x => x is null)) throw new ArgumentException("Every reviewed item is required.");
        var items = selected.OrderBy(x => x.ObligationId.ToString("D"), StringComparer.Ordinal).ToArray();
        if (items.Length is < 1 or > 25 || items.Select(x => x.ObligationId).Distinct().Count() != items.Length ||
            items.Any(x => x.ObligationId == Guid.Empty || x.Version == Guid.Empty || x.AccountId != binding.AccountId || x.Currency != "USD" ||
                x.RemainingMinorUnits <= 0 || x.HasProviderInstruction || x.Held) || items.Count(x => x.RemainingMinorUnits >= 50) > 1)
            throw new ArgumentException("Checkout requires one full balance plus eligible small remainders in the same account, with no conflicting provider instruction or hold.");
        var total = items.Aggregate(0L, (sum, item) => checked(sum + item.RemainingMinorUnits));
        BillingRules.IssuedAmount(new Money(total));
        var attempt = new PaymentAttempt
        {
            Id = Guid.NewGuid(),
            OperationId = operationId,
            ActorId = actorId,
            AccountId = binding.AccountId,
            CustomerBindingId = binding.Id,
            ProviderAccount = binding.ProviderAccount,
            LiveMode = binding.LiveMode,
            CustomerId = binding.CustomerId,
            TotalMinorUnits = total,
            CreatedAt = now.ToUniversalTime(),
            Version = Guid.NewGuid(),
            State = PaymentAttemptState.Admitted
        };
        foreach (var item in items) attempt._items.Add(PaymentAttemptItem.Snapshot(attempt.Id, item));
        attempt.PayloadHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            operationId,
            actorId,
            binding.AccountId,
            binding.ProviderAccount,
            binding.LiveMode,
            binding.CustomerId,
            currency = attempt.Currency,
            total,
            items = attempt.Items.Select(x => new { x.ObligationId, x.ReviewedVersion, x.AmountMinorUnits })
        })));
        return attempt;
    }

    public void BeginDispatch(DateTimeOffset now)
    {
        Require(PaymentAttemptState.Admitted);
        // A conservative window beneath the provider's minimum 24-hour retention.
        // Older operations require canonical investigation, never a fresh economic command.
        if (now < CreatedAt || now >= CreatedAt.AddHours(23)) throw new InvalidOperationException("The original provider operation needs reconciliation before dispatch.");
        Set(PaymentAttemptState.Dispatching);
    }
    public void AttachHandoff(string sessionId, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        if (State is not (PaymentAttemptState.Dispatching or PaymentAttemptState.NeedsReview) ||
            !PaymentReferences.Valid(sessionId, "cs_") || expiresAt <= now || (SessionId is not null && SessionId != sessionId))
            throw new InvalidOperationException("Only the original verified session can be attached.");
        SessionId = sessionId; ExpiresAt = expiresAt.ToUniversalTime(); Set(PaymentAttemptState.HandoffReady);
    }
    public void ObserveProcessing(string sessionId)
    {
        if (State is not (PaymentAttemptState.HandoffReady or PaymentAttemptState.Processing or PaymentAttemptState.NeedsReview) || SessionId != sessionId || SessionId is null)
            throw new InvalidOperationException("Processing requires the original canonical session.");
        Set(PaymentAttemptState.Processing);
    }
    public void RequireReview()
    {
        if (State is PaymentAttemptState.Settled or PaymentAttemptState.Uncollected) throw new InvalidOperationException("Terminal collection history cannot be replaced.");
        Set(PaymentAttemptState.NeedsReview);
    }
    public void CancelBeforeDispatch() { Require(PaymentAttemptState.Admitted); Set(PaymentAttemptState.Uncollected); }

    // The gateway/reconciliation coordinator must verify canonical evidence before calling these methods.
    // Receipt allocation and guard release belong in the same transaction as this state change.
    public void ApplyVerifiedSettlement(CanonicalSettlement fact)
    {
        if (fact is null || fact.ProviderAccount != ProviderAccount || fact.LiveMode != LiveMode || fact.CustomerId != CustomerId ||
            !PaymentReferences.Valid(fact.SessionId, "cs_") || (SessionId is not null && SessionId != fact.SessionId) ||
            !PaymentReferences.Valid(fact.PaymentId, "pi_") || fact.AmountMinorUnits != TotalMinorUnits || fact.Currency != Currency ||
            State is PaymentAttemptState.Admitted or PaymentAttemptState.Uncollected || (PaymentId is not null && PaymentId != fact.PaymentId))
            throw new InvalidOperationException("Settlement does not match the original attempt and reviewed items.");
        if (State == PaymentAttemptState.Settled) return;
        SessionId = fact.SessionId; PaymentId = fact.PaymentId; Set(PaymentAttemptState.Settled);
    }
    public void ConfirmVerifiedUncollected(string providerAccount, bool liveMode, string customerId, string sessionId)
    {
        if (State is not (PaymentAttemptState.HandoffReady or PaymentAttemptState.Processing or PaymentAttemptState.NeedsReview) ||
            providerAccount != ProviderAccount || liveMode != LiveMode || customerId != CustomerId ||
            !PaymentReferences.Valid(sessionId, "cs_") || (SessionId is not null && sessionId != SessionId))
            throw new InvalidOperationException("Releasing a collection requires verified original provider state.");
        SessionId = sessionId; Set(PaymentAttemptState.Uncollected);
    }
    private void Require(PaymentAttemptState state) { if (State != state) throw new InvalidOperationException("The attempt state changed."); }
    private void Set(PaymentAttemptState state) { State = state; Version = Guid.NewGuid(); }
}

public sealed class PaymentAttemptItem
{
    private PaymentAttemptItem() { }
    public Guid AttemptId { get; private set; }
    public Guid AccountId { get; private set; }
    public Guid ObligationId { get; private set; }
    public Guid ReviewedVersion { get; private set; }
    public long AmountMinorUnits { get; private set; }
    internal static PaymentAttemptItem Snapshot(Guid attemptId, ReviewedCollectionBalance item) => new()
    { AttemptId = attemptId, AccountId = item.AccountId, ObligationId = item.ObligationId, ReviewedVersion = item.Version, AmountMinorUnits = item.RemainingMinorUnits };
}

// One persisted current claim per obligation, shared by every manual/automatic coordinator.
public sealed class CollectionGuard
{
    public Guid ObligationId { get; set; }
    public Guid AccountId { get; set; }
    public Guid AttemptId { get; set; }
}
