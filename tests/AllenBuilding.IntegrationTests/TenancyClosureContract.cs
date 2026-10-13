using AllenBuilding.Application.Access;
using AllenBuilding.Application.Billing;
using AllenBuilding.Application.Maintenance;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace AllenBuilding.IntegrationTests;

public static class TenancyClosureContract
{
    public static async Task VerifyAsync(Func<OfficeDbContext> create)
    {
        var now = new DateTimeOffset(2026, 10, 13, 5, 0, 0, TimeSpan.Zero);
        var manager = new OfficeUser { UserName = "closure-manager-" + Guid.NewGuid().ToString("N"), EmailConfirmed = true };
        var payer = new OfficeUser { UserName = "closure-payer-" + Guid.NewGuid().ToString("N"), EmailConfirmed = true };
        var peer = new OfficeUser { UserName = "closure-peer-" + Guid.NewGuid().ToString("N"), EmailConfirmed = true };
        var building = new Building { Name = "Closure building" }; var foreign = new Building { Name = "Foreign closure building" };
        var suite = new Suite { BuildingId = building.Id, Name = "Closing suite" }; var secondSuite = new Suite { BuildingId = building.Id, Name = "Retained suite" };
        var member = Membership.Grant(suite.Id, payer.Id, now.AddYears(-1)); var retained = Membership.Grant(secondSuite.Id, payer.Id, now.AddYears(-1)); var accessOnly = Membership.Grant(suite.Id, peer.Id, now.AddYears(-1));
        var account = RentAccount.Open(Guid.NewGuid(), member.Id); var other = RentAccount.Open(Guid.NewGuid(), retained.Id); var accessAccount = RentAccount.Open(Guid.NewGuid(), accessOnly.Id);
        var binding = BillingCustomerBinding.Create(account.Id, "acct_closure", false, "cus_closure", now);
        var obligation = Obligation.Issue(account.Id, new Money(1000), "Debt retained after closing", new DateOnly(2026, 1, 1), manager.Id, now);
        var original = PaymentAttempt.Admit(Guid.NewGuid(), payer.Id, binding, [new(obligation.Id, account.Id, obligation.Version, 1000)], now); original.BeginDispatch(now);
        var guard = new CollectionGuard { AccountId = account.Id, ObligationId = obligation.Id, AttemptId = original.Id };
        await using (var db = create())
        {
            db.AddRange(manager, payer, peer, building, foreign, suite, secondSuite, member, retained, accessOnly, account, other, accessAccount, binding, obligation, original, guard,
                new BuildingManager { BuildingId = building.Id, UserId = manager.Id },
                new ActiveGrant { UserId = payer.Id, SuiteId = suite.Id, MembershipId = member.Id },
                new ActiveGrant { UserId = payer.Id, SuiteId = secondSuite.Id, MembershipId = retained.Id },
                new ActiveGrant { UserId = peer.Id, SuiteId = suite.Id, MembershipId = accessOnly.Id }); await db.SaveChangesAsync();
        }
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<TimeProvider>(new Clock(now)); services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddScoped<OfficeDbContext>(_ => create()); services.AddScoped<TestActor>(); services.AddScoped<ICurrentActor>(sp => sp.GetRequiredService<TestActor>());
        services.AddIdentityCore<OfficeUser>().AddEntityFrameworkStores<OfficeDbContext>().AddDefaultTokenProviders();
        services.AddScoped<AccountInvitations>(); services.AddScoped<IBillingStore, BillingStore>(); services.AddScoped<BillingActions>(); services.AddScoped<AccessRevocation>();
        await using var provider = services.BuildServiceProvider(); var actor = new Actor(manager.Id, new HashSet<Guid> { building.Id }, true, true);
        async Task<BillingResult> End(EndTenancy command, Actor? selected = null)
        {
            using var scope = provider.CreateScope(); scope.ServiceProvider.GetRequiredService<TestActor>().Actor = selected ?? actor;
            return await scope.ServiceProvider.GetRequiredService<BillingActions>().EndAsync(command);
        }
        var command = new EndTenancy(Guid.NewGuid(), account.Id, account.Version, member.Version, "<script>closure-reason()</script>");
        // A final audit/persistence failure rolls back access removal, account end and cleanup case together.
        string connection; bool mysql; await using (var db = create()) { connection = db.Database.GetConnectionString()!; mysql = db is MySqlOfficeDbContext; }
        await using (var db = mysql ? (OfficeDbContext)new MySqlOfficeDbContext(new DbContextOptionsBuilder<MySqlOfficeDbContext>().UseMySQL(connection).AddInterceptors(new FailEnd()).Options)
            : new SqliteOfficeDbContext(new DbContextOptionsBuilder<SqliteOfficeDbContext>().UseSqlite(connection).AddInterceptors(new FailEnd()).Options))
        {
            var fixedActor = new Fixed(actor); var store = new BillingStore(db, fixedActor, new AccountInvitations(db, null!, fixedActor, new EphemeralDataProtectionProvider(), new Clock(now)), new Clock(now));
            await Assert.ThrowsAsync<InvalidOperationException>(() => new BillingActions(fixedActor, store, new Clock(now)).EndAsync(new(Guid.NewGuid(), account.Id, account.Version, member.Version, "Fail final persistence")));
        }
        await using (var db = create()) { Assert.Null((await db.RentAccounts.SingleAsync(x => x.Id == account.Id)).EndedAt); Assert.False(await db.TenancyClosures.AnyAsync(x => x.AccountId == account.Id)); Assert.Null((await db.Memberships.SingleAsync(x => x.Id == member.Id)).RevokedAt); Assert.True(await db.ActiveGrants.AnyAsync(x => x.MembershipId == member.Id)); }
        Assert.Equal(BillingCode.Unavailable, (await End(command, actor with { RecentMfa = false })).Code);
        Assert.Equal(BillingCode.Unavailable, (await End(command, actor with { ManagedBuildings = new HashSet<Guid> { foreign.Id } })).Code);
        Assert.Equal(BillingCode.Unavailable, (await End(command, new Actor(payer.Id, new HashSet<Guid>(), false, false))).Code);
        Assert.Equal(BillingCode.ValidationFailed, (await End(command with { Reason = "\0" })).Code);
        Assert.Equal(BillingCode.Conflict, (await End(command with { Version = Guid.NewGuid() })).Code); Assert.Equal(BillingCode.Conflict, (await End(command with { MembershipVersion = Guid.NewGuid() })).Code);
        var result = await End(command); Assert.Equal(BillingCode.Success, result.Code); Assert.Equal(result, await End(command));
        Assert.Equal(BillingCode.Conflict, (await End(command with { Reason = "Different replay" })).Code);
        Assert.Equal(BillingCode.Conflict, (await End(command with { OperationId = Guid.NewGuid() })).Code);
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            var closed = await db.RentAccounts.SingleAsync(x => x.Id == account.Id); Assert.Equal(now, closed.EndedAt);
            var revoked = await db.Memberships.SingleAsync(x => x.Id == member.Id); Assert.Equal(now, revoked.RevokedAt); Assert.False(await db.ActiveGrants.AnyAsync(x => x.MembershipId == member.Id));
            Assert.True(await db.ActiveGrants.AnyAsync(x => x.MembershipId == retained.Id)); Assert.Null((await db.RentAccounts.SingleAsync(x => x.Id == other.Id)).EndedAt);
            var closure = await db.TenancyClosures.SingleAsync(x => x.AccountId == account.Id); Assert.Equal(BillingClosureState.Pending, closure.State); Assert.Equal(command.OperationId, closure.OperationId);
            Assert.Equal(command.Version, closure.ReviewedAccountVersion); Assert.Equal(command.MembershipVersion, closure.ReviewedMembershipVersion);
            Assert.Equal(1000, (await db.Obligations.SingleAsync(x => x.Id == obligation.Id)).AmountMinorUnits); Assert.Equal(PaymentAttemptState.Dispatching, (await db.PaymentAttempts.SingleAsync(x => x.Id == original.Id)).State);
            Assert.True(await db.CollectionGuards.AnyAsync(x => x.ObligationId == obligation.Id)); Assert.Equal(binding.CustomerId, (await db.BillingCustomers.SingleAsync(x => x.AccountId == account.Id)).CustomerId);
            Assert.Equal(1, await db.AccountAudits.CountAsync(x => x.Action == "TenancyAccessEnded" && x.SubjectId == member.Id.ToString("D")));
            Assert.Equal(1, await db.AccountAudits.CountAsync(x => x.Action == "BillingClosurePending" && x.SubjectId == account.Id.ToString("D")));
            var current = scope.ServiceProvider.GetRequiredService<TestActor>(); current.Actor = actor; var actions = scope.ServiceProvider.GetRequiredService<BillingActions>();
            Assert.Contains(await actions.ClosuresAsync(), x => x.AccountId == account.Id && x.State == BillingClosureState.Pending);
            Assert.Equal(BillingCode.Conflict, (await actions.ChargeAsync(new(Guid.NewGuid(), account.Id, closed.Version, 500, "Ordinary charge blocked", new DateOnly(2026, 12, 1)))).Code);
            Assert.Equal(BillingCode.Conflict, (await actions.TermsAsync(new(Guid.NewGuid(), account.Id, closed.Version, 1000, 1, new DateOnly(2026, 11, 1)))).Code);
            Assert.Equal(BillingCode.Conflict, (await actions.RentAsync(new(Guid.NewGuid(), account.Id, closed.Version, 2026, 12))).Code);
            current.Actor = new Actor(payer.Id, new HashSet<Guid>(), false, false); var visible = await actions.ReadAsync(false); Assert.Contains(visible, x => x.Id == other.Id); Assert.DoesNotContain(visible, x => x.Id == account.Id);
            Assert.Empty(await actions.ClosuresAsync());
            current.Actor = actor; Assert.Equal(MaintenanceCode.Success, await scope.ServiceProvider.GetRequiredService<AccessRevocation>().RevokeAsync(accessOnly.Id, accessOnly.Version, "Access only", default));
            Assert.Null((await db.RentAccounts.SingleAsync(x => x.Id == accessAccount.Id)).EndedAt); Assert.False(await db.TenancyClosures.AnyAsync(x => x.AccountId == accessAccount.Id));
        }
        // Independent requests serialize on membership/account and cannot create two closure cases.
        var competing = new EndTenancy(Guid.NewGuid(), other.Id, other.Version, retained.Version, "Competing end");
        var outcomes = await Task.WhenAll(End(competing), End(competing with { OperationId = Guid.NewGuid() })); Assert.Single(outcomes, x => x.Code == BillingCode.Success); Assert.Single(outcomes, x => x.Code == BillingCode.Conflict);

    }
    private sealed class Fixed(Actor actor) : ICurrentActor { public Task<Actor?> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult<Actor?>(actor); }
    private sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class FailEnd : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { if (eventData.Context!.ChangeTracker.Entries<TenancyClosure>().Any(x => x.State == EntityState.Added)) throw new InvalidOperationException("Synthetic closure audit failure"); return ValueTask.FromResult(result); }
    }
}
