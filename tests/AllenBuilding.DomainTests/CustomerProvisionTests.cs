using AllenBuilding.Domain.Billing;

namespace AllenBuilding.DomainTests;

public sealed class CustomerProvisionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private static CustomerProvision Original() => CustomerProvision.Enqueue(Guid.NewGuid(), "payer", "acct_customers", false, Now);
    [Fact]
    public void Lost_acknowledgment_reuses_the_original_body_key_and_is_fenced()
    {
        var original = Original(); var key = original.ProviderIdempotencyKey; Assert.True(original.Claim(Now)); original.BeginDispatch(Now);
        var stale = original.LeaseToken; Assert.False(original.Claim(Now.AddMinutes(1))); Assert.True(original.Claim(Now.AddMinutes(2)));
        Assert.False(original.Complete(stale, CustomerProvisionOutcome.Verified, "cus_stale", Now.AddMinutes(2)));
        Assert.True(original.Complete(original.LeaseToken, CustomerProvisionOutcome.Unknown, null, Now.AddMinutes(2)));
        Assert.Null(original.CustomerId); Assert.Equal(CustomerProvisionState.Queued, original.State); Assert.Equal(key, original.ProviderIdempotencyKey);
        Assert.Equal(Now, original.DispatchedAt); Assert.Equal(CheckoutProfile.ApiContract, original.ApiVersion);
    }
    [Fact]
    public void Old_unknown_creation_requires_investigation_and_cannot_mint_a_replacement()
    {
        var original = Original(); Assert.True(original.Claim(Now)); original.BeginDispatch(Now);
        Assert.True(original.Complete(original.LeaseToken, CustomerProvisionOutcome.Unknown, null, Now));
        Assert.False(original.Claim(Now.AddHours(23))); Assert.Equal(CustomerProvisionState.NeedsReview, original.State);
        Assert.False(original.CanCreate(Now.AddHours(23))); Assert.False(original.Claim(Now.AddDays(2)));
        Assert.Throws<InvalidOperationException>(() => original.Suppress(Now.AddDays(2)));
    }
    [Fact]
    public void Retry_limit_and_mismatch_are_terminal_investigation_states()
    {
        var original = Original(); var now = Now;
        for (var index = 0; index < 5; index++)
        {
            Assert.True(original.Claim(now)); if (index == 0) original.BeginDispatch(now);
            Assert.True(original.Complete(original.LeaseToken, CustomerProvisionOutcome.Unknown, null, now)); now = original.NextAttemptAt;
        }
        Assert.Equal(CustomerProvisionState.NeedsReview, original.State); Assert.Equal(5, original.Failures);
        var mismatch = Original(); Assert.True(mismatch.Claim(Now)); mismatch.BeginDispatch(Now);
        Assert.True(mismatch.Complete(mismatch.LeaseToken, CustomerProvisionOutcome.Invalid, "cus_wrong", Now)); Assert.Null(mismatch.CustomerId);
        Assert.Equal("CanonicalMismatch", mismatch.LastError);
    }
    [Fact]
    public void Access_revocation_suppresses_only_never_dispatched_work()
    {
        var original = Original(); original.Suppress(Now); Assert.False(original.Claim(Now)); Assert.Null(original.DispatchedAt);
        var dispatched = Original(); Assert.True(dispatched.Claim(Now)); dispatched.BeginDispatch(Now);
        Assert.Throws<InvalidOperationException>(() => dispatched.Suppress(Now.AddMinutes(2)));
        Assert.True(dispatched.Complete(dispatched.LeaseToken, CustomerProvisionOutcome.Verified, "cus_original", Now));
        Assert.Equal(CustomerProvisionState.Bound, dispatched.State); Assert.False(dispatched.Claim(Now));
        Assert.Throws<InvalidOperationException>(() => dispatched.Review("BindingConflict"));
    }
    [Theory]
    [InlineData("not-an-account", "payer")]
    [InlineData("acct_valid", "")]
    public void Invalid_identity_never_admits_a_provider_write(string account, string actor)
        => Assert.Throws<ArgumentException>(() => CustomerProvision.Enqueue(Guid.NewGuid(), actor, account, false, Now));
}
