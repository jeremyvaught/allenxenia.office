namespace AllenBuilding.Domain.Billing;

public enum ChargeKind { BaseRent, OneTime }

public static class BillingRules
{
    public static void IssuedAmount(Money amount)
    {
        if (amount.Currency != "USD" || amount.MinorUnits is < 50 or > 99_999_999)
            throw new ArgumentException("Issued amounts must be USD 0.50–999,999.99.");
    }
}

public sealed class RentAccount
{
    private RentAccount() { }
    public Guid Id { get; private set; }
    public Guid MembershipId { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }
    public Guid Version { get; private set; }
    public static RentAccount Open(Guid id, Guid membershipId)
    {
        if (id == Guid.Empty || membershipId == Guid.Empty) throw new ArgumentException("A rent account and original membership are required.");
        return new RentAccount { Id = id, MembershipId = membershipId, Version = Guid.NewGuid() };
    }
    public void EndTenancy(DateTimeOffset now)
    {
        if (EndedAt is not null || now == default) throw new InvalidOperationException("The tenancy has already ended or the end time is invalid.");
        EndedAt = now.ToUniversalTime(); Version = Guid.NewGuid();
    }
    public void TermsChanged() => Version = Guid.NewGuid();
    public void AuthorizationChanged() => Version = Guid.NewGuid();
}

public sealed class RentAgreement
{
    private RentAgreement() { }
    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }
    public long AmountMinorUnits { get; private set; }
    public string Currency { get; private set; } = "USD";
    public int DueDay { get; private set; }
    public DateOnly StartsOn { get; private set; }
    public DateOnly? EndsOn { get; private set; }
    public DateOnly? OriginalEndsOn { get; private set; }
    public Guid? SupersededBy { get; private set; }
    public Guid Version { get; private set; }
    public string CreatedBy { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }

    public static RentAgreement Create(Guid accountId, Money amount, int dueDay, DateOnly startsOn, DateOnly? endsOn, string actor, DateTimeOffset now)
    {
        BillingRules.IssuedAmount(amount);
        if (accountId == Guid.Empty || dueDay is < 1 or > 31 || startsOn == default || (endsOn is { } end && end <= startsOn) ||
            string.IsNullOrWhiteSpace(actor) || actor.Length > 450) throw new ArgumentException("Invalid rent terms.");
        return new RentAgreement
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            AmountMinorUnits = amount.MinorUnits,
            Currency = amount.Currency,
            DueDay = dueDay,
            StartsOn = startsOn,
            EndsOn = endsOn,
            OriginalEndsOn = endsOn,
            Version = Guid.NewGuid(),
            CreatedBy = actor,
            CreatedAt = now.ToUniversalTime()
        };
    }

    public bool Covers(RentPeriod period) => period.AccountId == AccountId && period.StartsOn >= StartsOn &&
        (EndsOn is null || period.EndsOn <= EndsOn);

    public void Supersede(RentAgreement replacement)
    {
        if (replacement.AccountId != AccountId || replacement.StartsOn.Day != 1 || replacement.StartsOn < StartsOn ||
            (EndsOn is { } end && replacement.StartsOn >= end) || SupersededBy is not null)
            throw new InvalidOperationException("These terms cannot replace this agreement.");
        EndsOn = replacement.StartsOn; SupersededBy = replacement.Id; Version = Guid.NewGuid();
    }
}

public sealed class BillingPeriod
{
    private BillingPeriod() { }
    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }
    public DateOnly StartsOn { get; private set; }
    public DateOnly EndsOn { get; private set; }
    public ChargeKind Kind { get; private set; }
    public static BillingPeriod Create(RentPeriod calendar)
    {
        if (calendar.AccountId == Guid.Empty || calendar.StartsOn.Day != 1 || calendar.EndsOn != calendar.StartsOn.AddMonths(1))
            throw new ArgumentException("Base rent uses a whole calendar month.");
        return new BillingPeriod
        {
            Id = Guid.NewGuid(),
            AccountId = calendar.AccountId,
            StartsOn = calendar.StartsOn,
            EndsOn = calendar.EndsOn,
            Kind = ChargeKind.BaseRent
        };
    }
}

public sealed class Obligation
{
    private Obligation() { }
    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }
    public Guid? PeriodId { get; private set; }
    public Guid? AgreementId { get; private set; }
    public ChargeKind Kind { get; private set; }
    public string Description { get; private set; } = "";
    public long AmountMinorUnits { get; private set; }
    public string Currency { get; private set; } = "USD";
    public DateOnly DueOn { get; private set; }
    public Guid Version { get; private set; }
    public string IssuedBy { get; private set; } = "";
    public DateTimeOffset IssuedAt { get; private set; }

    // Coordinators call this in the same transaction as an accepted allocation, adjustment or collection hold.
    public void FinancialStateChanged() => Version = Guid.NewGuid();

    public static Obligation Issue(Guid accountId, Money amount, string description, DateOnly dueOn, string actor, DateTimeOffset now)
    {
        BillingRules.IssuedAmount(amount); description = description?.Trim() ?? "";
        if (accountId == Guid.Empty || description.Length is < 1 or > 120 || dueOn == default || string.IsNullOrWhiteSpace(actor) || actor.Length > 450)
            throw new ArgumentException("Invalid charge.");
        return new Obligation
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Kind = ChargeKind.OneTime,
            Description = description,
            AmountMinorUnits = amount.MinorUnits,
            Currency = amount.Currency,
            DueOn = dueOn,
            Version = Guid.NewGuid(),
            IssuedBy = actor,
            IssuedAt = now.ToUniversalTime()
        };
    }

    public static Obligation Rent(BillingPeriod period, RentAgreement agreement, RentPeriod calendar, string actor, DateTimeOffset now)
    {
        if (period.AccountId != agreement.AccountId || period.AccountId != calendar.AccountId ||
            calendar != RentCalendar.ForMonth(agreement.AccountId, calendar.StartsOn.Year, calendar.StartsOn.Month, agreement.DueDay) || period.StartsOn != calendar.StartsOn || period.EndsOn != calendar.EndsOn || !agreement.Covers(calendar))
            throw new InvalidOperationException("Partial months require an explicit opening or closing charge.");
        var obligation = Issue(period.AccountId, new Money(agreement.AmountMinorUnits, agreement.Currency),
            $"Rent {calendar.StartsOn:yyyy-MM}", calendar.DueOn, actor, now);
        obligation.Kind = ChargeKind.BaseRent; obligation.PeriodId = period.Id; obligation.AgreementId = agreement.Id;
        return obligation;
    }
}

public sealed class BillingReceipt
{
    public string ActorId { get; set; } = "";
    public Guid OperationId { get; set; }
    public string PayloadHash { get; set; } = "";
    public Guid AccountId { get; set; }
    public Guid EntityId { get; set; }
    public Guid AccountVersion { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
