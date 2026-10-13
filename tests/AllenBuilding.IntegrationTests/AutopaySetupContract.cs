using AllenBuilding.Application.Access;
using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AllenBuilding.IntegrationTests;

public static class AutopaySetupContract
{
    public static async Task VerifyAsync(Func<OfficeDbContext> create)
    {
        var clock = new Clock(new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero)); var profile = new CheckoutProfile("https://frozen-setup.example.test", "pmc_frozen_setup");
        async Task<Fixture> Seed(bool frozen = true)
        {
            var user = new OfficeUser { UserName = "setup-" + Guid.NewGuid().ToString("N"), EmailConfirmed = true }; var building = new Building { Name = "Setup contract" }; var suite = new Suite { BuildingId = building.Id, Name = "Setup suite" };
            var member = Membership.Grant(suite.Id, user.Id, clock.Now.AddDays(-1)); var account = RentAccount.Open(Guid.NewGuid(), member.Id);
            var binding = BillingCustomerBinding.Create(account.Id, "acct_setup_contract", false, "cus_setup_" + account.Id.ToString("N"), clock.Now);
            var terms = RentAgreement.Create(account.Id, new Money(100000), 1, new DateOnly(2026, 1, 1), null, user.Id, clock.Now);
            var auth = AutopayAuthorization.Consent(Guid.NewGuid(), user.Id, account, binding, AutopaySchedule.Select(terms, clock.Now, []), true, clock.Now); var work = AutopayWork.Enqueue(auth, AutopayWorkStage.Setup, clock.Now, frozen ? profile : null);
            await using var db = create(); db.AddRange(user, building, suite, member, account, binding, terms, auth, work, new AutopaySlot { AccountId = account.Id, AuthorizationId = auth.Id }, new ActiveGrant { MembershipId = member.Id, UserId = user.Id, SuiteId = suite.Id }); await db.SaveChangesAsync();
            return new(user, member, account, terms, auth, work, clock.Now);
        }
        async Task<AutopaySetupLease?> Claim() { await using var db = create(); return await new AutopaySetupQueue(db, clock).ClaimAsync(default); }
        async Task<bool> Complete(AutopaySetupLease lease, VerifiedAutopaySetup fact) { await using var db = create(); return await new AutopaySetupQueue(db, clock).CompleteAsync(lease, new(AutopaySetupOutcome.Verified, fact), default); }
        VerifiedAutopaySetup Fact(Fixture f, AutopaySetupState state = AutopaySetupState.Ready) => new(f.Auth.ProviderAccount, false, f.Account.Id, f.Auth.CustomerId, f.Auth.ConsentHash,
            "cs_setup_" + f.Auth.Id.ToString("N"), f.CreatedAt.AddHours(24), state, state == AutopaySetupState.Ready ? "pm_setup_" + f.Auth.Id.ToString("N") : null, true, state == AutopaySetupState.Ready, clock.Now);
        async Task Revoke(Fixture f)
        {
            await using var db = create(); (await db.Memberships.SingleAsync(x => x.Id == f.Member.Id)).Revoke(f.User.Id, "Access ended during setup", clock.Now); db.ActiveGrants.Remove(await db.ActiveGrants.SingleAsync(x => x.MembershipId == f.Member.Id)); await db.SaveChangesAsync();
        }
        async Task Verify(Fixture f, AutopayState state, CollectionWorkState workState, int activationCount)
        {
            await using var db = create(); Assert.Equal(state, (await db.AutopayAuthorizations.AsNoTracking().SingleAsync(x => x.Id == f.Auth.Id)).State);
            Assert.Equal(workState, (await db.AutopayWork.AsNoTracking().SingleAsync(x => x.Id == f.Work.Id)).State); Assert.Equal(activationCount, await db.AutopayWork.CountAsync(x => x.AuthorizationId == f.Auth.Id && x.Stage == AutopayWorkStage.Activate));
            Assert.False(await db.PaymentAttempts.AnyAsync(x => x.AccountId == f.Account.Id)); Assert.False(await db.SettlementPayments.AnyAsync(x => x.AccountId == f.Account.Id));
        }

