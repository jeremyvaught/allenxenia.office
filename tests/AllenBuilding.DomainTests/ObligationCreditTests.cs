using AllenBuilding.Domain.Billing;

namespace AllenBuilding.DomainTests;

public sealed class ObligationCreditTests
{
    private static ReviewedCollectionBalance Balance(long amount = 1000) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), amount);
    [Fact]
    public void A_credit_can_leave_one_cent_without_recording_cash_or_deleting_the_charge()
    {
        var original = Balance(); var credit = ObligationCredit.RecordLocal(Guid.NewGuid(), "manager", original, 999, ObligationCreditKind.Credit, " Approved adjustment ", DateTimeOffset.UtcNow);
        Assert.Equal(999, credit.AmountMinorUnits); Assert.Equal(original.ObligationId, credit.ObligationId); Assert.Equal(original.Version, credit.ReviewedVersion); Assert.Equal("Approved adjustment", credit.Reason);
        Assert.Equal(1000, original.RemainingMinorUnits); Assert.Equal(1, original.RemainingMinorUnits - credit.AmountMinorUnits);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1001)]
    public void Zero_negative_and_excess_credit_cannot_reduce_a_reviewed_balance(long amount)
        => Assert.Throws<ArgumentException>(() => ObligationCredit.RecordLocal(Guid.NewGuid(), "manager", Balance(), amount, ObligationCreditKind.Credit, "Reason", DateTimeOffset.UtcNow));
    [Fact]
    public void Held_or_provider_instructed_debt_needs_provider_reconciliation()
    {
        foreach (var review in new[] { Balance() with { Held = true }, Balance() with { HasProviderInstruction = true }, Balance() with { Currency = "EUR" } })
            Assert.Throws<ArgumentException>(() => ObligationCredit.RecordLocal(Guid.NewGuid(), "manager", review, 100, ObligationCreditKind.Credit, "Reason", DateTimeOffset.UtcNow));
    }
    [Fact]
    public void A_reasoned_exact_writeoff_removes_debt_without_manufacturing_a_payment()
    {
        var review = Balance(25); var credit = ObligationCredit.RecordLocal(Guid.NewGuid(), "manager", review, 25, ObligationCreditKind.WriteOff, "Approved small balance write-off", DateTimeOffset.UtcNow);
        Assert.Equal(ObligationCreditKind.WriteOff, credit.Kind); Assert.Equal(25, credit.AmountMinorUnits); Assert.Equal("USD", credit.Currency);
    }
}
