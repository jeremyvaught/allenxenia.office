using AllenBuilding.Domain.Billing;

namespace AllenBuilding.DomainTests;

public sealed class CollectionWorkTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private static CollectionWork Work()
    {
        var account = Guid.NewGuid(); var binding = BillingCustomerBinding.Create(account, "acct_work", false, "cus_work", Now);
        var original = PaymentAttempt.Admit(Guid.NewGuid(), "payer", binding, [new(Guid.NewGuid(), account, Guid.NewGuid(), 1000)], Now);
        return CollectionWork.Enqueue(original, new("https://office.example.test/", "pmc_work"));
    }
    [Theory]
    [InlineData("http://office.example.test", "pmc_valid", CheckoutProfile.ApiContract)]
    [InlineData("https://office.example.test/private", "pmc_valid", CheckoutProfile.ApiContract)]
    [InlineData("https://user:password@office.example.test", "pmc_valid", CheckoutProfile.ApiContract)]
    [InlineData("https://office.example.test", "pmc_bad-value", CheckoutProfile.ApiContract)]
    [InlineData("https://office.example.test", "pmc_valid", "2026-09-30.preview")]
    public void Dispatch_profile_rejects_unsupported_or_unsafe_settings(string origin, string methods, string api)
        => Assert.Throws<ArgumentException>(() => new CheckoutProfile(origin, methods, api));
    [Fact]
    public void A_reclaimed_operation_fences_the_old_worker_and_preserves_request_settings()
    {
        var work = Work(); Assert.Equal("https://office.example.test", work.Profile.PublicOrigin); Assert.True(work.Claim(Now)); var old = work.LeaseToken;
        Assert.False(work.Claim(Now.AddSeconds(1))); Assert.False(work.Complete(old, CollectionWorkOutcome.Finished, Now.AddMinutes(2)));
        Assert.True(work.Claim(Now.AddMinutes(2))); Assert.NotEqual(old, work.LeaseToken); Assert.Equal(1, work.Failures);
        Assert.False(work.Complete(old, CollectionWorkOutcome.Finished, Now.AddMinutes(2)));
        Assert.True(work.Complete(work.LeaseToken, CollectionWorkOutcome.Waiting, Now.AddMinutes(2))); Assert.False(work.Eligible(Now.AddMinutes(2)));
        Assert.Equal("pmc_work", work.Profile.PaymentMethodConfiguration);
    }
    [Fact]
    public void Five_failures_or_an_aged_unfinished_operation_stop_automatic_work()
    {
        var work = Work(); var now = Now;
        for (var index = 0; index < 5; index++) { Assert.True(work.Claim(now)); Assert.True(work.Complete(work.LeaseToken, CollectionWorkOutcome.Retry, now)); now = work.NextAttemptAt; }
        Assert.Equal(CollectionWorkState.NeedsReview, work.State); Assert.Equal(5, work.Failures); Assert.False(work.Claim(now.AddDays(1)));
        var aged = Work(); Assert.True(aged.Claim(Now)); Assert.True(aged.Complete(aged.LeaseToken, CollectionWorkOutcome.Waiting, Now));
        Assert.False(aged.Claim(Now.AddHours(24))); Assert.Equal(CollectionWorkState.NeedsReview, aged.State);
    }
}
