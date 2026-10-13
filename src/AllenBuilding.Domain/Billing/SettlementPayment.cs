namespace AllenBuilding.Domain.Billing;

// Append-only cash capture evidence. Refunds/disputes require their own later economic entries.
public sealed class SettlementPayment
{
    private readonly List<PaymentAllocation> _allocations = [];
    private SettlementPayment() { }
    public Guid Id { get; private set; }
    public Guid AttemptId { get; private set; }
    public Guid AccountId { get; private set; }
    public string ProviderAccount { get; private set; } = "";
    public bool LiveMode { get; private set; }
    public string CollectionReference { get; private set; } = "";
    public long AmountMinorUnits { get; private set; }
    public string Currency { get; private set; } = "USD";
    public DateTimeOffset RecordedAt { get; private set; }
    public IReadOnlyList<PaymentAllocation> Allocations => _allocations.AsReadOnly();

    public static SettlementPayment Record(PaymentAttempt attempt, CanonicalSettlement verified, DateTimeOffset recordedAt)
    {
        if (attempt is null) throw new ArgumentException("The original payment attempt is required.");
        if (attempt.Items.Count == 0 || attempt.Items.Any(x => x.AccountId != attempt.AccountId || x.AttemptId != attempt.Id || x.AmountMinorUnits <= 0) ||
            attempt.Items.Aggregate(0L, (sum, item) => checked(sum + item.AmountMinorUnits)) != attempt.TotalMinorUnits)
            throw new InvalidOperationException("Load and verify the complete immutable item set before recording cash.");
        attempt.ApplyVerifiedSettlement(verified);
        var payment = new SettlementPayment
        {
            Id = Guid.NewGuid(),
            AttemptId = attempt.Id,
            AccountId = attempt.AccountId,
            ProviderAccount = attempt.ProviderAccount,
            LiveMode = attempt.LiveMode,
            CollectionReference = attempt.PaymentId!,
            AmountMinorUnits = attempt.TotalMinorUnits,
            Currency = attempt.Currency,
            RecordedAt = recordedAt.ToUniversalTime()
        };
        foreach (var item in attempt.Items) payment._allocations.Add(PaymentAllocation.Create(payment.Id, item));
        if (payment._allocations.Count == 0 || payment._allocations.Aggregate(0L, (sum, item) => checked(sum + item.AmountMinorUnits)) != payment.AmountMinorUnits)
            throw new InvalidOperationException("The recorded collection must exactly reconcile to its reviewed allocations.");
        return payment;
    }
}

public sealed class PaymentAllocation
{
    private PaymentAllocation() { }
    public Guid PaymentId { get; private set; }
    public Guid AccountId { get; private set; }
    public Guid AttemptId { get; private set; }
    public Guid ObligationId { get; private set; }
    public long AmountMinorUnits { get; private set; }
    internal static PaymentAllocation Create(Guid paymentId, PaymentAttemptItem item) => new()
    { PaymentId = paymentId, AccountId = item.AccountId, AttemptId = item.AttemptId, ObligationId = item.ObligationId, AmountMinorUnits = item.AmountMinorUnits };
}
