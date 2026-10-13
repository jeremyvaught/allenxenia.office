using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.IntegrationTests;

public static class CollectionWorkContract
{
    public static async Task VerifyAsync(Func<OfficeDbContext> create)
    {
        // Earlier than other contract jobs: the worker remains global, while the fixture is isolated by time.
        var clock = new WorkClock();
        async Task<(PaymentAttempt Attempt, Membership Membership, Obligation Debt)> Seed()
        {
            var user = new OfficeUser { UserName = "work-" + Guid.NewGuid().ToString("N"), EmailConfirmed = true };
            var building = new Building { Name = "Worker contract" }; var suite = new Suite { BuildingId = building.Id, Name = "Worker suite" };
            var membership = Membership.Grant(suite.Id, user.Id, clock.Now.AddDays(-1)); var account = RentAccount.Open(Guid.NewGuid(), membership.Id);
            var binding = BillingCustomerBinding.Create(account.Id, "acct_work", false, "cus_" + Guid.NewGuid().ToString("N"), clock.Now);
            var debt = Obligation.Issue(account.Id, new Money(1000), "Worker debt", DateOnly.FromDateTime(clock.Now.UtcDateTime), user.Id, clock.Now);
            var attempt = PaymentAttempt.Admit(Guid.NewGuid(), user.Id, binding, [new(debt.Id, account.Id, debt.Version, 1000)], clock.Now);
            await using var db = create(); db.AddRange(user, building, suite, membership, account, binding, debt, attempt,
                new ActiveGrant { UserId = user.Id, SuiteId = suite.Id, MembershipId = membership.Id },
                new CollectionGuard { AttemptId = attempt.Id, AccountId = account.Id, ObligationId = debt.Id },
                CollectionWork.Enqueue(attempt, new("https://original.example.test", "pmc_original")));
            await db.SaveChangesAsync(); return (attempt, membership, debt);
        }
        async Task Revoke(Membership membership)
        {
            await using var db = create(); await using var tx = await AuthorityLocks.BeginAsync(db); await AuthorityLocks.MembershipAsync(db, membership.Id);
            var row = await db.Memberships.SingleAsync(x => x.Id == membership.Id); row.Revoke(row.UserId, "Access ended", clock.Now);
            db.ActiveGrants.Remove(await db.ActiveGrants.SingleAsync(x => x.MembershipId == row.Id)); await db.SaveChangesAsync(); await tx.CommitAsync();
        }
        async Task<CollectionLease?> Claim()
        { await using var db = create(); return await new CollectionWorkQueue(db, clock).ClaimAsync(CancellationToken.None); }
        async Task<bool> Complete(CollectionLease lease, ProviderCollectionObservation observation)
        { await using var db = create(); return await new CollectionWorkQueue(db, clock).CompleteAsync(lease, observation, CancellationToken.None); }
        static ProviderCollectionObservation Paid(PaymentAttempt attempt)
        {
            var fact = new CanonicalSettlement(attempt.ProviderAccount, attempt.LiveMode, attempt.CustomerId, "cs_" + attempt.Id.ToString("N"), "pi_" + attempt.Id.ToString("N"), attempt.TotalMinorUnits, attempt.Currency);
            return new(ProviderCollectionState.Paid, fact.SessionId, Settlement: fact);
        }
        var first = await Seed();
        var contenders = await Task.WhenAll(Task.Run(Claim), Task.Run(Claim)); Assert.Single(contenders, x => x is not null);
        var lease = contenders.Single(x => x is not null)!; Assert.Equal(first.Attempt.Id, lease.Original.Id); Assert.Equal(PaymentAttemptState.Dispatching, lease.Original.State);
        Assert.Equal("https://original.example.test", lease.Profile.PublicOrigin); Assert.Equal("pmc_original", lease.Profile.PaymentMethodConfiguration);
        await Revoke(first.Membership); Assert.True(await Complete(lease, Paid(lease.Original)));
        await using (var db = create())
        {
            Assert.Equal(1, await db.SettlementPayments.CountAsync(x => x.AttemptId == first.Attempt.Id));
            Assert.Equal(CollectionWorkState.Completed, (await db.CollectionWork.SingleAsync(x => x.AttemptId == first.Attempt.Id)).State);
        }
        var fenced = await Seed(); var stale = (await Claim())!; clock.Now = clock.Now.AddMinutes(2);
        var fresh = (await Claim())!; Assert.NotEqual(stale.Token, fresh.Token); Assert.False(await Complete(stale, Paid(stale.Original)));
        await using (var db = create()) { Assert.False(await db.SettlementPayments.AnyAsync(x => x.AttemptId == fenced.Attempt.Id)); Assert.True(await db.CollectionGuards.AnyAsync(x => x.AttemptId == fenced.Attempt.Id)); }
        Assert.True(await Complete(fresh, Paid(fresh.Original)));
        var ended = await Seed(); await Revoke(ended.Membership); Assert.Null(await Claim());
        await using (var db = create())
        {
            Assert.Equal(PaymentAttemptState.Uncollected, (await db.PaymentAttempts.SingleAsync(x => x.Id == ended.Attempt.Id)).State);
            Assert.Equal(CollectionWorkState.Suppressed, (await db.CollectionWork.SingleAsync(x => x.AttemptId == ended.Attempt.Id)).State);
            Assert.False(await db.CollectionGuards.AnyAsync(x => x.AttemptId == ended.Attempt.Id)); Assert.False(await db.FinancialExceptions.AnyAsync(x => x.AttemptId == ended.Attempt.Id));
        }
        var changed = await Seed();
        await using (var db = create()) { var debt = await db.Obligations.SingleAsync(x => x.Id == changed.Debt.Id); debt.FinancialStateChanged(); await db.SaveChangesAsync(); }
        Assert.Null(await Claim());
        await using (var db = create()) { Assert.Equal(CollectionWorkState.NeedsReview, (await db.CollectionWork.SingleAsync(x => x.AttemptId == changed.Attempt.Id)).State); Assert.True(await db.FinancialExceptions.AnyAsync(x => x.AttemptId == changed.Attempt.Id)); }
        var rollback = await Seed(); var rollbackLease = (await Claim())!;
        await using (var db = create())
        {
            db.SavingChanges += (_, _) => { foreach (var entry in db.ChangeTracker.Entries<PaymentAllocation>().Where(x => x.State == EntityState.Added)) entry.Property(x => x.AmountMinorUnits).CurrentValue = 0; };
            Assert.True(await new CollectionWorkQueue(db, clock).CompleteAsync(rollbackLease, Paid(rollbackLease.Original), CancellationToken.None));
        }
        await using (var db = create())
        {
            Assert.False(await db.SettlementPayments.AnyAsync(x => x.AttemptId == rollback.Attempt.Id)); Assert.Equal(rollback.Debt.Version, (await db.Obligations.SingleAsync(x => x.Id == rollback.Debt.Id)).Version);
            Assert.Equal(CollectionWorkState.Queued, (await db.CollectionWork.SingleAsync(x => x.AttemptId == rollback.Attempt.Id)).State);
        }
        clock.Now = clock.Now.AddMinutes(1); var repaired = (await Claim())!; Assert.True(await Complete(repaired, Paid(repaired.Original)));
        var uncertain = await Seed();
        for (var index = 0; index < 5; index++)
        {
            var retry = (await Claim())!; Assert.Equal(uncertain.Attempt.Id, retry.Original.Id); Assert.True(await Complete(retry, new(ProviderCollectionState.Unknown)));
            clock.Now = clock.Now.AddMinutes(10);
        }
        await using (var db = create())
        {
            Assert.Equal(5, (await db.CollectionWork.SingleAsync(x => x.AttemptId == uncertain.Attempt.Id)).Failures);
            Assert.Equal(CollectionWorkState.NeedsReview, (await db.CollectionWork.SingleAsync(x => x.AttemptId == uncertain.Attempt.Id)).State);
            Assert.True(await db.CollectionGuards.AnyAsync(x => x.AttemptId == uncertain.Attempt.Id)); Assert.True(await db.FinancialExceptions.AnyAsync(x => x.AttemptId == uncertain.Attempt.Id));
        }
        var old = await Seed(); clock.Now = clock.Now.AddHours(23); Assert.Null(await Claim());
        await using (var db = create()) { Assert.Equal(PaymentAttemptState.Uncollected, (await db.PaymentAttempts.SingleAsync(x => x.Id == old.Attempt.Id)).State); }
        var lost = await Seed(); var lostLease = (await Claim())!; clock.Now = clock.Now.AddHours(23); Assert.Null(await Claim());
        Assert.False(await Complete(lostLease, Paid(lostLease.Original)));
        await using (var db = create()) { Assert.Equal(CollectionWorkState.NeedsReview, (await db.CollectionWork.SingleAsync(x => x.AttemptId == lost.Attempt.Id)).State); Assert.True(await db.CollectionGuards.AnyAsync(x => x.AttemptId == lost.Attempt.Id)); }
        var outside = await Seed();
        await using (var db = create())
        {
            var factory = new CallbackFactory(async original => { Assert.Null(db.Database.CurrentTransaction); await Revoke(outside.Membership); return Paid(original); });
            Assert.True(await new CollectionWorker(new CollectionWorkQueue(db, clock), factory).ProcessOneAsync()); Assert.Equal(1, factory.Creates); Assert.Equal(0, factory.Reads);
        }
        await using (var db = create()) { Assert.True(await db.SettlementPayments.AnyAsync(x => x.AttemptId == outside.Attempt.Id)); }
        var polling = await Seed(); var handoff = (await Claim())!;
        Assert.True(await Complete(handoff, new(ProviderCollectionState.Open, "cs_" + handoff.Original.Id.ToString("N"), new Uri("https://checkout.stripe.com/c/pay/synthetic"), clock.Now.AddHours(1))));
        clock.Now = clock.Now.AddMinutes(1);
        await using (var db = create())
        {
            var factory = new CallbackFactory(original => Task.FromResult(Paid(original)));
            Assert.True(await new CollectionWorker(new CollectionWorkQueue(db, clock), factory).ProcessOneAsync()); Assert.Equal(0, factory.Creates); Assert.Equal(1, factory.Reads);
        }
        await using (var db = create()) { Assert.Equal(1, await db.SettlementPayments.CountAsync(x => x.AttemptId == polling.Attempt.Id)); }
    }
    private sealed class WorkClock : TimeProvider
    { public DateTimeOffset Now { get; set; } = new(2020, 1, 1, 12, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class CallbackFactory(Func<PaymentAttempt, Task<ProviderCollectionObservation>> callback) : ICollectionGatewayFactory, ICollectionGateway
    {
        public int Creates { get; private set; }
        public int Reads { get; private set; }
        public ICollectionGateway Open(PaymentAttempt original, CheckoutProfile profile) { Assert.Equal("pmc_original", profile.PaymentMethodConfiguration); return this; }
        public Task<ProviderCollectionObservation> CreateAsync(PaymentAttempt original, CancellationToken cancellationToken = default) { Creates++; return callback(original); }
        public Task<ProviderCollectionObservation> ReadAsync(PaymentAttempt original, string sessionId, CancellationToken cancellationToken = default) { Reads++; Assert.Equal(original.SessionId, sessionId); return callback(original); }
        public Task<ProviderCollectionObservation> ExpireAsync(PaymentAttempt original, string sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
