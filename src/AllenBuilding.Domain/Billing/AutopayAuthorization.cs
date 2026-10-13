using System.Security.Cryptography;
using System.Text.Json;

namespace AllenBuilding.Domain.Billing;

public enum AutopayState { AwaitingMethod, AwaitingProvider, Active, StopPending, Stopped, NeedsReview }
public enum AutopayStopTiming { Immediately, EndOfCurrentRentMonth }

public sealed record AutopaySchedule(Guid AccountId, Guid AgreementId, Guid AgreementVersion, long AmountMinorUnits, string Currency,
    int DueDay, DateOnly FirstPeriodStartsOn, DateOnly FirstDueOn, DateTimeOffset FirstAnchorUtc, DateOnly? EndsBefore = null)
{
    public const string TimeZone = "America/New_York";
    // A fixed 17:00 UTC is noon/13:00 in Xenia. Its local DATE stays correct through DST.
    // Provider renewal config must retain the ORIGINAL due day, including 31 when the first month clamps.
    public const int ProviderHourUtc = 17;
    public static AutopaySchedule Select(RentAgreement terms, DateTimeOffset now, IReadOnlyCollection<DateOnly> manualPeriods)
    {
        if (terms is null || now == default || terms.SupersededBy is not null || manualPeriods is null || manualPeriods.Count > 25 ||
            manualPeriods.Any(x => x == default || x.Day != 1) || manualPeriods.Distinct().Count() != manualPeriods.Count)
            throw new ArgumentException("Autopay requires current terms and a bounded, unique set of already-paid, adjusted or instructed calendar periods.");
        var local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(TimeZone));
        var first = new DateOnly(local.Year, local.Month, 1).AddMonths(1);
        // No automatic partial month or current-period collection at enrollment.
        var termMonth = new DateOnly(terms.StartsOn.Year, terms.StartsOn.Month, 1);
        if (terms.StartsOn.Day != 1) termMonth = termMonth.AddMonths(1);
        if (first < termMonth) first = termMonth;
        // Only 25 reviewed exclusions are accepted; every selected period still needs atomic admission.
        for (var i = 0; i <= 25; i++, first = first.AddMonths(1))
        {
            var period = RentCalendar.ForMonth(terms.AccountId, first.Year, first.Month, terms.DueDay);
            if (!terms.Covers(period)) throw new InvalidOperationException("No future whole rent period is covered by these terms.");
            if (manualPeriods.Contains(first)) continue;
            return new(terms.AccountId, terms.Id, terms.Version, terms.AmountMinorUnits, terms.Currency, terms.DueDay, first, period.DueOn,
                new DateTimeOffset(period.DueOn.Year, period.DueOn.Month, period.DueOn.Day, ProviderHourUtc, 0, 0, TimeSpan.Zero),
                terms.EndsOn is { } end ? new DateOnly(end.Year, end.Month, 1) : null);
        }
        throw new InvalidOperationException("No bounded eligible first period could be selected.");
    }
    public DateOnly DueFor(DateOnly periodStartsOn)
    {
        if (periodStartsOn.Day != 1 || periodStartsOn < FirstPeriodStartsOn || periodStartsOn >= EndsBefore) throw new ArgumentException("The period is outside this authorization.");
        return RentCalendar.ForMonth(AccountId, periodStartsOn.Year, periodStartsOn.Month, DueDay).DueOn;
    }
    public DateTimeOffset? AutomaticCollectionEndsAt => EndsBefore is { } end ? LocalMidnightUtc(end) : null;
    public static DateTimeOffset LocalMidnightUtc(DateOnly date) => new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), TimeZoneInfo.FindSystemTimeZoneById(TimeZone)));
    public static DateTimeOffset StopAt(AutopayStopTiming timing, DateTimeOffset now)
    {
        if (!Enum.IsDefined(timing) || now == default) throw new ArgumentException("An explicit supported stop timing is required.");
        if (timing == AutopayStopTiming.Immediately) return now.ToUniversalTime();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZone); var local = TimeZoneInfo.ConvertTime(now, zone);
        return LocalMidnightUtc(new DateOnly(local.Year, local.Month, 1).AddMonths(1));
    }
}