        var normal = await Seed(); var first = (await Claim())!; Assert.NotNull(first); Assert.Equal(normal.Work.Id, first.Original.Id); Assert.False(first.ReadOnly); Assert.Equal(profile, first.Original.Profile); Assert.NotNull(first.Original.DispatchedAt);
        Assert.True(await Complete(first, Fact(normal, AutopaySetupState.Waiting))); await Verify(normal, AutopayState.AwaitingMethod, CollectionWorkState.Queued, 0);
        clock.Now = clock.Now.AddMinutes(1); var ready = (await Claim())!; Assert.True(ready.ReadOnly); Assert.Equal(first.Original.ProviderOperationKey, ready.Original.ProviderOperationKey);
        Assert.True(await Complete(ready, Fact(normal))); await Verify(normal, AutopayState.AwaitingProvider, CollectionWorkState.Completed, 1); Assert.False(await Complete(first, Fact(normal)));
        await using (var db = create())
        {
            var auth = await db.AutopayAuthorizations.SingleAsync(x => x.Id == normal.Auth.Id); Assert.Equal(Fact(normal).SessionId, auth.SetupSessionId); Assert.Equal(Fact(normal).PaymentMethodId, auth.PaymentMethodId); Assert.Null(auth.SubscriptionId); Assert.False(auth.AdmitsNewAutomaticWork);
            var activate = await db.AutopayWork.SingleAsync(x => x.AuthorizationId == auth.Id && x.Stage == AutopayWorkStage.Activate); Assert.Equal(profile, activate.Profile); Assert.Equal(auth.SubscriptionOperationKey, activate.ProviderOperationKey); Assert.Equal(auth.Version, activate.ReviewedAuthorizationVersion);
        }

        var reclaimed = await Seed(); var competing = await Task.WhenAll(Claim(), Claim()); Assert.Single(competing, x => x is not null); var stale = competing.Single(x => x is not null)!;
        clock.Now = clock.Now.AddMinutes(2); var winner = (await Claim())!; Assert.NotEqual(stale.Token, winner.Token); Assert.Equal(stale.Original.ProviderOperationKey, winner.Original.ProviderOperationKey);
        Assert.False(await Complete(stale, Fact(reclaimed))); Assert.True(await Complete(winner, Fact(reclaimed))); await Verify(reclaimed, AutopayState.AwaitingProvider, CollectionWorkState.Completed, 1);

        var saving = await Seed(); var saveLease = (await Claim())!; string connection; bool mysql;
        await using (var db = create()) { connection = db.Database.GetConnectionString()!; mysql = db is MySqlOfficeDbContext; }
        await using (var db = mysql ? (OfficeDbContext)new MySqlOfficeDbContext(new DbContextOptionsBuilder<MySqlOfficeDbContext>().UseMySQL(connection).AddInterceptors(new ExpireAfterSave(clock)).Options)
            : new SqliteOfficeDbContext(new DbContextOptionsBuilder<SqliteOfficeDbContext>().UseSqlite(connection).AddInterceptors(new ExpireAfterSave(clock)).Options))
            Assert.False(await new AutopaySetupQueue(db, clock).CompleteAsync(saveLease, new(AutopaySetupOutcome.Verified, Fact(saving)), default));
        await Verify(saving, AutopayState.AwaitingMethod, CollectionWorkState.Leased, 0);
        await using (var db = create()) Assert.Null((await db.AutopayAuthorizations.SingleAsync(x => x.Id == saving.Auth.Id)).SetupSessionId);
        var saveRetry = (await Claim())!; Assert.Equal(saveLease.Original.ProviderOperationKey, saveRetry.Original.ProviderOperationKey); Assert.True(await Complete(saveRetry, Fact(saving))); await Verify(saving, AutopayState.AwaitingProvider, CollectionWorkState.Completed, 1);

        var stopped = await Seed(); var late = (await Claim())!;
        await using (var db = create())
        {
            var actor = new Actor(stopped.User.Id, new HashSet<Guid>(), false, false); var current = new Fixed(actor); var policy = new AutopayPolicy(true, stopped.Auth.ProviderAccount, false, new("https://changed-config.example.test", "pmc_changed_config"));
            var actions = new AutopayPlanning(current, new AutopayStore(db, current, policy, new AccountInvitations(db, null!, current, new EphemeralDataProtectionProvider(), clock), clock), policy);
            Assert.Equal(BillingCode.Success, (await actions.StopAsync(new(Guid.NewGuid(), stopped.Auth.Id, stopped.Auth.Version, AutopayStopTiming.Immediately))).Code);
        }
        Assert.True(await Complete(late, Fact(stopped))); await Verify(stopped, AutopayState.StopPending, CollectionWorkState.Completed, 0);
        await using (var db = create()) { Assert.NotNull((await db.AutopayAuthorizations.SingleAsync(x => x.Id == stopped.Auth.Id)).SetupSessionId); Assert.Equal(1, await db.AutopayWork.CountAsync(x => x.AuthorizationId == stopped.Auth.Id && x.Stage == AutopayWorkStage.Stop)); Assert.Equal(profile, (await db.AutopayWork.SingleAsync(x => x.AuthorizationId == stopped.Auth.Id && x.Stage == AutopayWorkStage.Stop)).Profile); }

