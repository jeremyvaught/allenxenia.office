namespace AllenBuilding.Domain.Billing;

public enum RentInvoiceState { Draft, Open, Paid, Void, Uncollectible }
public sealed record CanonicalRentInvoice(string ProviderAccount, bool LiveMode, Guid AccountId, Guid AuthorizationId,
    string CustomerId, string SubscriptionId, string ConsentHash, string InvoiceId, string LineId,
    DateTimeOffset LineStartsAt, DateTimeOffset LineEndsAt, long LineMinorUnits, long TotalMinorUnits,
    long RemainingMinorUnits, string Currency, RentInvoiceState State, bool AutoAdvance, DateTimeOffset CreatedAt, DateTimeOffset CheckedAt);

// Immutable economic lineage, distinct from cash. Even Paid may mean a balance transfer or an
// out-of-band mark. Keep the original invoice path until a separate canonical cash/disposition proof.
public sealed class ProviderRentInvoice
{
    private ProviderRentInvoice() { }
    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }
    public Guid AuthorizationId { get; private set; }
    public Guid PeriodId { get; private set; }
    public Guid ObligationId { get; private set; }
    public string ProviderAccount { get; private set; } = "";
    public bool LiveMode { get; private set; }
    public string CustomerId { get; private set; } = "";
    public string SubscriptionId { get; private set; } = "";
    public string InvoiceId { get; private set; } = "";
    public string LineId { get; private set; } = "";
    public string ConsentHash { get; private set; } = "";
    public DateTimeOffset LineStartsAt { get; private set; }
    public DateTimeOffset LineEndsAt { get; private set; }
    public long OriginalMinorUnits { get; private set; }
    public long RemainingMinorUnits { get; private set; }
    public string Currency { get; private set; } = "USD";
    public RentInvoiceState State { get; private set; }
    public bool AutoAdvance { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset CheckedAt { get; private set; }
    public Guid Version { get; private set; }

    public static RentPeriod Calendar(AutopayAuthorization authorization, CanonicalRentInvoice fact)
    {
        if (authorization is null || fact is null || authorization.SubscriptionId is null ||
            fact.AuthorizationId != authorization.Id || fact.AccountId != authorization.AccountId || fact.ProviderAccount != authorization.ProviderAccount ||
            fact.LiveMode != authorization.LiveMode || fact.CustomerId != authorization.CustomerId || fact.SubscriptionId != authorization.SubscriptionId ||
            fact.ConsentHash != authorization.ConsentHash || !PaymentReferences.Valid(fact.InvoiceId, "in_") || !PaymentReferences.Valid(fact.LineId, "il_") ||
            !Enum.IsDefined(fact.State) || fact.Currency != authorization.Currency || fact.LineMinorUnits != authorization.MonthlyMinorUnits ||
            fact.TotalMinorUnits != fact.LineMinorUnits || fact.RemainingMinorUnits < 0 || fact.RemainingMinorUnits > fact.TotalMinorUnits ||
            fact.CreatedAt < authorization.ConsentedAt || fact.CheckedAt < fact.CreatedAt || fact.CheckedAt < authorization.ConsentedAt ||
            fact.LineStartsAt.Offset != TimeSpan.Zero || fact.LineEndsAt.Offset != TimeSpan.Zero || fact.LineEndsAt <= fact.LineStartsAt ||
            (fact.State is RentInvoiceState.Paid or RentInvoiceState.Void && fact.RemainingMinorUnits != 0))
            throw new InvalidOperationException("The invoice must prove one unchanged monthly line for the original customer, subscription and consent.");
        // Stripe renewal intervals follow the due-day anchor; local debt remains a whole calendar month.
        var start = new DateOnly(fact.LineStartsAt.Year, fact.LineStartsAt.Month, 1);
        if (start < authorization.FirstPeriodStartsOn || start >= authorization.EndsBefore) throw new InvalidOperationException("The period is outside the original authorization.");
        var period = RentCalendar.ForMonth(authorization.AccountId, start.Year, start.Month, authorization.DueDay);
        DateTimeOffset Anchor(DateOnly month)
        {
            var due = RentCalendar.ForMonth(authorization.AccountId, month.Year, month.Month, authorization.DueDay).DueOn;
            return new(due.Year, due.Month, due.Day, AutopaySchedule.ProviderHourUtc, 0, 0, TimeSpan.Zero);
        }
        if (fact.LineStartsAt != Anchor(start) || fact.LineEndsAt != Anchor(start.AddMonths(1)))
            throw new InvalidOperationException("A prorated, partial or shifted interval cannot create calendar rent.");
        return period;
    }
    public static ProviderRentInvoice Map(AutopayAuthorization authorization, CanonicalRentInvoice fact, BillingPeriod period, Obligation debt)
    {
        var calendar = Calendar(authorization, fact);
        if (period.AccountId != calendar.AccountId || period.StartsOn != calendar.StartsOn || period.EndsOn != calendar.EndsOn || period.Kind != ChargeKind.BaseRent ||
            debt.AccountId != calendar.AccountId || debt.PeriodId != period.Id || debt.Kind != ChargeKind.BaseRent || debt.AgreementId != authorization.AgreementId ||
            debt.AmountMinorUnits != fact.LineMinorUnits || debt.Currency != fact.Currency || debt.DueOn != calendar.DueOn)
            throw new InvalidOperationException("The original invoice must reuse its exact calendar obligation.");
        return new()
        {
            Id = Guid.NewGuid(),
            AccountId = authorization.AccountId,
            AuthorizationId = authorization.Id,
            PeriodId = period.Id,
            ObligationId = debt.Id,
            ProviderAccount = fact.ProviderAccount,
            LiveMode = fact.LiveMode,
            CustomerId = fact.CustomerId,
            SubscriptionId = fact.SubscriptionId,
            InvoiceId = fact.InvoiceId,
            LineId = fact.LineId,
            ConsentHash = fact.ConsentHash,
            LineStartsAt = fact.LineStartsAt,
            LineEndsAt = fact.LineEndsAt,
            OriginalMinorUnits = fact.LineMinorUnits,
            RemainingMinorUnits = fact.RemainingMinorUnits,
            Currency = fact.Currency,
            State = fact.State,
            AutoAdvance = fact.AutoAdvance,
            CreatedAt = fact.CreatedAt,
            CheckedAt = fact.CheckedAt,
            Version = Guid.NewGuid()
        };
    }
    public void Observe(AutopayAuthorization authorization, CanonicalRentInvoice fact)
    {
        Calendar(authorization, fact);
        if (AuthorizationId != fact.AuthorizationId || InvoiceId != fact.InvoiceId || LineId != fact.LineId || ConsentHash != fact.ConsentHash ||
            LineStartsAt != fact.LineStartsAt || LineEndsAt != fact.LineEndsAt || OriginalMinorUnits != fact.LineMinorUnits || CreatedAt != fact.CreatedAt ||
            fact.CheckedAt < CheckedAt || State is RentInvoiceState.Paid or RentInvoiceState.Void && State != fact.State)
            throw new InvalidOperationException("An invoice observation cannot replace original lineage or regress terminal evidence.");
        if (State == fact.State && RemainingMinorUnits == fact.RemainingMinorUnits && AutoAdvance == fact.AutoAdvance && CheckedAt == fact.CheckedAt) return;
        if (fact.CheckedAt == CheckedAt) throw new InvalidOperationException("Conflicting observations require a fresh canonical read.");
        State = fact.State; RemainingMinorUnits = fact.RemainingMinorUnits; AutoAdvance = fact.AutoAdvance; CheckedAt = fact.CheckedAt; Version = Guid.NewGuid();
    }
}
