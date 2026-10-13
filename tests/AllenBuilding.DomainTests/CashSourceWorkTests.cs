using AllenBuilding.Domain.Billing;

namespace AllenBuilding.DomainTests;

public sealed class CashSourceWorkTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    private static PaymentAttempt Original()
    {
        var account = Guid.NewGuid(); var binding = BillingCustomerBinding.Create(account, "acct_source", false, "cus_source", Now);
        var original = PaymentAttempt.Admit(Guid.NewGuid(), "payer", binding, [new(Guid.NewGuid(), account, Guid.NewGuid(), 1000)], Now); original.BeginDispatch(Now);
        SettlementPayment.Record(original, new("acct_source", false, "cus_source", "cs_source", "pi_source", 1000, "USD"), Now); return original;
    }
    [Theory]
    [InlineData("re_source")]
    [InlineData("du_source")]
    [InlineData("dp_source")]
    public void Scoped_source_and_request_identity_survive_lease_reclamation(string source)
    {
        var original = Original(); var operation = Guid.NewGuid(); var work = CashSourceWork.Enqueue(original, source, operation, "manager", Now);
        Assert.True(work.Claim(Now)); var first = work.LeaseToken;
        Assert.Throws<InvalidOperationException>(() => work.RequestAgain(Now.AddSeconds(1)));
        Assert.False(work.Complete(first, CollectionWorkOutcome.Finished, Now.AddMinutes(2)));
        Assert.True(work.Claim(Now.AddMinutes(2))); Assert.NotEqual(first, work.LeaseToken);
        Assert.False(work.Complete(first, CollectionWorkOutcome.Finished, Now.AddMinutes(2)));
        Assert.True(work.Complete(work.LeaseToken, CollectionWorkOutcome.Finished, Now.AddMinutes(2)));
        Assert.False(work.Claim(Now.AddMinutes(3))); work.RequestAgain(Now.AddDays(30)); Assert.True(work.Claim(Now.AddDays(30)));
        Assert.Equal(source, work.SourceId); Assert.Equal(operation, work.OperationId); Assert.Equal(original.Id, work.AttemptId); Assert.Equal(original.AccountId, work.AccountId);
        Assert.Equal(CheckoutProfile.ApiContract, work.ApiVersion); Assert.Equal(PaymentAttemptState.Settled, original.State);
    }
    [Fact]
    public void Pending_evidence_and_unknown_reads_are_bounded_and_keep_the_original_source()
    {
        var original = Original(); var work = CashSourceWork.Enqueue(original, "re_source", Guid.NewGuid(), "manager", Now); var at = Now;
        for (var index = 0; index < 5; index++) { Assert.True(work.Claim(at)); Assert.True(work.Complete(work.LeaseToken, CollectionWorkOutcome.Retry, at)); at = work.NextAttemptAt; }
        Assert.Equal(CollectionWorkState.NeedsReview, work.State); Assert.Equal(5, work.Failures); Assert.Equal("RetryLimit", work.LastError); Assert.False(work.Eligible(Now.AddDays(1)));
        var pending = CashSourceWork.Enqueue(original, "re_source", Guid.NewGuid(), "manager", Now); Assert.True(pending.Claim(Now));
        Assert.True(pending.Complete(pending.LeaseToken, CollectionWorkOutcome.Waiting, Now)); Assert.False(pending.Eligible(Now.AddSeconds(59)));
        Assert.False(pending.Claim(Now.AddHours(24))); Assert.Equal(CollectionWorkState.NeedsReview, pending.State);
        var undispatched = CashSourceWork.Enqueue(original, "re_source", Guid.NewGuid(), "manager", Now); Assert.False(undispatched.Claim(Now.AddHours(24)));
    }
    [Fact]
    public void Evidence_reads_cannot_be_replaced_with_arbitrary_payment_references_or_suppressed_as_writes()
    {
        var original = Original(); Assert.Throws<ArgumentException>(() => CashSourceWork.Enqueue(original, "pi_other", Guid.NewGuid(), "manager", Now));
        Assert.Throws<ArgumentException>(() => CashSourceWork.Enqueue(original, "https://example.test/re_source", Guid.NewGuid(), "manager", Now));
        Assert.Throws<ArgumentException>(() => CashSourceWork.Enqueue(original, "re_source", Guid.Empty, "manager", Now));
        var work = CashSourceWork.Enqueue(original, "re_source", Guid.NewGuid(), "manager", Now); Assert.True(work.Claim(Now));
        Assert.False(work.Complete(work.LeaseToken, CollectionWorkOutcome.Suppressed, Now)); Assert.True(work.Owns(work.LeaseToken, Now));
    }
}
