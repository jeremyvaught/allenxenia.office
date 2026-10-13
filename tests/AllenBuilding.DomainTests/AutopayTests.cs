using AllenBuilding.Domain.Billing;

namespace AllenBuilding.DomainTests;

public sealed class AutopayTests
{
    [Fact]
    public void An_original_activation_response_can_be_retained_before_the_later_cleanup_request_without_verifying_stop()
    {
        var (account, binding, terms, now) = Fixture(); var authorization = AutopayAuthorization.Consent(Guid.NewGuid(), "payer", account, binding, AutopaySchedule.Select(terms, now, []), true, now);
        authorization.ApplyVerifiedMethod(binding.ProviderAccount, false, binding.CustomerId, "pm_saved", authorization.ConsentHash);
        var fact = new VerifiedAutopaySubscription(binding.ProviderAccount, false, account.Id, binding.CustomerId, "sub_original", "pm_saved", authorization.ConsentHash, 100000, "USD", 31, 17, authorization.FirstAnchorUtc, true, now.AddSeconds(1));
        var cleanup = Guid.NewGuid(); authorization.RequestCleanupStop(cleanup, now.AddSeconds(2)); Assert.Null(authorization.StopActorId);
        var requested = authorization.StopRequestedAt; var cleanupVersion = authorization.Version;
        authorization.RequestCleanupStop(cleanup, now.AddSeconds(3)); Assert.Equal(cleanupVersion, authorization.Version); Assert.Equal(requested, authorization.StopRequestedAt);
        Assert.Throws<ArgumentException>(() => authorization.RequestCleanupStop(Guid.Empty, now));
        Assert.Throws<InvalidOperationException>(() => authorization.RequestStop(cleanup, "payer", AutopayStopTiming.Immediately, now.AddSeconds(3)));
        Assert.Throws<InvalidOperationException>(() => authorization.ObserveOriginalSubscriptionForStop(fact));
        Assert.Throws<InvalidOperationException>(() => authorization.ObserveDispatchedSubscriptionForCleanup(fact with { CustomerId = "cus_other" }, now));
        Assert.Throws<InvalidOperationException>(() => authorization.ObserveDispatchedSubscriptionForCleanup(fact, now.AddSeconds(2)));
        authorization.ObserveDispatchedSubscriptionForCleanup(fact, now);
        Assert.Equal("sub_original", authorization.SubscriptionId); Assert.Equal(AutopayState.StopPending, authorization.State); Assert.False(authorization.AdmitsNewAutomaticWork);
        var version = authorization.Version; authorization.ObserveDispatchedSubscriptionForCleanup(fact, now); Assert.Equal(version, authorization.Version);
        Assert.Throws<InvalidOperationException>(() => authorization.VerifyStop(new(binding.ProviderAccount, false, account.Id, binding.CustomerId, "sub_original", true, true, fact.CheckedAt)));
    }
    [Theory]
    [InlineData(2026, 1, 31, 2026, 2, 28)]
    [InlineData(2028, 1, 31, 2028, 2, 29)]
    [InlineData(2026, 2, 31, 2026, 3, 31)]
    [InlineData(2026, 3, 1, 2026, 4, 1)]
    [InlineData(2026, 10, 15, 2026, 11, 15)]
    public void Future_calendar_schedule_clamps_short_months_but_preserves_the_original_due_day(int year, int month, int dueDay, int nextYear, int nextMonth, int expectedDay)
    {
        var terms = Terms(dueDay); var now = new DateTimeOffset(year, month, 12, 20, 0, 0, TimeSpan.Zero);
        var schedule = AutopaySchedule.Select(terms, now, []);
        Assert.Equal(new DateOnly(nextYear, nextMonth, 1), schedule.FirstPeriodStartsOn); Assert.Equal(new DateOnly(nextYear, nextMonth, expectedDay), schedule.FirstDueOn); Assert.Equal(dueDay, schedule.DueDay);
        Assert.Equal(schedule.FirstDueOn, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(schedule.FirstAnchorUtc, TimeZoneInfo.FindSystemTimeZoneById(AutopaySchedule.TimeZone)).DateTime));
        Assert.Equal(17, schedule.FirstAnchorUtc.Hour); Assert.Equal(TimeSpan.Zero, schedule.FirstAnchorUtc.Offset);
        if (dueDay == 31) Assert.Equal(31, schedule.DueFor(new DateOnly(2029, 3, 1)).Day);
    }
    [Fact]
    public void Selection_skips_current_partial_paid_adjusted_and_instructed_periods_and_stops_at_agreement_end()
    {
        var terms = Terms(1); var now = new DateTimeOffset(2026, 10, 1, 3, 0, 0, TimeSpan.Zero); // Still September in Xenia.
        var schedule = AutopaySchedule.Select(terms, now, [new(2026, 10, 1), new(2026, 11, 1)]); Assert.Equal(new DateOnly(2026, 12, 1), schedule.FirstPeriodStartsOn);
        var later = RentAgreement.Create(terms.AccountId, new Money(100000), 1, new(2027, 1, 15), new(2027, 3, 1), "manager", now);
        var bounded = AutopaySchedule.Select(later, now, []);
        Assert.Equal(new DateOnly(2027, 2, 1), bounded.FirstPeriodStartsOn); Assert.Equal(new DateOnly(2027, 3, 1), bounded.EndsBefore);
        Assert.Equal(new DateTimeOffset(2027, 3, 1, 5, 0, 0, TimeSpan.Zero), bounded.AutomaticCollectionEndsAt);
        Assert.Throws<ArgumentException>(() => bounded.DueFor(new(2027, 3, 1)));
        Assert.Throws<InvalidOperationException>(() => AutopaySchedule.Select(later, now, [new(2027, 2, 1)]));
        Assert.Throws<ArgumentException>(() => AutopaySchedule.Select(terms, now, [new(2026, 10, 2)]));
        Assert.Throws<ArgumentException>(() => AutopaySchedule.Select(terms, now, [new(2026, 10, 1), new(2026, 10, 1)]));
        Assert.Throws<ArgumentException>(() => AutopaySchedule.Select(terms, now, Enumerable.Range(0, 26).Select(i => new DateOnly(2027, 1, 1).AddMonths(i)).ToArray()));
        Assert.Throws<ArgumentException>(() => schedule.DueFor(new(2026, 10, 1)));
    }
    [Fact]
    public void Consent_and_activation_require_exact_customer_terms_anchor_amount_and_verified_setup_and_do_not_settle_rent()
    {
        var (account, binding, terms, now) = Fixture(); var schedule = AutopaySchedule.Select(terms, now, []);
        Assert.Throws<ArgumentException>(() => AutopayAuthorization.Consent(Guid.NewGuid(), "payer", account, binding, schedule, false, now));
        Assert.Throws<ArgumentException>(() => AutopayAuthorization.Consent(Guid.NewGuid(), "payer", account, binding, schedule with { FirstAnchorUtc = now }, true, now));
        Assert.Throws<ArgumentException>(() => AutopayAuthorization.Consent(Guid.NewGuid(), "payer", account, binding,
            schedule with { FirstPeriodStartsOn = new(2026, 10, 1), FirstDueOn = new(2026, 10, 31), FirstAnchorUtc = new(2026, 10, 31, 17, 0, 0, TimeSpan.Zero) }, true, now));
        var foreign = BillingCustomerBinding.Create(Guid.NewGuid(), binding.ProviderAccount, false, "cus_other", now);
        Assert.Throws<ArgumentException>(() => AutopayAuthorization.Consent(Guid.NewGuid(), "payer", account, foreign, schedule, true, now));
        var authorization = AutopayAuthorization.Consent(Guid.NewGuid(), "payer", account, binding, schedule, true, now);
        Assert.Equal(AutopayState.AwaitingMethod, authorization.State); Assert.False(authorization.AdmitsNewAutomaticWork); Assert.Equal(account.Version, authorization.ReviewedAccountVersion);
        Assert.Equal(64, authorization.ConsentHash.Length); Assert.Contains(authorization.Id.ToString("N"), authorization.SubscriptionOperationKey);
        Assert.Throws<InvalidOperationException>(() => authorization.ApplyVerifiedMethod(binding.ProviderAccount, false, "cus_other", "pm_saved", authorization.ConsentHash));
        authorization.ApplyVerifiedMethod(binding.ProviderAccount, false, binding.CustomerId, "pm_saved", authorization.ConsentHash);
        var verified = new VerifiedAutopaySubscription(binding.ProviderAccount, false, account.Id, binding.CustomerId, "sub_original", "pm_saved", authorization.ConsentHash, 100000, "USD", 31, 17, schedule.FirstAnchorUtc, true, now.AddMinutes(1));
        foreach (var invalid in new[] { verified with { MonthlyMinorUnits = 90000 }, verified with { DueDay = 30 }, verified with { HourUtc = 0 }, verified with { InitialPeriodNotCharged = false }, verified with { LiveMode = true }, verified with { Currency = "EUR" }, verified with { ConsentHash = new string('A', 64) }, verified with { AccountId = Guid.NewGuid() }, verified with { FirstAnchorUtc = schedule.FirstAnchorUtc.AddDays(1) }, verified with { PaymentMethodId = "pm_wrong" } })
            Assert.Throws<InvalidOperationException>(() => authorization.ApplyVerifiedSubscription(invalid));
        authorization.ApplyVerifiedSubscription(verified); var version = authorization.Version; authorization.ApplyVerifiedSubscription(verified); Assert.Equal(version, authorization.Version); Assert.True(authorization.AdmitsNewAutomaticWork);
        Assert.True(authorization.CanAdmitPeriod(schedule.FirstPeriodStartsOn, terms.Version, now));
        Assert.False(authorization.CanAdmitPeriod(schedule.FirstPeriodStartsOn, Guid.NewGuid(), now));
        Assert.False(authorization.CanAdmitPeriod(schedule.FirstPeriodStartsOn, terms.Version, now.AddSeconds(-1)));
        Assert.Throws<InvalidOperationException>(() => authorization.ApplyVerifiedSubscription(verified with { SubscriptionId = "sub_replacement" }));
        account.EndTenancy(now); Assert.Throws<ArgumentException>(() => AutopayAuthorization.Consent(Guid.NewGuid(), "payer", account, binding, schedule, true, now));
    }
    [Theory]
    [InlineData(2026, 2, 15, 2026, 3, 1, 5)]
    [InlineData(2026, 3, 15, 2026, 4, 1, 4)]
    [InlineData(2026, 10, 15, 2026, 11, 1, 4)]
    [InlineData(2026, 11, 15, 2026, 12, 1, 5)]
    public void End_of_rent_month_cutoff_uses_calendar_midnight_and_DST_not_the_provider_enrollment_anniversary(int year, int month, int day, int endYear, int endMonth, int endDay, int utcHour)
    {
        var now = new DateTimeOffset(year, month, day, 20, 0, 0, TimeSpan.Zero);
        Assert.Equal(now, AutopaySchedule.StopAt(AutopayStopTiming.Immediately, now));
        Assert.Equal(new DateTimeOffset(endYear, endMonth, endDay, utcHour, 0, 0, TimeSpan.Zero), AutopaySchedule.StopAt(AutopayStopTiming.EndOfCurrentRentMonth, now));
        Assert.Throws<ArgumentException>(() => AutopaySchedule.StopAt((AutopayStopTiming)99, now));
    }
    [Fact]
    public void Stop_blocks_new_work_immediately_preserves_original_operation_and_waits_for_verified_unpaid_invoice_collection_mode()
    {
        var (account, binding, terms, now) = Fixture(); var authorization = AutopayAuthorization.Consent(Guid.NewGuid(), "payer", account, binding, AutopaySchedule.Select(terms, now, []), true, now);
        authorization.ApplyVerifiedMethod(binding.ProviderAccount, false, binding.CustomerId, "pm_saved", authorization.ConsentHash);
        authorization.RequireReview(); var operation = Guid.NewGuid(); authorization.RequestStop(operation, "payer", AutopayStopTiming.EndOfCurrentRentMonth, now.AddMinutes(1));
        var effective = authorization.StopEffectiveAt; var version = authorization.Version; authorization.RequestStop(operation, "payer", AutopayStopTiming.EndOfCurrentRentMonth, now.AddDays(1));
        Assert.Equal(effective, authorization.StopEffectiveAt); Assert.Equal(version, authorization.Version); Assert.False(authorization.AdmitsNewAutomaticWork);
        Assert.Throws<InvalidOperationException>(() => authorization.RequestStop(Guid.NewGuid(), "payer", AutopayStopTiming.Immediately, now.AddDays(1)));
        Assert.Throws<InvalidOperationException>(() => authorization.RequestStop(operation, "payer", AutopayStopTiming.Immediately, now.AddDays(1)));
        Assert.Throws<InvalidOperationException>(() => authorization.ApplyVerifiedMethod(binding.ProviderAccount, false, binding.CustomerId, "pm_other", authorization.ConsentHash));
        var proof = new VerifiedAutopayStop(binding.ProviderAccount, false, account.Id, binding.CustomerId, null, true, true, now.AddMinutes(2));
        Assert.Throws<InvalidOperationException>(() => authorization.VerifyStop(proof with { NoFurtherAutomaticInstructions = false }));
        Assert.Throws<InvalidOperationException>(() => authorization.VerifyStop(proof with { UnpaidInvoicesRetainedForManualPayment = false }));
        Assert.Throws<InvalidOperationException>(() => authorization.VerifyStop(proof with { CustomerId = "cus_foreign" }));
        Assert.Throws<InvalidOperationException>(() => authorization.VerifyStop(proof with { CheckedAt = now }));
        authorization.VerifyStop(proof); Assert.Equal(AutopayState.Stopped, authorization.State); Assert.False(authorization.AdmitsNewAutomaticWork);
        version = authorization.Version; authorization.VerifyStop(proof); Assert.Equal(version, authorization.Version);
        Assert.Throws<InvalidOperationException>(() => authorization.RequireReview());
    }
    [Fact]
    public void A_subscription_discovered_after_a_lost_create_response_can_be_stopped_without_reactivating_consent()
    {
        var (account, binding, terms, now) = Fixture(); var schedule = AutopaySchedule.Select(terms, now, []);
        var authorization = AutopayAuthorization.Consent(Guid.NewGuid(), "payer", account, binding, schedule, true, now);
        authorization.ApplyVerifiedMethod(binding.ProviderAccount, false, binding.CustomerId, "pm_saved", authorization.ConsentHash);
        var stop = Guid.NewGuid(); authorization.RequestStop(stop, "payer", AutopayStopTiming.Immediately, now.AddMinutes(1));
        var fact = new VerifiedAutopaySubscription(binding.ProviderAccount, false, account.Id, binding.CustomerId, "sub_lost_response", "pm_saved", authorization.ConsentHash,
            100000, "USD", 31, 17, schedule.FirstAnchorUtc, true, now.AddMinutes(2));
        Assert.Throws<InvalidOperationException>(() => authorization.ApplyVerifiedSubscription(fact));
        Assert.Throws<InvalidOperationException>(() => authorization.ObserveOriginalSubscriptionForStop(fact with { CustomerId = "cus_foreign" }));
        authorization.ObserveOriginalSubscriptionForStop(fact); Assert.Equal(AutopayState.StopPending, authorization.State); Assert.False(authorization.AdmitsNewAutomaticWork);
        Assert.Equal("sub_lost_response", authorization.SubscriptionId); Assert.NotEqual(authorization.SetupOperationKey, authorization.SubscriptionOperationKey);
        Assert.Contains(stop.ToString("N"), authorization.StopOperationKey!);
        authorization.VerifyStop(new(binding.ProviderAccount, false, account.Id, binding.CustomerId, "sub_lost_response", true, true, now.AddMinutes(3)));
        Assert.Equal(AutopayState.Stopped, authorization.State);
    }
    [Fact]
    public void A_bounded_authorization_cannot_renew_past_its_last_whole_month_or_omit_its_provider_cutoff()
    {
        var (account, binding, _, now) = Fixture(); var terms = RentAgreement.Create(account.Id, new Money(100000), 31, new(2026, 1, 1), new(2026, 12, 15), "manager", now);
        var schedule = AutopaySchedule.Select(terms, now, []); Assert.Equal(new DateOnly(2026, 12, 1), schedule.EndsBefore);
        var authorization = AutopayAuthorization.Consent(Guid.NewGuid(), "payer", account, binding, schedule, true, now);
        Assert.Equal(schedule.EndsBefore, authorization.EndsBefore); authorization.ApplyVerifiedMethod(binding.ProviderAccount, false, binding.CustomerId, "pm_saved", authorization.ConsentHash);
        var fact = new VerifiedAutopaySubscription(binding.ProviderAccount, false, account.Id, binding.CustomerId, "sub_bounded", "pm_saved", authorization.ConsentHash, 100000, "USD", 31, 17, schedule.FirstAnchorUtc, true, now.AddMinutes(1));
        Assert.Throws<InvalidOperationException>(() => authorization.ApplyVerifiedSubscription(fact));
        authorization.ApplyVerifiedSubscription(fact with { AutomaticCollectionEndsAt = schedule.AutomaticCollectionEndsAt });
        Assert.True(authorization.CanAdmitPeriod(new(2026, 11, 1), terms.Version, now)); Assert.False(authorization.CanAdmitPeriod(new(2026, 12, 1), terms.Version, now));
        Assert.False(authorization.CanAdmitPeriod(new(2026, 11, 1), terms.Version, schedule.AutomaticCollectionEndsAt!.Value));
    }
    private static RentAgreement Terms(int dueDay) => RentAgreement.Create(Guid.NewGuid(), new Money(100000), dueDay, new(2020, 1, 1), null, "manager", new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private static (RentAccount Account, BillingCustomerBinding Binding, RentAgreement Terms, DateTimeOffset Now) Fixture()
    {
        var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero); var account = RentAccount.Open(Guid.NewGuid(), Guid.NewGuid());
        return (account, BillingCustomerBinding.Create(account.Id, "acct_autopay", false, "cus_autopay", now), RentAgreement.Create(account.Id, new Money(100000), 31, new(2026, 1, 1), null, "manager", now), now);
    }
}
