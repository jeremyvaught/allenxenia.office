using AllenBuilding.Domain.Billing;

namespace AllenBuilding.DomainTests;

public sealed class AutopaySetupTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    [Fact]
    public void Verified_setup_records_original_identity_and_requires_off_session_ready_facts_before_activation()
    {
        var (auth, work) = Fixture(); var waiting = Fact(auth) with { State = AutopaySetupState.Waiting, PaymentMethodId = null };
        auth.ObserveVerifiedSetup(waiting, true); Assert.Equal(AutopayState.AwaitingMethod, auth.State); Assert.Equal(waiting.SessionId, auth.SetupSessionId); Assert.Null(auth.PaymentMethodId);
        // Same observation instant with a changed canonical state must still be projected.
        auth.ObserveVerifiedSetup(Fact(auth), true); Assert.Equal(AutopayState.AwaitingProvider, auth.State); Assert.Equal(AutopaySetupState.Ready, auth.SetupState); Assert.Equal("pm_verified", auth.PaymentMethodId);
        Assert.False(auth.AdmitsNewAutomaticWork); Assert.Null(auth.SubscriptionId);
        var version = auth.Version; auth.ObserveVerifiedSetup(Fact(auth), true); Assert.Equal(version, auth.Version);
        Assert.Throws<InvalidOperationException>(() => auth.ObserveVerifiedSetup(waiting with { CheckedAt = Now.AddMinutes(1) }, true));
        var activation = AutopayWork.Enqueue(auth, AutopayWorkStage.Activate, Now, work.Profile); Assert.NotEqual(work.ProviderOperationKey, activation.ProviderOperationKey); Assert.Equal(work.PublicOrigin, activation.PublicOrigin);
    }
    [Theory]
    [InlineData("merchant")]
    [InlineData("mode")]
    [InlineData("account")]
    [InlineData("customer")]
    [InlineData("consent")]
    [InlineData("mode-payment")]
    [InlineData("on-session")]
    [InlineData("method")]
    [InlineData("expiry")]
    [InlineData("old-read")]
    public void Foreign_or_incomplete_setup_evidence_cannot_register_a_payment_method(string mismatch)
    {
        var (auth, _) = Fixture(); var original = Fact(auth);
        var wrong = mismatch switch
        {
            "merchant" => original with { ProviderAccount = "acct_elsewhere" },
            "mode" => original with { LiveMode = true },
            "account" => original with { AccountId = Guid.NewGuid() },
            "customer" => original with { CustomerId = "cus_foreign" },
            "consent" => original with { ConsentHash = new string('0', 64) },
            "mode-payment" => original with { SetupOnly = false },
            "on-session" => original with { OffSessionUsage = false },
            "method" => original with { PaymentMethodId = "not-a-method" },
            "expiry" => original with { ExpiresAt = Now },
            _ => original with { CheckedAt = Now.AddSeconds(-1) }
        };
        Assert.Throws<InvalidOperationException>(() => auth.ObserveVerifiedSetup(wrong, true)); Assert.Null(auth.PaymentMethodId); Assert.Null(auth.SetupSessionId); Assert.Equal(AutopayState.AwaitingMethod, auth.State);
    }
    [Fact]
    public void Late_setup_keeps_original_stop_and_disallowed_activation_cannot_start_a_subscription()
    {
        var (auth, _) = Fixture(); var stop = Guid.NewGuid(); auth.RequestStop(stop, "payer", AutopayStopTiming.Immediately, Now);
        auth.ObserveVerifiedSetup(Fact(auth), true); Assert.Equal(AutopayState.StopPending, auth.State); Assert.Equal(stop, auth.StopOperationId); Assert.Equal("pm_verified", auth.PaymentMethodId); Assert.False(auth.AdmitsNewAutomaticWork);
        var (revoked, _) = Fixture(); revoked.RequireReview(); revoked.ObserveVerifiedSetup(Fact(revoked), false); Assert.Equal(AutopayState.NeedsReview, revoked.State); Assert.NotNull(revoked.SetupSessionId); Assert.False(revoked.AdmitsNewAutomaticWork);
    }
    [Fact]
    public void Known_setup_identity_is_immutable_and_expiration_comes_from_provider_state_including_early_expiry()
    {
        var (auth, _) = Fixture(); var waiting = Fact(auth) with { State = AutopaySetupState.Waiting, PaymentMethodId = null }; auth.ObserveVerifiedSetup(waiting, true);
        Assert.Throws<InvalidOperationException>(() => auth.ObserveVerifiedSetup(Fact(auth) with { SessionId = "cs_replacement" }, true));
        Assert.Throws<InvalidOperationException>(() => auth.ObserveVerifiedSetup(Fact(auth) with { ExpiresAt = Now.AddHours(2) }, true));
        Assert.Throws<InvalidOperationException>(() => auth.ObserveVerifiedSetup(waiting with { CheckedAt = waiting.ExpiresAt }, true));
        // Stripe can expire an open session early. Only a canonical Expired fact establishes that result, never elapsed time alone.
        auth.ObserveVerifiedSetup(waiting with { State = AutopaySetupState.Expired }, false); Assert.Equal(AutopaySetupState.Expired, auth.SetupState);
        Assert.Throws<InvalidOperationException>(() => auth.ObserveVerifiedSetup(Fact(auth) with { CheckedAt = waiting.ExpiresAt.AddSeconds(1) }, true));
    }
    [Fact]
    public void Reclaimed_fences_cannot_complete_and_unknown_old_creation_is_not_suppressible_or_repeatable()
    {
        var (_, work) = Fixture(); var key = work.ProviderOperationKey; Assert.True(work.Claim(Now)); work.BeginDispatch(Now); var old = work.LeaseToken;
        Assert.False(work.Claim(Now.AddSeconds(1))); Assert.False(work.Complete(old, CollectionWorkOutcome.Finished, Now.AddMinutes(2)));
        Assert.True(work.Claim(Now.AddMinutes(2))); Assert.NotEqual(old, work.LeaseToken); Assert.Equal(1, work.Failures); Assert.False(work.Complete(old, CollectionWorkOutcome.Finished, Now.AddMinutes(2)));
        Assert.True(work.Complete(work.LeaseToken, CollectionWorkOutcome.Waiting, Now.AddMinutes(2)));
        Assert.True(work.WithinOriginalWriteWindow(Now.AddHours(23).AddTicks(-1))); Assert.False(work.WithinOriginalWriteWindow(Now.AddHours(23)));
        Assert.True(work.Claim(Now.AddHours(23))); Assert.Equal(key, work.ProviderOperationKey); Assert.Throws<InvalidOperationException>(() => work.Suppress(Now.AddHours(23))); Assert.Equal("https://original.example.test", work.Profile!.PublicOrigin);
    }
    [Fact]
    public void Failures_and_age_are_bounded_without_consuming_another_operation_identity()
    {
        var (_, work) = Fixture(); var now = Now;
        for (var i = 0; i < 5; i++) { Assert.True(work.Claim(now)); if (i == 0) work.BeginDispatch(now); Assert.True(work.Complete(work.LeaseToken, CollectionWorkOutcome.Retry, now)); now = work.NextAttemptAt; }
        Assert.Equal(CollectionWorkState.NeedsReview, work.State); Assert.Equal(5, work.Failures); Assert.False(work.Claim(Now.AddDays(1)));
        var (_, aged) = Fixture(); Assert.False(aged.Claim(Now.AddHours(48))); Assert.Equal(CollectionWorkState.NeedsReview, aged.State);
        var (auth, _) = Fixture(); auth.RequestStop(Guid.NewGuid(), "payer", AutopayStopTiming.EndOfCurrentRentMonth, Now); var stop = AutopayWork.Enqueue(auth, AutopayWorkStage.Stop, Now);
        Assert.True(stop.DeadlineAt > Now.AddHours(48)); Assert.True(stop.Claim(Now.AddDays(3))); // A reviewed future stop is not given setup's 48h limit.
    }
    [Fact]
    public void Never_dispatched_setup_can_be_suppressed_and_an_unfrozen_profile_is_not_guessed()
    {
        var (auth, work) = Fixture(); work.Suppress(Now); Assert.Equal(CollectionWorkState.Suppressed, work.State); Assert.False(work.Claim(Now)); Assert.Null(work.DispatchedAt);
        var missing = AutopayWork.Enqueue(auth, AutopayWorkStage.Setup, Now); Assert.Null(missing.Profile); Assert.Equal(auth.SetupOperationKey, missing.ProviderOperationKey);
        Assert.Throws<ArgumentException>(() => AutopayWork.Enqueue(auth, AutopayWorkStage.Activate, Now));
    }
    private static VerifiedAutopaySetup Fact(AutopayAuthorization auth) => new(auth.ProviderAccount, auth.LiveMode, auth.AccountId, auth.CustomerId, auth.ConsentHash, "cs_original", Now.AddHours(24), AutopaySetupState.Ready, "pm_verified", true, true, Now);
    private static (AutopayAuthorization, AutopayWork) Fixture()
    {
        var account = RentAccount.Open(Guid.NewGuid(), Guid.NewGuid()); var binding = BillingCustomerBinding.Create(account.Id, "acct_setup", false, "cus_setup", Now);
        var terms = RentAgreement.Create(account.Id, new Money(100000), 31, new(2026, 1, 1), null, "payer", Now);
        var auth = AutopayAuthorization.Consent(Guid.NewGuid(), "payer", account, binding, AutopaySchedule.Select(terms, Now, []), true, Now);
        return (auth, AutopayWork.Enqueue(auth, AutopayWorkStage.Setup, Now, new("https://original.example.test", "pmc_original")));
    }
}
