using AllenBuilding.Domain.Billing;

namespace AllenBuilding.DomainTests;

public sealed class CashMovementTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private static (PaymentAttempt Original, SettlementPayment Capture, CanonicalCashMovement Fact) Fixture()
    {
        var account = Guid.NewGuid(); var binding = BillingCustomerBinding.Create(account, "acct_cash", false, "cus_cash", Now);
        var original = PaymentAttempt.Admit(Guid.NewGuid(), "payer", binding, [new(Guid.NewGuid(), account, Guid.NewGuid(), 1000)], Now); original.BeginDispatch(Now);
        var capture = SettlementPayment.Record(original, new("acct_cash", false, "cus_cash", "cs_cash", "pi_cash", 1000, "USD"), Now);
        return (original, capture, new("acct_cash", false, account, original.Id, "cus_cash", "pi_cash", "re_cash", "txn_cash", CashMovementKind.Refund, -200, "USD", Now));
    }
    [Fact]
    public void Returned_cash_preserves_capture_and_requires_separate_debt_review()
    {
        var fixture = Fixture(); var movement = CashMovement.Record(fixture.Capture, fixture.Original, fixture.Fact, Now);
        Assert.Equal(-200, movement.SignedMinorUnits); Assert.Equal(1000, fixture.Capture.AmountMinorUnits); Assert.Equal(1000, fixture.Capture.Allocations.Single().AmountMinorUnits);
        Assert.Equal(PaymentAttemptState.Settled, fixture.Original.State); Assert.Equal(CashMovementReviewState.Pending, CashMovementReview.Open(movement).State);
        Assert.True(movement.Matches(fixture.Fact)); Assert.False(movement.Matches(fixture.Fact with { SignedMinorUnits = -201 }));
    }
    [Theory]
    [InlineData(200)]
    [InlineData(0)]
    [InlineData(-1001)]
    [InlineData(long.MinValue)]
    public void Wrong_sign_zero_excess_and_overflow_cannot_be_a_refund(long amount)
    {
        var fixture = Fixture(); Assert.Throws<InvalidOperationException>(() => CashMovement.Record(fixture.Capture, fixture.Original, fixture.Fact with { SignedMinorUnits = amount }, Now));
    }
    [Fact]
    public void Restored_dispute_funds_are_a_positive_verified_transaction()
    {
        var fixture = Fixture(); var restored = CashMovement.Record(fixture.Capture, fixture.Original, fixture.Fact with { SourceId = "du_cash", Kind = CashMovementKind.DisputeReinstatement, SignedMinorUnits = 1000 }, Now);
        Assert.Equal(1000, restored.SignedMinorUnits); Assert.Equal(CashMovementKind.DisputeReinstatement, restored.Kind);
        Assert.Throws<InvalidOperationException>(() => CashMovement.Record(fixture.Capture, fixture.Original, fixture.Fact with { SourceId = "du_cash", Kind = CashMovementKind.DisputeReinstatement }, Now));
    }
    [Fact]
    public void Customer_mode_payment_and_currency_must_match_the_original_capture()
    {
        var fixture = Fixture();
        foreach (var mismatch in new[] { fixture.Fact with { CustomerId = "cus_other" }, fixture.Fact with { LiveMode = true }, fixture.Fact with { PaymentId = "pi_other" }, fixture.Fact with { Currency = "EUR" }, fixture.Fact with { AccountId = Guid.NewGuid() } })
            Assert.Throws<InvalidOperationException>(() => CashMovement.Record(fixture.Capture, fixture.Original, mismatch, Now));
    }
}
