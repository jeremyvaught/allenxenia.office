using AllenBuilding.Domain.Billing;
using AllenBuilding.Domain.Tenancy;

namespace AllenBuilding.DomainTests;

public sealed class TenancyAndBillingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Revocation_is_local_immediate_and_irreversible_for_the_grant()
    {
        var membership = Membership.Grant(Guid.NewGuid(), "tenant", Now.AddDays(-1));
        var originalId = membership.Id;
        Assert.True(membership.IsActive(Now));
        membership.Revoke("manager", "Access ended", Now);
        Assert.False(membership.IsActive(Now));
        Assert.False(membership.IsActive(Now.AddYears(1)));
        Assert.Equal(originalId, membership.Id);
        var replacement = Membership.Grant(membership.SuiteId, "tenant", Now);
        Assert.NotEqual(originalId, replacement.Id);
    }

    [Fact]
    public void Grant_dates_are_start_inclusive_end_exclusive()
    {
        var membership = Membership.Grant(Guid.NewGuid(), "tenant", Now, Now.AddDays(1));
        Assert.False(membership.IsActive(Now.AddTicks(-1)));
        Assert.True(membership.IsActive(Now));
        Assert.False(membership.IsActive(Now.AddDays(1)));
    }

    [Theory]
    [InlineData(2026, 2, 31, 28)]
    [InlineData(2028, 2, 31, 29)]
    [InlineData(2026, 4, 31, 30)]
    [InlineData(2026, 3, 1, 1)]
    public void Manager_due_day_is_clamped_to_calendar_month(int year, int month, int day, int expectedDay)
    {
        var account = Guid.NewGuid();
        var period = RentCalendar.ForMonth(account, year, month, day);
        Assert.Equal(new DateOnly(year, month, expectedDay), period.DueOn);
        Assert.Equal(1, period.StartsOn.Day);
        Assert.Equal(period.StartsOn.AddMonths(1), period.EndsOn);
        Assert.Equal(account, period.AccountId);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(25, true)]
    [InlineData(49, true)]
    [InlineData(50, false)]
    public void Net_small_balance_is_preserved_for_later_disposition(long amount, bool carried)
    {
        var money = new Money(amount);
        Assert.Equal(amount, money.MinorUnits);
        Assert.Equal(carried, money.IsBelowCardMinimum);
    }

    [Fact]
    public void Monetary_addition_cannot_overflow_or_enable_an_unsupported_currency()
    {
        Assert.Throws<OverflowException>(() => new Money(long.MaxValue).Add(new Money(1)));
        Assert.Throws<ArgumentException>(() => new Money(100, "EUR"));
    }
}
