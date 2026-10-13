namespace AllenBuilding.Domain.Billing;

// Immutable read context from canonical capture evidence. Constructing it never settles or changes a leased attempt.
public sealed class CapturedPaymentEvidence
{
    private CapturedPaymentEvidence() { }
    public Guid Id { get; private init; }
    public Guid AccountId { get; private init; }
    public string ProviderAccount { get; private init; } = "";
    public bool LiveMode { get; private init; }
    public string CustomerId { get; private init; } = "";
    public string PaymentId { get; private init; } = "";
    public string PayloadHash { get; private init; } = "";
    public long TotalMinorUnits { get; private init; }
    public DateTimeOffset CreatedAt { get; private init; }
    public static CapturedPaymentEvidence FromSettled(PaymentAttempt original)
    {
        if (original is null || original.State != PaymentAttemptState.Settled) throw new ArgumentException("An original captured payment is required.");
        return FromSettlement(original, new(original.ProviderAccount, original.LiveMode, original.CustomerId, original.SessionId!, original.PaymentId!, original.TotalMinorUnits, original.Currency));
    }
    public static CapturedPaymentEvidence FromSettlement(PaymentAttempt original, CanonicalSettlement settlement)
    {
        if (original is null || settlement is null || original.State is PaymentAttemptState.Admitted or PaymentAttemptState.Uncollected || original.Id == Guid.Empty || original.AccountId == Guid.Empty ||
            settlement.ProviderAccount != original.ProviderAccount || settlement.LiveMode != original.LiveMode || settlement.CustomerId != original.CustomerId || settlement.Currency != "USD" || settlement.Currency != original.Currency ||
            settlement.AmountMinorUnits != original.TotalMinorUnits || settlement.AmountMinorUnits is < 50 or > 99_999_999 || !PaymentReferences.Valid(settlement.SessionId, "cs_") ||
            (original.SessionId is not null && original.SessionId != settlement.SessionId) || !PaymentReferences.Valid(settlement.PaymentId, "pi_") ||
            (original.PaymentId is not null && original.PaymentId != settlement.PaymentId) || original.PayloadHash is not { Length: 64 } || !original.PayloadHash.All(Uri.IsHexDigit))
            throw new ArgumentException("Capture evidence must match the dispatched original amount, scope, references and reviewed payload.");
        return new()
        {
            Id = original.Id,
            AccountId = original.AccountId,
            ProviderAccount = original.ProviderAccount,
            LiveMode = original.LiveMode,
            CustomerId = original.CustomerId,
            PaymentId = settlement.PaymentId,
            PayloadHash = original.PayloadHash,
            TotalMinorUnits = original.TotalMinorUnits,
            CreatedAt = original.CreatedAt
        };
    }
}
