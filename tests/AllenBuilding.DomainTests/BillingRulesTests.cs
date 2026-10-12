using AllenBuilding.Domain.Billing;

namespace AllenBuilding.DomainTests;

public sealed class BillingRulesTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(49, false)]
    [InlineData(50, true)]
    [InlineData(99_999_999, true)]
    [InlineData(100_000_000, false)]
    public void Issued_amount_limits_are_separate_from_remaining_balance_limits(long cents, bool valid)
    {
        var amount = new Money(cents);
        if (valid) BillingRules.IssuedAmount(amount);
        else Assert.Throws<ArgumentException>(() => BillingRules.IssuedAmount(amount));
        Assert.Equal(cents is > 0 and < 50, amount.IsBelowCardMinimum);
    }

    [Fact]
    public void Default_money_cannot_issue_a_charge_without_an_explicit_currency()
        => Assert.Throws<ArgumentException>(() => BillingRules.IssuedAmount(default));

    [Fact]
    public void Partial_months_need_explicit_charges_and_replacement_preserves_original_terms()
    {
        var account = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var original = RentAgreement.Create(account, new Money(100_000), 1, new DateOnly(2026, 2, 18), null, "manager", now);
        Assert.False(original.Covers(RentCalendar.ForMonth(account, 2026, 2, 1)));
        Assert.True(original.Covers(RentCalendar.ForMonth(account, 2026, 3, 1)));
        var replacement = RentAgreement.Create(account, new Money(120_000), 31, new DateOnly(2026, 4, 1), null, "manager", now);
        original.Supersede(replacement);
        Assert.Null(original.OriginalEndsOn); Assert.Equal(new DateOnly(2026, 4, 1), original.EndsOn);
        Assert.Equal(100_000, original.AmountMinorUnits); Assert.Equal(replacement.Id, original.SupersededBy);
        var april = RentCalendar.ForMonth(account, 2026, 4, replacement.DueDay);
        Assert.Throws<InvalidOperationException>(() => Obligation.Rent(BillingPeriod.Create(april), replacement, april with { DueOn = new DateOnly(2026, 4, 1) }, "manager", now));
        var charge = Obligation.Rent(BillingPeriod.Create(april), replacement, april, "manager", now);
        Assert.Equal(new DateOnly(2026, 4, 30), charge.DueOn); Assert.Equal(120_000, charge.AmountMinorUnits);
        Assert.Throws<InvalidOperationException>(() => original.Supersede(replacement));
    }
}