        var revoked = await Seed(); await Revoke(revoked); Assert.Null(await Claim()); await Verify(revoked, AutopayState.NeedsReview, CollectionWorkState.Suppressed, 0);
        await using (var db = create()) Assert.Null((await db.AutopayWork.SingleAsync(x => x.Id == revoked.Work.Id)).DispatchedAt);
        var revokedLate = await Seed(); var revokedLease = (await Claim())!; await Revoke(revokedLate); Assert.True(await Complete(revokedLease, Fact(revokedLate))); await Verify(revokedLate, AutopayState.NeedsReview, CollectionWorkState.Completed, 0);
        await using (var db = create()) Assert.Equal(Fact(revokedLate).SessionId, (await db.AutopayAuthorizations.SingleAsync(x => x.Id == revokedLate.Auth.Id)).SetupSessionId);

        var changed = await Seed(); var changedLease = (await Claim())!;
        await using (var db = create())
        {
            var terms = await db.RentAgreements.SingleAsync(x => x.Id == changed.Terms.Id); var replacement = RentAgreement.Create(changed.Account.Id, new Money(110000), 1, new DateOnly(2026, 12, 1), null, changed.User.Id, clock.Now); terms.Supersede(replacement); db.RentAgreements.Add(replacement); (await db.RentAccounts.SingleAsync(x => x.Id == changed.Account.Id)).TermsChanged(); await db.SaveChangesAsync();
        }
        Assert.True(await Complete(changedLease, Fact(changed))); await Verify(changed, AutopayState.NeedsReview, CollectionWorkState.Completed, 0);

        var adjusted = await Seed();
        await using (var db = create())
        {
            var calendar = RentCalendar.ForMonth(adjusted.Account.Id, adjusted.Auth.FirstPeriodStartsOn.Year, adjusted.Auth.FirstPeriodStartsOn.Month, 1); var p = BillingPeriod.Create(calendar); var debt = Obligation.Rent(p, adjusted.Terms, calendar, adjusted.User.Id, clock.Now);
            var credit = ObligationCredit.RecordLocal(Guid.NewGuid(), adjusted.User.Id, new(debt.Id, adjusted.Account.Id, debt.Version, 100000), 1, ObligationCreditKind.Credit, "Adjustment after consent", clock.Now); db.AddRange(p, debt, credit); await db.SaveChangesAsync();
        }
        Assert.Null(await Claim()); await Verify(adjusted, AutopayState.NeedsReview, CollectionWorkState.Suppressed, 0);

        var invalid = await Seed(); var invalidLease = (await Claim())!; Assert.True(await Complete(invalidLease, Fact(invalid) with { CustomerId = "cus_foreign" })); await Verify(invalid, AutopayState.NeedsReview, CollectionWorkState.NeedsReview, 0);
        await using (var db = create()) Assert.Null((await db.AutopayAuthorizations.SingleAsync(x => x.Id == invalid.Auth.Id)).PaymentMethodId);

        var missing = await Seed(false); Assert.Null(await Claim()); await Verify(missing, AutopayState.NeedsReview, CollectionWorkState.NeedsReview, 0);
        await using (var db = create()) { var work = await db.AutopayWork.SingleAsync(x => x.Id == missing.Work.Id); Assert.Null(work.DispatchedAt); Assert.Equal("OriginalProfileMissing", work.LastError); }

        var uncertain = await Seed(); var unknown = (await Claim())!;
        await using (var db = create()) Assert.True(await new AutopaySetupQueue(db, clock).CompleteAsync(unknown, new(AutopaySetupOutcome.Unknown), default));
        clock.Now = clock.Now.AddHours(23); var readOnly = (await Claim())!; Assert.True(readOnly.ReadOnly); Assert.Null(readOnly.Authorization.SetupSessionId); Assert.Equal(unknown.Original.DispatchedAt, readOnly.Original.DispatchedAt); Assert.Equal(unknown.Original.ProviderOperationKey, readOnly.Original.ProviderOperationKey);
        await using (var db = create()) Assert.True(await new AutopaySetupQueue(db, clock).CompleteAsync(readOnly, new(AutopaySetupOutcome.Invalid), default));
        await Verify(uncertain, AutopayState.NeedsReview, CollectionWorkState.NeedsReview, 0);
    }
    private sealed record Fixture(OfficeUser User, Membership Member, RentAccount Account, RentAgreement Terms, AutopayAuthorization Auth, AutopayWork Work, DateTimeOffset CreatedAt);
    private sealed class Fixed(Actor actor) : ICurrentActor { public Task<Actor?> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult<Actor?>(actor); }
    private sealed class Clock(DateTimeOffset now) : TimeProvider { public DateTimeOffset Now { get; set; } = now; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class ExpireAfterSave(Clock clock) : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        { if (eventData.Context!.ChangeTracker.Entries<AutopayWork>().Any(x => x.Entity.Stage == AutopayWorkStage.Setup && x.Entity.State == CollectionWorkState.Completed)) clock.Now = clock.Now.AddMinutes(3); return ValueTask.FromResult(result); }
    }
}
