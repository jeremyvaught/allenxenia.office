using AllenBuilding.Domain.Billing;

namespace AllenBuilding.DomainTests;

public sealed class RefundAllocationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private static (SettlementPayment Capture, CashMovement Movement) Fixture()
    {
        var account = Guid.NewGuid(); var binding = BillingCustomerBinding.Create(account, "acct_refund", false, "cus_refund", Now);
        var attempt = PaymentAttempt.Admit(Guid.NewGuid(), "payer", binding, [new(Guid.NewGuid(), account, Guid.NewGuid(), 1000)], Now); attempt.BeginDispatch(Now);
        var capture = SettlementPayment.Record(attempt, new("acct_refund", false, "cus_refund", "cs_refund", "pi_refund", 1000, "USD"), Now);
        var movement = CashMovement.Record(capture, attempt, new("acct_refund", false, account, attempt.Id, "cus_refund", "pi_refund", "re_refund", "txn_refund", CashMovementKind.Refund, -200, "USD", Now), Now);
        return (capture, movement);
    }
    [Theory]
    [InlineData(RefundDisposition.CreditDebt, 0)]
    [InlineData(RefundDisposition.RetainDebt, 200)]
    public void Returned_cash_and_explicit_debt_disposition_have_distinct_balances(RefundDisposition disposition, long balance)
    {
        var fixture = Fixture(); var version = Guid.NewGuid(); var operation = Guid.NewGuid();
        var allocation = RefundAllocation.Record(fixture.Movement, fixture.Capture.Allocations.Single(), version, 200, 1000);
        var credit = disposition == RefundDisposition.CreditDebt ? ObligationCredit.RecordRefundCredit(operation, "manager", allocation, 1000, "Approved return", Now) : null;
        var review = CashMovementReview.Open(fixture.Movement); review.ApplyRefund(fixture.Movement, operation, "manager", disposition, "Approved return", Now);
        Assert.Equal(balance, 1000 - (credit?.AmountMinorUnits ?? 0) - (1000 + allocation.SignedMinorUnits));
        Assert.Equal(1000, fixture.Capture.AmountMinorUnits); Assert.Equal(1000, fixture.Capture.Allocations.Single().AmountMinorUnits);
        Assert.Equal(CashMovementReviewState.AppliedPendingVerification, review.State); Assert.Equal(disposition, review.Disposition);
        Assert.Equal(operation, review.OperationId); Assert.Equal("manager", review.ActorId);
        if (credit is not null) { Assert.Equal(fixture.Movement.Id, credit.SourceMovementId); Assert.Equal(version, credit.ReviewedVersion); }
        Assert.Throws<InvalidOperationException>(() => review.ApplyRefund(fixture.Movement, Guid.NewGuid(), "manager", disposition, "Duplicate classification", Now));
    }
    [Fact]
    public void Prior_returns_limit_still_available_captured_funds()
    {
        var fixture = Fixture(); var original = fixture.Capture.Allocations.Single();
        Assert.Throws<ArgumentException>(() => RefundAllocation.Record(fixture.Movement, original, Guid.NewGuid(), 200, 199));
        Assert.Throws<ArgumentException>(() => RefundAllocation.Record(fixture.Movement, original, Guid.NewGuid(), 201, 1000));
        Assert.Throws<ArgumentException>(() => RefundAllocation.Record(fixture.Movement, original, Guid.NewGuid(), 0, 1000));
        var allocation = RefundAllocation.Record(fixture.Movement, original, Guid.NewGuid(), 200, 200);
        Assert.Throws<ArgumentException>(() => ObligationCredit.RecordRefundCredit(Guid.NewGuid(), "manager", allocation, 199, "Over-credit", Now));
    }
    [Fact]
    public void Another_capture_cannot_supply_the_refunds_allocation()
    {
        var fixture = Fixture(); var other = Fixture();
        Assert.Throws<ArgumentException>(() => RefundAllocation.Record(fixture.Movement, other.Capture.Allocations.Single(), Guid.NewGuid(), 200, 1000));
        Assert.Throws<InvalidOperationException>(() => CashMovementReview.Open(fixture.Movement).ApplyRefund(other.Movement, Guid.NewGuid(), "manager", RefundDisposition.RetainDebt, "Wrong fact", Now));
    }
}
