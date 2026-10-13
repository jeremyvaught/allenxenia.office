using AllenBuilding.Application.Access;
using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AllenBuilding.IntegrationTests;

public static class CollectionAdmissionContract
{
    public static async Task VerifyAsync(Func<OfficeDbContext> create)
    {
        var now = DateTimeOffset.UtcNow;
        var payer = new OfficeUser { UserName = "collect-" + Guid.NewGuid().ToString("N") };
        var peer = new OfficeUser { UserName = "collect-peer-" + Guid.NewGuid().ToString("N") };
        var building = new Building { Name = "Collection contract" };
        var suite = new Suite { BuildingId = building.Id, Name = "Collection suite" };
        var membership = Membership.Grant(suite.Id, payer.Id, now.AddYears(-1));
        var peerMembership = Membership.Grant(suite.Id, peer.Id, now.AddYears(-1));
        var account = RentAccount.Open(Guid.NewGuid(), membership.Id);
        var otherAccount = RentAccount.Open(Guid.NewGuid(), peerMembership.Id);
        var binding = BillingCustomerBinding.Create(account.Id, "acct_admission", false, "cus_" + Guid.NewGuid().ToString("N"), now);
        Obligation Charge(string description, Guid? target = null) => Obligation.Issue(target ?? account.Id, new Money(1000), description, new DateOnly(2026, 10, 1), payer.Id, now);
        var small = Charge("Carry remainder"); var full = Charge("Current charge"); var stale = Charge("Stale review");
        var fresh = Charge("Concurrent admission"); var race = Charge("Revocation race"); var foreign = Charge("Other payer", otherAccount.Id);
        // Seed a previously verified allocation to exercise a real net balance below the provider minimum.
        var previous = PaymentAttempt.Admit(Guid.NewGuid(), payer.Id, binding, [new(small.Id, account.Id, small.Version, 975)], now);
        previous.BeginDispatch(now);
        var capture = SettlementPayment.Record(previous, new("acct_admission", false, binding.CustomerId, "cs_" + previous.Id.ToString("N"), "pi_" + previous.Id.ToString("N"), 975, "USD"), now);
        small.FinancialStateChanged();
        await using (var db = create())
        {
            db.AddRange(payer, peer, building, suite, membership, peerMembership, account, otherAccount, binding, small, full, stale, fresh, race, foreign, previous, capture,
                new ActiveGrant { UserId = payer.Id, SuiteId = suite.Id, MembershipId = membership.Id },
                new ActiveGrant { UserId = peer.Id, SuiteId = suite.Id, MembershipId = peerMembership.Id });
            await db.SaveChangesAsync();
        }
        var services = new ServiceCollection(); services.AddLogging();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider()); services.AddSingleton(TimeProvider.System);
        services.AddScoped<OfficeDbContext>(_ => create());
        services.AddIdentityCore<OfficeUser>().AddEntityFrameworkStores<OfficeDbContext>().AddDefaultTokenProviders();
        services.AddSingleton(new CollectionPolicy(true, "acct_admission", false));
        services.AddScoped<TestActor>(); services.AddScoped<ICurrentActor>(sp => sp.GetRequiredService<TestActor>());
        services.AddScoped<AccountInvitations>(); services.AddScoped<ICollectionStore, CollectionStore>(); services.AddScoped<CollectionActions>();
        await using var provider = services.BuildServiceProvider();
        var tenant = new Actor(payer.Id, new HashSet<Guid>(), false, false);
        var roommate = new Actor(peer.Id, new HashSet<Guid>(), false, false);
        async Task<T> As<T>(Actor actor, Func<CollectionActions, Task<T>> action)
        {
            using var scope = provider.CreateScope(); scope.ServiceProvider.GetRequiredService<TestActor>().Actor = actor;
            return await action(scope.ServiceProvider.GetRequiredService<CollectionActions>());
        }
        Task<IReadOnlyList<ReviewedCollectionBalance>> Review(params Guid[] ids) => As(tenant, x => x.ReviewAsync(account.Id, ids));
        Task<CollectionResult> Admit(CollectReviewedBalances command) => As(tenant, x => x.AdmitAsync(command));
        Assert.Empty(await As(roommate, x => x.ReviewAsync(account.Id, new[] { full.Id })));
        Assert.Empty(await As(tenant with { ManagedBuildings = new HashSet<Guid> { building.Id } }, x => x.ReviewAsync(account.Id, new[] { full.Id })));
        var review = await Review(small.Id, full.Id); Assert.Equal(2, review.Count);
        Assert.Equal(25, review.Single(x => x.ObligationId == small.Id).RemainingMinorUnits);
        Assert.Equal(CollectionCode.ValidationFailed, (await Admit(new(Guid.NewGuid(), account.Id, review.Where(x => x.ObligationId == small.Id).ToArray()))).Code);
        Assert.Equal(CollectionCode.Conflict, (await Admit(new(Guid.NewGuid(), account.Id, new[] { review[0] with { RemainingMinorUnits = 500 } }))).Code);
        Assert.Equal(CollectionCode.ValidationFailed, (await Admit(new(Guid.NewGuid(), account.Id, new[] { review[0], review[0] }))).Code);
        var foreignReview = new ReviewedCollectionBalance(foreign.Id, account.Id, foreign.Version, 1000);
        Assert.Equal(CollectionCode.Conflict, (await Admit(new(Guid.NewGuid(), account.Id, new[] { foreignReview }))).Code);
        var command = new CollectReviewedBalances(Guid.NewGuid(), account.Id, review);
        Assert.Equal(CollectionCode.Unavailable, (await As(roommate, x => x.AdmitAsync(command))).Code);
        var admitted = await Admit(command); Assert.Equal(CollectionCode.Success, admitted.Code);
        Assert.Equal(PaymentAttemptState.Admitted, admitted.State);
        Assert.Equal(admitted, await Admit(command with { Items = review.Reverse().ToArray() }));
        Assert.Equal(CollectionCode.Conflict, (await Admit(command with { Items = new[] { review[0] } })).Code);
        Assert.All(await Review(small.Id, full.Id), x => Assert.True(x.HasProviderInstruction));
        Assert.Equal(CollectionCode.Conflict, (await Admit(command with { OperationId = Guid.NewGuid() })).Code);
        var staleReview = await Review(stale.Id);
        await using (var db = create()) { var row = await db.Obligations.SingleAsync(x => x.Id == stale.Id); row.FinancialStateChanged(); await db.SaveChangesAsync(); }
        Assert.Equal(CollectionCode.Conflict, (await Admit(new(Guid.NewGuid(), account.Id, staleReview))).Code);
        var concurrent = new CollectReviewedBalances(Guid.NewGuid(), account.Id, await Review(fresh.Id));
        var results = await Task.WhenAll(Task.Run(() => Admit(concurrent)), Task.Run(() => Admit(concurrent with { OperationId = Guid.NewGuid() })));
        Assert.Single(results, x => x.Code == CollectionCode.Success); Assert.Single(results, x => x.Code == CollectionCode.Conflict);
        // A long-lived context cannot reuse a tracked obligation after a concurrent ledger change.
        using (var scope = provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<TestActor>().Actor = tenant;
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            var tracked = await db.Obligations.SingleAsync(x => x.Id == race.Id);
            var old = new ReviewedCollectionBalance(tracked.Id, account.Id, tracked.Version, 1000);
            await using (var writer = create()) { var changed = await writer.Obligations.SingleAsync(x => x.Id == race.Id); changed.FinancialStateChanged(); await writer.SaveChangesAsync(); }
            Assert.Equal(CollectionCode.Conflict, (await scope.ServiceProvider.GetRequiredService<CollectionActions>().AdmitAsync(new(Guid.NewGuid(), account.Id, new[] { old }))).Code);
        }
        var raceCommand = new CollectReviewedBalances(Guid.NewGuid(), account.Id, await Review(race.Id));
        async Task Revoke()
        {
            await using var db = create(); await using var transaction = await AuthorityLocks.BeginAsync(db);
            await AuthorityLocks.MembershipAsync(db, membership.Id);
            var row = await db.Memberships.SingleAsync(x => x.Id == membership.Id);
            row.Revoke("synthetic-manager", "Access ended", DateTimeOffset.UtcNow);
            db.ActiveGrants.Remove(await db.ActiveGrants.SingleAsync(x => x.MembershipId == membership.Id));
            await db.SaveChangesAsync(); await transaction.CommitAsync();
        }
        var admission = Task.Run(() => Admit(raceCommand)); var revocation = Task.Run(Revoke);
        await Task.WhenAll(admission, revocation);
        Assert.Contains(admission.Result.Code, new[] { CollectionCode.Success, CollectionCode.Unavailable });
        Assert.Empty(await Review(full.Id));
        Assert.Equal(CollectionCode.Unavailable, (await Admit(command)).Code);
        await using (var db = create())
        {
            var returning = Membership.Grant(suite.Id, payer.Id, DateTimeOffset.UtcNow.AddMinutes(-1));
            db.AddRange(returning, new ActiveGrant { UserId = payer.Id, SuiteId = suite.Id, MembershipId = returning.Id }); await db.SaveChangesAsync();
        }
        Assert.Empty(await Review(full.Id)); Assert.Equal(CollectionCode.Unavailable, (await Admit(command)).Code);
        await using (var db = create())
        {
            var saved = await db.PaymentAttempts.Include(x => x.Items).SingleAsync(x => x.Id == admitted.AttemptId);
            Assert.Equal(1025, saved.TotalMinorUnits); Assert.Equal(2, saved.Items.Count);
            Assert.Equal(2, await db.CollectionGuards.CountAsync(x => x.AttemptId == saved.Id));
            Assert.Equal(0, await db.PaymentAttempts.CountAsync(x => x.AccountId == otherAccount.Id));
            Assert.False(await db.CollectionGuards.AnyAsync(x => x.ObligationId == stale.Id));
        }
    }
}
