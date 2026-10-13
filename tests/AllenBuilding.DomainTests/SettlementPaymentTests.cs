using AllenBuilding.Domain.Billing;

namespace AllenBuilding.DomainTests;

public sealed class SettlementPaymentTests
{
    [Fact]
    public void One_capture_records_every_reviewed_allocation_without_creating_or_rewriting_debt()
    {
        var now = DateTimeOffset.UtcNow;
        var account = Guid.NewGuid(); var binding = BillingCustomerBinding.Create(account, "acct_settlement", false, "cus_settlement", now);
        var old = Obligation.Issue(account, new Money(1000), "Original charge", new DateOnly(2026, 9, 1), "payer", now);
        var current = Obligation.Issue(account, new Money(1000), "Current charge", new DateOnly(2026, 10, 1), "payer", now);
        var attempt = PaymentAttempt.Admit(Guid.NewGuid(), "payer", binding,
            new[] { new ReviewedCollectionBalance(old.Id, account, old.Version, 25), new ReviewedCollectionBalance(current.Id, account, current.Version, 1000) }, now);
        attempt.BeginDispatch(now);
        var payment = SettlementPayment.Record(attempt, new CanonicalSettlement(binding.ProviderAccount, false, binding.CustomerId, "cs_settlement", "pi_settlement", 1025, "USD"), now);
        Assert.Equal(PaymentAttemptState.Settled, attempt.State); Assert.Equal(1025, payment.AmountMinorUnits); Assert.Equal(2, payment.Allocations.Count);
        Assert.Equal(25, payment.Allocations.Single(x => x.ObligationId == old.Id).AmountMinorUnits);
        Assert.Equal(1000, payment.Allocations.Single(x => x.ObligationId == current.Id).AmountMinorUnits);
        Assert.All(payment.Allocations, item => { Assert.Equal(account, item.AccountId); Assert.Equal(attempt.Id, item.AttemptId); Assert.Equal(payment.Id, item.PaymentId); });
        Assert.Equal(payment.AmountMinorUnits, payment.Allocations.Sum(x => x.AmountMinorUnits));
        Assert.Equal(1000, old.AmountMinorUnits); Assert.Equal(new DateOnly(2026, 9, 1), old.DueOn);
        Assert.Throws<NotSupportedException>(() => ((ICollection<PaymentAllocation>)payment.Allocations).Clear());
        var version = old.Version; old.FinancialStateChanged(); Assert.NotEqual(version, old.Version); Assert.Equal(1000, old.AmountMinorUnits);
    }

    [Fact]
    public void No_cash_receipt_is_created_for_zero_transferred_debt_or_unverified_mismatched_capture()
    {
        var now = DateTimeOffset.UtcNow; var binding = BillingCustomerBinding.Create(Guid.NewGuid(), "acct_settlement", false, "cus_settlement", now);
        var attempt = PaymentAttempt.Admit(Guid.NewGuid(), "payer", binding,
            new[] { new ReviewedCollectionBalance(Guid.NewGuid(), binding.AccountId, Guid.NewGuid(), 1000) }, now);
        var fact = new CanonicalSettlement(binding.ProviderAccount, false, binding.CustomerId, "cs_settlement", "pi_settlement", 1000, "USD");
        Assert.Throws<InvalidOperationException>(() => SettlementPayment.Record(attempt, fact, now));
        attempt.BeginDispatch(now);
        Assert.Throws<InvalidOperationException>(() => SettlementPayment.Record(attempt, fact with { AmountMinorUnits = 0 }, now));
        Assert.Throws<InvalidOperationException>(() => SettlementPayment.Record(attempt, fact with { CustomerId = "cus_other" }, now));
        Assert.Equal(PaymentAttemptState.Dispatching, attempt.State); Assert.Null(attempt.PaymentId);
    }
}
