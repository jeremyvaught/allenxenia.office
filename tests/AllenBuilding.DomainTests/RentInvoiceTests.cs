using AllenBuilding.Domain.Billing;

namespace AllenBuilding.DomainTests;

public sealed class RentInvoiceTests
{
    [Theory]
    [InlineData(2026, 11, 30, 2026, 12, 31)]
    [InlineData(2028, 2, 29, 2028, 3, 31)]
    [InlineData(2026, 12, 31, 2027, 1, 31)]
    public void Provider_due_day_intervals_reuse_whole_calendar_months(int year, int month, int day, int nextYear, int nextMonth, int nextDay)
    {
        var (auth, terms, now) = Fixture(year, month);
        var fact = Fact(auth, new(year, month, day, 17, 0, 0, TimeSpan.Zero), new(nextYear, nextMonth, nextDay, 17, 0, 0, TimeSpan.Zero), now);
        var calendar = ProviderRentInvoice.Calendar(auth, fact); Assert.Equal(new DateOnly(year, month, 1), calendar.StartsOn); Assert.Equal(calendar.StartsOn.AddMonths(1), calendar.EndsOn);
        var period = BillingPeriod.Create(calendar); var debt = Obligation.Rent(period, terms, calendar, "payer", now);
        var map = ProviderRentInvoice.Map(auth, fact, period, debt); Assert.Equal(debt.Id, map.ObligationId); Assert.Equal(period.Id, map.PeriodId);
        Assert.NotEqual(calendar.EndsOn, DateOnly.FromDateTime(map.LineEndsAt.Date));
        var version = map.Version; map.Observe(auth, fact); Assert.Equal(version, map.Version);
        map.Observe(auth, fact with { State = RentInvoiceState.Paid, RemainingMinorUnits = 0, AutoAdvance = false, CheckedAt = now.AddSeconds(1) });
        Assert.Equal(100000, debt.AmountMinorUnits); Assert.Equal(100000, map.OriginalMinorUnits); // Paid is provider status, never a local cash allocation.
        Assert.Throws<InvalidOperationException>(() => map.Observe(auth, fact with { CheckedAt = now.AddSeconds(2) }));
    }
    [Theory]
    [InlineData("account")]
    [InlineData("customer")]
    [InlineData("mode")]
    [InlineData("consent")]
    [InlineData("subscription")]
    [InlineData("line")]
    [InlineData("credit")]
    [InlineData("proration")]
    [InlineData("anchor")]
    [InlineData("current")]
    [InlineData("paid-remainder")]
    public void Unproved_scope_adjustments_and_shifted_periods_cannot_create_rent(string mismatch)
    {
        var (auth, _, now) = Fixture(2026, 11); var fact = Fact(auth, new(2026, 11, 30, 17, 0, 0, TimeSpan.Zero), new(2026, 12, 31, 17, 0, 0, TimeSpan.Zero), now);
        fact = mismatch switch
        {
            "account" => fact with { AccountId = Guid.NewGuid() },
            "customer" => fact with { CustomerId = "cus_foreign" },
            "mode" => fact with { LiveMode = true },
            "consent" => fact with { ConsentHash = new string('B', 64) },
            "subscription" => fact with { SubscriptionId = "sub_other" },
            "line" => fact with { LineId = "ii_wrong" },
            "credit" => fact with { TotalMinorUnits = 80000 },
            "proration" => fact with { LineEndsAt = fact.LineEndsAt.AddDays(-1) },
            "anchor" => fact with { LineStartsAt = fact.LineStartsAt.AddHours(1) },
            "current" => fact with { LineStartsAt = new(2026, 10, 31, 17, 0, 0, TimeSpan.Zero) },
            _ => fact with { State = RentInvoiceState.Paid }
        };
        Assert.Throws<InvalidOperationException>(() => ProviderRentInvoice.Calendar(auth, fact));
    }
    internal static (AutopayAuthorization, RentAgreement, DateTimeOffset) Fixture(int year, int month)
    {
        var now = new DateTimeOffset(year, month, 1, 12, 0, 0, TimeSpan.Zero).AddMonths(-1); var account = RentAccount.Open(Guid.NewGuid(), Guid.NewGuid());
        var terms = RentAgreement.Create(account.Id, new Money(100000), 31, new DateOnly(year - 1, 1, 1), null, "payer", now);
        var binding = BillingCustomerBinding.Create(account.Id, "acct_invoice", false, "cus_invoice", now);
        var auth = AutopayAuthorization.Consent(Guid.NewGuid(), "payer", account, binding, AutopaySchedule.Select(terms, now, []), true, now);
        auth.ApplyVerifiedMethod(binding.ProviderAccount, false, binding.CustomerId, "pm_original", auth.ConsentHash);
        auth.ApplyVerifiedSubscription(new(binding.ProviderAccount, false, account.Id, binding.CustomerId, "sub_original", "pm_original", auth.ConsentHash, 100000, "USD", 31, 17, auth.FirstAnchorUtc, true, now));
        return (auth, terms, now);
    }
    internal static CanonicalRentInvoice Fact(AutopayAuthorization auth, DateTimeOffset start, DateTimeOffset end, DateTimeOffset now) =>
        new(auth.ProviderAccount, false, auth.AccountId, auth.Id, auth.CustomerId, auth.SubscriptionId!, auth.ConsentHash, "in_original", "il_original", start, end, 100000, 100000, 100000, "USD", RentInvoiceState.Open, true, now, now);
}