public sealed record VerifiedAutopaySubscription(string ProviderAccount, bool LiveMode, Guid AccountId, string CustomerId, string SubscriptionId,
    string PaymentMethodId, string ConsentHash, long MonthlyMinorUnits, string Currency, int DueDay, int HourUtc,
    DateTimeOffset FirstAnchorUtc, bool InitialPeriodNotCharged, DateTimeOffset CheckedAt, DateTimeOffset? AutomaticCollectionEndsAt = null);
public sealed record VerifiedAutopayStop(string ProviderAccount, bool LiveMode, Guid AccountId, string CustomerId, string? SubscriptionId,
    bool NoFurtherAutomaticInstructions, bool UnpaidInvoicesRetainedForManualPayment, DateTimeOffset CheckedAt);

// This authorization does not create rent, settle money, or assert provider state from a redirect.
// Coordinators also recheck actor, versions, original period identities and provider instructions under locks.
public sealed class AutopayAuthorization
{
    private AutopayAuthorization() { }
    public Guid Id { get; private set; }
    public Guid OperationId { get; private set; }
    public Guid AccountId { get; private set; }
    public Guid ReviewedAccountVersion { get; private set; }
    public Guid AgreementId { get; private set; }
    public Guid AgreementVersion { get; private set; }
    public string ActorId { get; private set; } = "";
    public string ProviderAccount { get; private set; } = "";
    public bool LiveMode { get; private set; }
    public string CustomerId { get; private set; } = "";
    public long MonthlyMinorUnits { get; private set; }
    public string Currency { get; private set; } = "USD";
    public int DueDay { get; private set; }
    public DateOnly FirstPeriodStartsOn { get; private set; }
    public DateOnly FirstDueOn { get; private set; }
    public DateTimeOffset FirstAnchorUtc { get; private set; }
    public DateOnly? EndsBefore { get; private set; }
    public DateTimeOffset ConsentedAt { get; private set; }
    public string ConsentHash { get; private set; } = "";
    public string? PaymentMethodId { get; private set; }
    public string? SubscriptionId { get; private set; }
    public DateTimeOffset? LastVerifiedAt { get; private set; }
    public Guid? StopOperationId { get; private set; }
    public string? StopActorId { get; private set; }
    public DateTimeOffset? StopRequestedAt { get; private set; }
    public AutopayStopTiming? StopTiming { get; private set; }
    public DateTimeOffset? StopEffectiveAt { get; private set; }
    public AutopayState State { get; private set; }
    public Guid Version { get; private set; }
    public bool AdmitsNewAutomaticWork => State == AutopayState.Active && StopOperationId is null;
    public bool CanAdmitPeriod(DateOnly period, Guid currentAgreementVersion, DateTimeOffset now) => AdmitsNewAutomaticWork && now >= ConsentedAt &&
        currentAgreementVersion == AgreementVersion && period.Day == 1 && period >= FirstPeriodStartsOn && !(period >= EndsBefore) &&
        (EndsBefore is null || now < AutopaySchedule.LocalMidnightUtc(EndsBefore.Value));
    public string SetupOperationKey => $"office:{(LiveMode ? "live" : "test")}:{Id:N}:autopay:setup:v1";
    public string SubscriptionOperationKey => $"office:{(LiveMode ? "live" : "test")}:{Id:N}:autopay:subscription:v1";
    public string? StopOperationKey => StopOperationId is { } operation ? $"office:{(LiveMode ? "live" : "test")}:{Id:N}:autopay:stop:{operation:N}:v1" : null;

