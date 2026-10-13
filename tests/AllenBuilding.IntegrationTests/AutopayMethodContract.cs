using AllenBuilding.Application.Access;
using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.IntegrationTests;

public static class AutopayMethodContract
{
    public static async Task VerifyAsync(Func<OfficeDbContext> create)
    {
        foreach (var scenario in new[] { "valid", "revoke-original", "stop", "terms", "account-version", "work-version", "new-credit", "locked", "expiry", "wrong-host", "wrong-session", "wrong-expiry", "provider-error", "identity" })
        {
            var clock = new Clock(); var now = clock.Now; var policy = new AutopayPolicy(true, "acct_method_contract", false);
            var user = new OfficeUser { UserName = "method-" + Guid.NewGuid().ToString("N"), EmailConfirmed = true };
            var other = new OfficeUser { UserName = "method-other-" + Guid.NewGuid().ToString("N"), EmailConfirmed = true };
            var building = new Building { Name = "Method handoff" }; var suite = new Suite { BuildingId = building.Id, Name = "Original method suite" };
            var separateSuite = new Suite { BuildingId = building.Id, Name = "Other active tenancy" };
            var member = Membership.Grant(suite.Id, user.Id, now.AddDays(-1)); var separate = Membership.Grant(separateSuite.Id, user.Id, now.AddDays(-1));
            var account = RentAccount.Open(Guid.NewGuid(), member.Id); var binding = BillingCustomerBinding.Create(account.Id, policy.ProviderAccount, false, "cus_" + account.Id.ToString("N"), now);
            var terms = RentAgreement.Create(account.Id, new Money(100000), 1, new(2026, 1, 1), null, user.Id, now);
            var auth = AutopayAuthorization.Consent(Guid.NewGuid(), user.Id, account, binding, AutopaySchedule.Select(terms, now, []), true, now);
            var work = AutopayWork.Enqueue(auth, AutopayWorkStage.Setup, now, new("https://method.example.test", "pmc_method")); work.Claim(now); work.BeginDispatch(now);
            var session = "cs_" + auth.Id.ToString("N");
            auth.ObserveVerifiedSetup(new(policy.ProviderAccount, false, account.Id, binding.CustomerId, auth.ConsentHash, session, now.AddHours(24), AutopaySetupState.Waiting, null, true, false, now), false);
            work.Complete(work.LeaseToken, CollectionWorkOutcome.Waiting, now);
            await using (var seed = create())
            {
                seed.AddRange(user, other, building, suite, separateSuite, member, separate, account, binding, terms, auth, work,
                new AutopaySlot { AccountId = account.Id, AuthorizationId = auth.Id }, new ActiveGrant { MembershipId = member.Id, SuiteId = suite.Id, UserId = user.Id },
                new ActiveGrant { MembershipId = separate.Id, SuiteId = separateSuite.Id, UserId = user.Id }); await seed.SaveChangesAsync();
            }
            var actor = new Actor(user.Id, new HashSet<Guid>(), false, false); var current = new Current(actor);
            await using var db = create(); var audit = new AccountInvitations(db, null!, current, new EphemeralDataProtectionProvider(), clock);
            var store = new AutopayStore(db, current, policy, audit, clock);
            var before = await store.ReadAsync(actor, auth.Id, default); Assert.NotNull(before); Assert.Equal(session, before.Authorization.SetupSessionId);
            Assert.Null(await store.ReadAsync(actor with { UserId = other.Id, ManagedBuildings = new HashSet<Guid> { building.Id }, ChallengedMfa = true }, auth.Id, default));
            current.Actor = actor with { ManagedBuildings = new HashSet<Guid> { building.Id }, ChallengedMfa = false };
            Assert.Null(await store.ReadAsync(current.Actor, auth.Id, default)); current.Actor = actor;
            var probe = new Probe(new(session, now.AddHours(24), new("https://checkout.stripe.com/c/setup/synthetic")), async () =>
            {
                Assert.Null(db.Database.CurrentTransaction); // Provider port is invoked after the snapshot transaction ends.
                if (scenario == "expiry") { clock.Now = clock.Now.AddHours(24); return; }
                if (scenario == "wrong-host") { return; }
                if (scenario == "identity") { current.Actor = null; return; }
                await using var changed = create();
                if (scenario == "revoke-original") { var m = await changed.Memberships.SingleAsync(x => x.Id == member.Id); m.Revoke(user.Id, "Original grant ended", now); changed.ActiveGrants.Remove(await changed.ActiveGrants.SingleAsync(x => x.MembershipId == member.Id)); }
                if (scenario == "stop") (await changed.AutopayAuthorizations.SingleAsync(x => x.Id == auth.Id)).RequestStop(Guid.NewGuid(), user.Id, AutopayStopTiming.Immediately, now);
                if (scenario == "terms") changed.Entry(await changed.RentAgreements.SingleAsync(x => x.Id == terms.Id)).Property(x => x.Version).CurrentValue = Guid.NewGuid();
                if (scenario == "account-version") (await changed.RentAccounts.SingleAsync(x => x.Id == account.Id)).AuthorizationChanged();
                if (scenario == "work-version") { var item = await changed.AutopayWork.SingleAsync(x => x.Id == work.Id); Assert.True(item.Claim(now.AddMinutes(1))); }
                if (scenario == "locked") (await changed.Users.SingleAsync(x => x.Id == user.Id)).LockoutEnd = now.AddHours(1);
                if (scenario == "new-credit")
                {
                    var cal = RentCalendar.ForMonth(account.Id, 2026, 11, 1); var period = BillingPeriod.Create(cal); var debt = Obligation.Rent(period, terms, cal, user.Id, now);
                    changed.AddRange(period, debt, ObligationCredit.RecordLocal(Guid.NewGuid(), user.Id, new(debt.Id, account.Id, debt.Version, 100000), 200, ObligationCreditKind.Credit, "Adjustment arrives during method read", now));
                }
                await changed.SaveChangesAsync();
            });
            if (scenario == "wrong-host") probe.Handoff = probe.Handoff with { Destination = new("https://checkout.stripe.com.attacker.test/") };
            if (scenario == "wrong-session") probe.Handoff = probe.Handoff with { SessionId = "cs_other" };
            if (scenario == "wrong-expiry") probe.Handoff = probe.Handoff with { ExpiresAt = now.AddHours(1) };
            probe.Throw = scenario == "provider-error";
            var result = await new AutopayMethodHandoffs(current, store, policy, clock, probe).ContinueAsync(auth.Id);
            if (scenario == "valid") Assert.Equal(probe.Handoff.Destination, result); else Assert.Null(result);
            Assert.Equal(1, probe.Reads); Assert.True(probe.Disposed);
            await using var check = create(); Assert.Null((await check.AutopayAuthorizations.SingleAsync(x => x.Id == auth.Id)).SubscriptionId);
            Assert.False(await check.SettlementPayments.AnyAsync(x => x.AccountId == account.Id)); Assert.False(await check.PaymentAttempts.AnyAsync(x => x.AccountId == account.Id));
            if (scenario == "revoke-original") Assert.True(await check.ActiveGrants.AnyAsync(x => x.MembershipId == separate.Id));
        }
    }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now { get; set; } = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Current(Actor? actor) : ICurrentActor { public Actor? Actor { get; set; } = actor; public Task<Actor?> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(Actor); }
    private sealed class Probe(AutopayMethodHandoff handoff, Func<Task> change) : IAutopayMethodGatewayFactory, IAutopayMethodGateway, IDisposable
    {
        public AutopayMethodHandoff Handoff { get; set; } = handoff;
        public bool Throw { get; set; }
        public bool Disposed { get; private set; }
        public int Reads { get; private set; }
        public IAutopayMethodGateway Open(AutopayMethodSnapshot original) => this;
        public async Task<AutopayMethodHandoff?> ReadAsync(AutopayMethodSnapshot original, CancellationToken cancellationToken) { Reads++; await change(); if (Throw) throw new HttpRequestException("Synthetic unavailable provider"); return Handoff; }
        public void Dispose() => Disposed = true;
    }
}
