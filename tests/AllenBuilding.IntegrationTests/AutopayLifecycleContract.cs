using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AllenBuilding.IntegrationTests;

public static class AutopayLifecycleContract
{
    public static async Task VerifyAsync(Func<OfficeDbContext> create)
    {
        foreach (var scenario in new[] { "activate", "competing", "reclaim", "wrong-proof", "future-proof", "five-failures", "missing-profile", "stop-before", "revoke-before", "stop-during", "revoke-during", "unknown-revoked", "expired-write-window", "save-expiry", "no-subscription-stop", "uncertain-no-subscription", "known-stop", "pending-stop" })
        {
            var clock = new Clock(); var now = clock.Now;
            var user = new OfficeUser { UserName = "lifecycle-" + Guid.NewGuid().ToString("N"), EmailConfirmed = true };
            var building = new Building { Name = "Lifecycle fence" }; var suite = new Suite { BuildingId = building.Id, Name = "Lifecycle original suite" };
            var member = Membership.Grant(suite.Id, user.Id, now.AddDays(-1)); var account = RentAccount.Open(Guid.NewGuid(), member.Id);
            var binding = BillingCustomerBinding.Create(account.Id, "acct_lifecycle", false, "cus_" + account.Id.ToString("N"), now);
            var terms = RentAgreement.Create(account.Id, new Money(100000), 31, new(2026, 1, 1), null, user.Id, now);
            var auth = AutopayAuthorization.Consent(Guid.NewGuid(), user.Id, account, binding, AutopaySchedule.Select(terms, now, []), true, now);
            var profile = new CheckoutProfile("https://lifecycle.example.test", "pmc_lifecycle");
            var setup = AutopayWork.Enqueue(auth, AutopayWorkStage.Setup, now, profile); setup.Claim(now); setup.BeginDispatch(now); setup.Complete(setup.LeaseToken, CollectionWorkOutcome.Finished, now);
            auth.ObserveVerifiedSetup(new(binding.ProviderAccount, false, account.Id, binding.CustomerId, auth.ConsentHash, "cs_" + auth.Id.ToString("N"), now.AddHours(24), AutopaySetupState.Ready, "pm_" + auth.Id.ToString("N"), true, true, now), true);
            var activate = AutopayWork.Enqueue(auth, AutopayWorkStage.Activate, now, scenario == "missing-profile" ? null : profile);
            await using (var seed = create()) { seed.AddRange(user, building, suite, member, account, binding, terms, auth, setup, activate, new AutopaySlot { AccountId = account.Id, AuthorizationId = auth.Id }, new ActiveGrant { MembershipId = member.Id, SuiteId = suite.Id, UserId = user.Id }); await seed.SaveChangesAsync(); }
            VerifiedAutopaySubscription Fact() => new(binding.ProviderAccount, false, account.Id, binding.CustomerId, "sub_" + auth.Id.ToString("N"), auth.PaymentMethodId!, auth.ConsentHash, 100000, "USD", 31, 17, auth.FirstAnchorUtc, true, clock.Now);
            async Task<AutopayLifecycleLease?> Claim() { await using var db = create(); return await new AutopayLifecycleQueue(db, clock).ClaimAsync(default); }
            async Task<bool> Complete(AutopayLifecycleLease lease, AutopayLifecycleObservation observation) { await using var db = create(); return await new AutopayLifecycleQueue(db, clock).CompleteAsync(lease, observation, default); }
            async Task Stop(AutopayStopTiming timing = AutopayStopTiming.Immediately)
            {
                clock.Now = clock.Now.AddSeconds(1);
                await using var db = create(); var original = await db.AutopayAuthorizations.SingleAsync(x => x.Id == auth.Id); original.RequestStop(Guid.NewGuid(), user.Id, timing, clock.Now);
                db.AutopayWork.Add(AutopayWork.Enqueue(original, AutopayWorkStage.Stop, clock.Now, profile)); await db.SaveChangesAsync();
            }
            async Task Revoke() { await using var db = create(); (await db.Memberships.SingleAsync(x => x.Id == member.Id)).Revoke(user.Id, "Original access revoked", clock.Now); db.ActiveGrants.Remove(await db.ActiveGrants.SingleAsync(x => x.MembershipId == member.Id)); await db.SaveChangesAsync(); }
            if (scenario is "stop-before" or "no-subscription-stop") await Stop();
            if (scenario == "revoke-before") await Revoke();
            if (scenario is "stop-before" or "revoke-before" or "missing-profile")
            {
                Assert.Null(await Claim()); await using var db = create(); Assert.Equal(scenario == "missing-profile" ? CollectionWorkState.NeedsReview : CollectionWorkState.Suppressed, (await db.AutopayWork.SingleAsync(x => x.Id == activate.Id)).State);
                Assert.Null((await db.AutopayAuthorizations.SingleAsync(x => x.Id == auth.Id)).SubscriptionId);
            }
            else if (scenario == "no-subscription-stop")
            {
                Assert.Null(await Claim()); var stop = (await Claim())!; Assert.Equal(AutopayWorkStage.Stop, stop.Original.Stage);
                Assert.True(await Complete(stop, new(AutopayLifecycleOutcome.Verified, Stop: new(binding.ProviderAccount, false, account.Id, binding.CustomerId, null, true, true, clock.Now))));
                await using var db = create(); Assert.Equal(AutopayState.Stopped, (await db.AutopayAuthorizations.SingleAsync(x => x.Id == auth.Id)).State);
            }
            else
            {
                AutopayLifecycleLease first;
                if (scenario == "competing") { var claims = await Task.WhenAll(Claim(), Claim()); first = Assert.Single(claims.OfType<AutopayLifecycleLease>()); }
                else first = (await Claim())!;
                Assert.NotNull(first); Assert.Equal(activate.Id, first.Original.Id); Assert.Equal(auth.SubscriptionOperationKey, first.Original.ProviderOperationKey); Assert.Equal(profile, first.Original.Profile);
                if (scenario is "reclaim" or "expired-write-window")
                {
                    clock.Now = clock.Now.Add(scenario == "reclaim" ? TimeSpan.FromMinutes(2) : TimeSpan.FromHours(23)); var next = (await Claim())!;
                    Assert.NotEqual(first.Token, next.Token); Assert.False(await Complete(first, new(AutopayLifecycleOutcome.Verified, Fact())));
                    Assert.Equal(scenario == "expired-write-window", next.ReadOnly); Assert.Equal(first.Original.ProviderOperationKey, next.Original.ProviderOperationKey); first = next;
                }
                if (scenario is "stop-during" or "uncertain-no-subscription") await Stop();
                if (scenario is "revoke-during" or "unknown-revoked") await Revoke();
                var fact = Fact(); if (scenario == "wrong-proof") fact = fact with { MonthlyMinorUnits = 99900 };
                // Simulate completion later than the verified response: cleanup must retain its earlier original reference.
                if (scenario == "revoke-during") clock.Now = clock.Now.AddSeconds(1);
                if (scenario == "future-proof") fact = fact with { CheckedAt = clock.Now.AddSeconds(1) };
                if (scenario == "five-failures")
                {
                    for (var i = 0; i < 5; i++)
                    {
                        Assert.True(await Complete(first, new(AutopayLifecycleOutcome.Unknown)));
                        if (i < 4) { clock.Now = clock.Now.AddSeconds(30 * (1 << i)); first = (await Claim())!; Assert.Equal(activate.Id, first.Original.Id); }
                    }
                    await using var check = create(); var saved = await check.AutopayAuthorizations.SingleAsync(x => x.Id == auth.Id); Assert.Equal(AutopayState.StopPending, saved.State); Assert.Null(saved.StopActorId);
                    Assert.Equal(CollectionWorkState.NeedsReview, (await check.AutopayWork.SingleAsync(x => x.Id == activate.Id)).State); Assert.Equal(1, await check.AutopayWork.CountAsync(x => x.AuthorizationId == auth.Id && x.Stage == AutopayWorkStage.Stop));
                }
                else if (scenario == "save-expiry")
                {
                    string connection; bool mysql; await using (var db = create()) { connection = db.Database.GetConnectionString()!; mysql = db is MySqlOfficeDbContext; }
                    await using var expiring = mysql ? (OfficeDbContext)new MySqlOfficeDbContext(new DbContextOptionsBuilder<MySqlOfficeDbContext>().UseMySQL(connection).AddInterceptors(new ExpireAfterSave(clock)).Options)
                        : new SqliteOfficeDbContext(new DbContextOptionsBuilder<SqliteOfficeDbContext>().UseSqlite(connection).AddInterceptors(new ExpireAfterSave(clock)).Options);
                    Assert.False(await new AutopayLifecycleQueue(expiring, clock).CompleteAsync(first, new(AutopayLifecycleOutcome.Verified, fact), default));
                    await using var check = create(); Assert.Equal(AutopayState.AwaitingProvider, (await check.AutopayAuthorizations.SingleAsync(x => x.Id == auth.Id)).State); Assert.Null((await check.AutopayAuthorizations.SingleAsync(x => x.Id == auth.Id)).SubscriptionId);
                    Assert.Equal(CollectionWorkState.Leased, (await check.AutopayWork.SingleAsync(x => x.Id == activate.Id)).State);
                }
                else
                {
                    var observation = scenario is "unknown-revoked" or "uncertain-no-subscription" ? new AutopayLifecycleObservation(AutopayLifecycleOutcome.Unknown) : new(AutopayLifecycleOutcome.Verified, fact);
                    Assert.True(await Complete(first, observation), scenario); Assert.False(await Complete(first, observation));
                    await using var check = create(); var saved = await check.AutopayAuthorizations.SingleAsync(x => x.Id == auth.Id);
                    if (scenario is "wrong-proof" or "future-proof") { Assert.Equal(AutopayState.StopPending, saved.State); Assert.Null(saved.SubscriptionId); Assert.Null(saved.StopActorId); }
                    else if (scenario is "stop-during" or "revoke-during" or "unknown-revoked" or "uncertain-no-subscription")
                    {
                        Assert.Equal(AutopayState.StopPending, saved.State); Assert.False(saved.AdmitsNewAutomaticWork);
                        Assert.Equal(1, await check.AutopayWork.CountAsync(x => x.AuthorizationId == auth.Id && x.Stage == AutopayWorkStage.Stop));
                        if (scenario is "stop-during" or "revoke-during") Assert.Equal(fact.SubscriptionId, saved.SubscriptionId); else Assert.Null(saved.SubscriptionId);
                    }
                    else Assert.Equal(AutopayState.Active, saved.State);
                }
                if (scenario is "uncertain-no-subscription" or "known-stop" or "pending-stop")
                {
                    if (scenario is "known-stop" or "pending-stop") await Stop(scenario == "pending-stop" ? AutopayStopTiming.EndOfCurrentRentMonth : AutopayStopTiming.Immediately);
                    // The activation retry is not due yet; the stop work can claim independently.
                    var stop = (await Claim())!; Assert.Equal(AutopayWorkStage.Stop, stop.Original.Stage);
                    var proof = new VerifiedAutopayStop(binding.ProviderAccount, false, account.Id, binding.CustomerId, scenario == "known-stop" ? Fact().SubscriptionId : null, true, true, clock.Now);
                    Assert.True(await Complete(stop, scenario == "pending-stop" ? new(AutopayLifecycleOutcome.Pending, Fact()) : new(AutopayLifecycleOutcome.Verified, Stop: proof)));
                    await using var check = create(); Assert.Equal(AutopayState.StopPending, (await check.AutopayAuthorizations.SingleAsync(x => x.Id == auth.Id)).State); Assert.Equal(scenario == "pending-stop" ? CollectionWorkState.Queued : CollectionWorkState.NeedsReview, (await check.AutopayWork.SingleAsync(x => x.Id == stop.Original.Id)).State);
                    if (scenario == "pending-stop") { clock.Now = clock.Now.AddHours(23); var pending = (await Claim())!; Assert.True(pending.ReadOnly); Assert.Equal(stop.Original.ProviderOperationKey, pending.Original.ProviderOperationKey); }
                }
            }
            // No scenario creates debt/cash. Keep isolated fixture leftovers from becoming the next scenario's candidate.
            await using (var cleanup = create())
            {
                Assert.False(await cleanup.PaymentAttempts.AnyAsync(x => x.AccountId == account.Id)); Assert.False(await cleanup.Obligations.AnyAsync(x => x.AccountId == account.Id)); Assert.False(await cleanup.SettlementPayments.AnyAsync(x => x.AccountId == account.Id));
                foreach (var pending in await cleanup.AutopayWork.Where(x => x.AuthorizationId == auth.Id && x.State != CollectionWorkState.Completed && x.State != CollectionWorkState.Suppressed && x.State != CollectionWorkState.NeedsReview).ToListAsync()) pending.Review("CanonicalReviewRequired");
                await cleanup.SaveChangesAsync();
            }
        }
    }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now { get; set; } = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class ExpireAfterSave(Clock clock) : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default) { clock.Now = clock.Now.AddMinutes(2); return ValueTask.FromResult(result); }
    }
}