    public static AutopayAuthorization Consent(Guid operation, string actor, RentAccount account, BillingCustomerBinding binding,
        AutopaySchedule schedule, bool explicitlyAccepted, DateTimeOffset now)
    {
        if (!explicitlyAccepted || operation == Guid.Empty || string.IsNullOrWhiteSpace(actor) || actor.Length > 450 || account is null || account.EndedAt is not null ||
            binding is null || schedule is null || binding.AccountId != account.Id || schedule.AccountId != account.Id || schedule.AgreementId == Guid.Empty || schedule.AgreementVersion == Guid.Empty ||
            schedule.Currency != "USD" || schedule.FirstPeriodStartsOn == default || schedule.FirstPeriodStartsOn.Day != 1 || now == default || schedule.FirstAnchorUtc <= now || schedule.FirstAnchorUtc.Offset != TimeSpan.Zero ||
            schedule.FirstDueOn != RentCalendar.ForMonth(account.Id, schedule.FirstPeriodStartsOn.Year, schedule.FirstPeriodStartsOn.Month, schedule.DueDay).DueOn ||
            schedule.FirstAnchorUtc != new DateTimeOffset(schedule.FirstDueOn.Year, schedule.FirstDueOn.Month, schedule.FirstDueOn.Day, AutopaySchedule.ProviderHourUtc, 0, 0, TimeSpan.Zero))
            throw new ArgumentException("Explicit consent must match the exact reviewed account, dedicated customer, terms and future calendar schedule.");
        var local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(AutopaySchedule.TimeZone));
        if (schedule.FirstPeriodStartsOn <= new DateOnly(local.Year, local.Month, 1) || schedule.EndsBefore is { } end && (end.Day != 1 || end < schedule.FirstPeriodStartsOn.AddMonths(1)))
            throw new ArgumentException("Consent cannot authorize the current/partial month or a period beyond the frozen agreement end.");
        BillingRules.IssuedAmount(new Money(schedule.AmountMinorUnits));
        var result = new AutopayAuthorization
        {
            Id = Guid.NewGuid(),
            OperationId = operation,
            AccountId = account.Id,
            ReviewedAccountVersion = account.Version,
            ActorId = actor,
            AgreementId = schedule.AgreementId,
            AgreementVersion = schedule.AgreementVersion,
            ProviderAccount = binding.ProviderAccount,
            LiveMode = binding.LiveMode,
            CustomerId = binding.CustomerId,
            MonthlyMinorUnits = schedule.AmountMinorUnits,
            DueDay = schedule.DueDay,
            FirstPeriodStartsOn = schedule.FirstPeriodStartsOn,
            FirstDueOn = schedule.FirstDueOn,
            FirstAnchorUtc = schedule.FirstAnchorUtc,
            EndsBefore = schedule.EndsBefore,
            ConsentedAt = now.ToUniversalTime(),
            State = AutopayState.AwaitingMethod,
            Version = Guid.NewGuid()
        };
        result.ConsentHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            AuthorizationId = result.Id,
            operation,
            actor,
            AccountId = account.Id,
            account.Version,
            binding.ProviderAccount,
            binding.LiveMode,
            binding.CustomerId,
            schedule,
            ExplicitConsent = true,
            result.ConsentedAt,
            Contract = "calendar-autopay.v1"
        })));
        return result;
    }
    public void ApplyVerifiedMethod(string providerAccount, bool liveMode, string customerId, string methodId, string consentHash)
    {
        if (State != AutopayState.AwaitingMethod || providerAccount != ProviderAccount || liveMode != LiveMode || customerId != CustomerId ||
            consentHash != ConsentHash || !PaymentReferences.Valid(methodId, "pm_")) throw new InvalidOperationException("The verified setup method must belong to this original customer and consent.");
        PaymentMethodId = methodId; Set(AutopayState.AwaitingProvider);
    }
    public void ApplyVerifiedSubscription(VerifiedAutopaySubscription fact)
    {
        if (State is not (AutopayState.AwaitingProvider or AutopayState.Active) || StopOperationId is not null || !MatchesSubscription(fact))
            throw new InvalidOperationException("A subscription must verify the original scope, method, consent, amount, calendar anchor and uncharged initial period.");
        if (State == AutopayState.Active && LastVerifiedAt == fact.CheckedAt) return;
        SubscriptionId = fact.SubscriptionId; LastVerifiedAt = fact.CheckedAt.ToUniversalTime(); Set(AutopayState.Active);
    }
    public void ObserveOriginalSubscriptionForStop(VerifiedAutopaySubscription fact)
    {
        // A create may have succeeded before its response was lost. Discovering it after a stop never reactivates consent.
        if (State != AutopayState.StopPending || !MatchesSubscription(fact) || fact.CheckedAt < StopRequestedAt)
            throw new InvalidOperationException("Stop reconciliation can attach only the verified original subscription.");
        if (SubscriptionId == fact.SubscriptionId && LastVerifiedAt == fact.CheckedAt) return;
        SubscriptionId = fact.SubscriptionId; LastVerifiedAt = fact.CheckedAt.ToUniversalTime(); Version = Guid.NewGuid();
    }
    private bool MatchesSubscription(VerifiedAutopaySubscription? fact)
    {
        if (fact is null || fact.ProviderAccount != ProviderAccount || fact.LiveMode != LiveMode || fact.AccountId != AccountId || fact.CustomerId != CustomerId ||
            !PaymentReferences.Valid(fact.SubscriptionId, "sub_") || (SubscriptionId is not null && SubscriptionId != fact.SubscriptionId) ||
            PaymentMethodId is null || fact.PaymentMethodId != PaymentMethodId || fact.ConsentHash != ConsentHash || fact.MonthlyMinorUnits != MonthlyMinorUnits ||
            fact.Currency != Currency || fact.DueDay != DueDay || fact.HourUtc != AutopaySchedule.ProviderHourUtc || fact.FirstAnchorUtc != FirstAnchorUtc ||
            fact.AutomaticCollectionEndsAt != (EndsBefore is { } end ? AutopaySchedule.LocalMidnightUtc(end) : (DateTimeOffset?)null) ||
            !fact.InitialPeriodNotCharged || fact.CheckedAt < ConsentedAt || fact.CheckedAt < LastVerifiedAt)
            return false;
        return true;
    }
    public void RequireReview()
    { if (State is AutopayState.Stopped or AutopayState.StopPending) throw new InvalidOperationException("A stopped/pending-stop authorization cannot resume automatically."); Set(AutopayState.NeedsReview); }
    public void RequestStop(Guid operation, string actor, AutopayStopTiming timing, DateTimeOffset now)
    {
        if (operation == Guid.Empty || string.IsNullOrWhiteSpace(actor) || actor.Length > 450 || now < ConsentedAt || !Enum.IsDefined(timing)) throw new ArgumentException("Stopping requires an original reviewed operation, actor and timing.");
        if (State is AutopayState.StopPending or AutopayState.Stopped)
        {
            // Retried original intent preserves its effective date; a later browser time must not shift it.
            if (StopOperationId == operation && StopActorId == actor && StopTiming == timing) return;
            throw new InvalidOperationException("A different stop cannot replace the original pending instruction.");
        }
        StopOperationId = operation; StopActorId = actor; StopRequestedAt = now.ToUniversalTime(); StopTiming = timing; StopEffectiveAt = AutopaySchedule.StopAt(timing, now); Set(AutopayState.StopPending);
    }
    public void VerifyStop(VerifiedAutopayStop fact)
    {
        if (fact is null || State is not (AutopayState.StopPending or AutopayState.Stopped) || StopOperationId is null ||
            fact.ProviderAccount != ProviderAccount || fact.LiveMode != LiveMode || fact.AccountId != AccountId || fact.CustomerId != CustomerId || fact.SubscriptionId != SubscriptionId ||
            !fact.NoFurtherAutomaticInstructions || !fact.UnpaidInvoicesRetainedForManualPayment || (SubscriptionId is not null && fact.CheckedAt < StopEffectiveAt) || fact.CheckedAt < StopRequestedAt || fact.CheckedAt < LastVerifiedAt)
            throw new InvalidOperationException("A stop needs canonical original ownership, collection cutoff and preserved unpaid instructions.");
        if (State == AutopayState.Stopped && LastVerifiedAt == fact.CheckedAt) return;
        LastVerifiedAt = fact.CheckedAt.ToUniversalTime(); Set(AutopayState.Stopped);
    }
    private void Set(AutopayState state) { State = state; Version = Guid.NewGuid(); }
}
