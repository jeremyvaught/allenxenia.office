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

public static class BillingContract
{
    public static async Task VerifyAsync(Func<OfficeDbContext> create)
    {
        var services = new ServiceCollection(); services.AddLogging();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider()); services.AddSingleton(TimeProvider.System);
        services.AddScoped<OfficeDbContext>(_ => create()); services.AddIdentityCore<OfficeUser>().AddEntityFrameworkStores<OfficeDbContext>().AddDefaultTokenProviders();
        services.AddScoped<TestActor>(); services.AddScoped<ICurrentActor>(sp => sp.GetRequiredService<TestActor>());
        services.AddScoped<AccountInvitations>(); services.AddScoped<IBillingStore, BillingStore>(); services.AddScoped<BillingActions>();
        await using var provider = services.BuildServiceProvider();
        OfficeUser User(string prefix) => new() { UserName = $"billing-{prefix}-{Guid.NewGuid():N}" };
        var payer = User("payer"); var peerUser = User("peer"); var managerUser = User("manager"); var secondUser = User("second");
        var building = new Building { Name = "Billing building" }; var other = new Building { Name = "Other billing building" };
        var suite = new Suite { BuildingId = building.Id, Name = "Rent suite" };
        var membership = Membership.Grant(suite.Id, payer.Id, DateTimeOffset.UtcNow.AddYears(-1));
        var peerMembership = Membership.Grant(suite.Id, peerUser.Id, DateTimeOffset.UtcNow.AddYears(-1));
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            db.AddRange(payer, peerUser, managerUser, secondUser, building, other, suite, membership, peerMembership,
                new BuildingManager { UserId = managerUser.Id, BuildingId = building.Id }, new BuildingManager { UserId = secondUser.Id, BuildingId = building.Id },
                new ActiveGrant { UserId = payer.Id, SuiteId = suite.Id, MembershipId = membership.Id },
                new ActiveGrant { UserId = peerUser.Id, SuiteId = suite.Id, MembershipId = peerMembership.Id });
            await db.SaveChangesAsync();
        }
        var manager = new Actor(managerUser.Id, new HashSet<Guid> { building.Id }, true, true);
        var second = new Actor(secondUser.Id, new HashSet<Guid> { building.Id }, true, true);
        var tenant = new Actor(payer.Id, new HashSet<Guid>(), false, false);
        var peer = new Actor(peerUser.Id, new HashSet<Guid>(), false, false);
        async Task<T> As<T>(Actor actor, Func<BillingActions, Task<T>> call)
        {
            using var scope = provider.CreateScope(); scope.ServiceProvider.GetRequiredService<TestActor>().Actor = actor;
            return await call(scope.ServiceProvider.GetRequiredService<BillingActions>());
        }
        var open = new OpenRentAccount(Guid.NewGuid(), Guid.NewGuid(), membership.Id);
        Assert.Equal(BillingCode.Unavailable, (await As(tenant, a => a.OpenAsync(open))).Code);
        Assert.Equal(BillingCode.Unavailable, (await As(manager with { RecentMfa = false }, a => a.OpenAsync(open))).Code);
        var opened = await As(manager, a => a.OpenAsync(open)); Assert.Equal(BillingCode.Success, opened.Code);
        Assert.Equal(opened, await As(manager, a => a.OpenAsync(open)));
        Assert.Equal(BillingCode.Conflict, (await As(manager, a => a.OpenAsync(open with { OperationId = Guid.NewGuid(), AccountId = Guid.NewGuid() }))).Code);
        Assert.Empty(await As(peer, a => a.ReadAsync(false))); Assert.Empty(await As(manager, a => a.ReadAsync(false)));
        Assert.False((await As(tenant, a => a.ReadAsync(false))).Single().CollectionAvailable);
        var initial = new SetRentTerms(Guid.NewGuid(), open.AccountId, opened.AccountVersion!.Value, 100_000, 1, new DateOnly(2026, 2, 18));
        var terms = await As(manager, a => a.TermsAsync(initial)); Assert.Equal(BillingCode.Success, terms.Code);
        Assert.Equal(terms, await As(manager, a => a.TermsAsync(initial)));
        Assert.Equal(BillingCode.Conflict, (await As(manager, a => a.TermsAsync(initial with { AmountMinorUnits = 110_000 }))).Code);
        Assert.Equal(BillingCode.NoFullPeriod, (await As(manager, a => a.RentAsync(new(Guid.NewGuid(), open.AccountId, terms.AccountVersion!.Value, 2026, 2)))).Code);
        var march = new IssueRent(Guid.NewGuid(), open.AccountId, terms.AccountVersion!.Value, 2026, 3);
        var concurrent = await Task.WhenAll(
            Task.Run(() => As(manager, a => a.RentAsync(march))),
            Task.Run(() => As(second, a => a.RentAsync(march with { OperationId = Guid.NewGuid() }))));
        Assert.All(concurrent, x => Assert.Equal(BillingCode.Success, x.Code)); Assert.Equal(concurrent[0].EntityId, concurrent[1].EntityId);
        Assert.Equal(concurrent[0], await As(manager, a => a.RentAsync(march)));
        var opening = new IssueCharge(Guid.NewGuid(), open.AccountId, terms.AccountVersion!.Value, 7_000, "Opening February charge", new DateOnly(2026, 2, 18));
        Assert.Equal(BillingCode.Success, (await As(manager, a => a.ChargeAsync(opening))).Code);
        Assert.Equal(BillingCode.Success, (await As(manager, a => a.ChargeAsync(opening with { OperationId = Guid.NewGuid(), Description = "Separate approved charge" }))).Code);
        var replacement = new SetRentTerms(Guid.NewGuid(), open.AccountId, terms.AccountVersion.Value, 120_000, 31, new DateOnly(2026, 3, 1), ReplacesId: terms.EntityId);
        var changed = await As(manager, a => a.TermsAsync(replacement)); Assert.Equal(BillingCode.Success, changed.Code);
        var reused = await As(manager, a => a.RentAsync(march with { OperationId = Guid.NewGuid(), Version = changed.AccountVersion!.Value }));
        Assert.Equal(concurrent[0].EntityId, reused.EntityId);
        Assert.Equal(BillingCode.Conflict, (await As(manager, a => a.ChargeAsync(opening with { OperationId = Guid.NewGuid() }))).Code);
        Assert.Equal(BillingCode.Success, (await As(manager, a => a.RentAsync(new(Guid.NewGuid(), open.AccountId, changed.AccountVersion!.Value, 2026, 4)))).Code);
        var view = (await As(tenant, a => a.ReadAsync(false))).Single();
        Assert.Equal(4, view.Obligations.Count);
        var marchView = view.Obligations.Single(x => x.PeriodStartsOn == new DateOnly(2026, 3, 1));
        Assert.Equal(100_000, marchView.IssuedMinorUnits); Assert.Equal(new DateOnly(2026, 3, 1), marchView.DueOn);
        Assert.Equal(new DateOnly(2026, 4, 30), view.Obligations.Single(x => x.PeriodStartsOn == new DateOnly(2026, 4, 1)).DueOn);
        var future = replacement with { OperationId = Guid.NewGuid(), Version = changed.AccountVersion!.Value, StartsOn = new DateOnly(2026, 6, 1), ReplacesId = changed.EntityId, AmountMinorUnits = 150_000 };
        var competingTerms = await Task.WhenAll(
            Task.Run(() => As(manager, a => a.TermsAsync(future))),
            Task.Run(() => As(second, a => a.TermsAsync(future with { OperationId = Guid.NewGuid(), AmountMinorUnits = 160_000 }))));
        Assert.Single(competingTerms, x => x.Code == BillingCode.Success); Assert.Single(competingTerms, x => x.Code == BillingCode.Conflict);
        view = (await As(manager, a => a.ReadAsync(true))).Single();
        Assert.Equal(3, view.Terms.Count);
        var july = await As(manager, a => a.RentAsync(new(Guid.NewGuid(), open.AccountId, view.Version, 2026, 7))); Assert.Equal(BillingCode.Success, july.Code);
        // A long-lived scope must not admit a charge using a tracked, stale terms version.
        using (var staleScope = provider.CreateScope())
        {
            staleScope.ServiceProvider.GetRequiredService<TestActor>().Actor = manager;
            var staleDb = staleScope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            var tracked = await staleDb.RentAccounts.SingleAsync(x => x.Id == open.AccountId);
            var staleVersion = tracked.Version;
            var latest = view.Terms.Single(x => x.StartsOn == new DateOnly(2026, 6, 1));
            Assert.Equal(BillingCode.Success, (await As(second, a => a.TermsAsync(new(Guid.NewGuid(), open.AccountId, view.Version, 170_000, 1, new DateOnly(2026, 9, 1), ReplacesId: latest.Id)))).Code);
            var staleCharge = await staleScope.ServiceProvider.GetRequiredService<BillingActions>().ChargeAsync(opening with { OperationId = Guid.NewGuid(), Version = staleVersion });
            Assert.Equal(BillingCode.Conflict, staleCharge.Code);
        }
        view = (await As(manager, a => a.ReadAsync(true))).Single();
        var septemberTerms = view.Terms.Single(x => x.StartsOn == new DateOnly(2026, 9, 1));
        var octoberReplacement = new SetRentTerms(Guid.NewGuid(), open.AccountId, view.Version, 180_000, 1, new DateOnly(2026, 10, 1), ReplacesId: septemberTerms.Id);
        var octoberSchedule = new IssueRent(Guid.NewGuid(), open.AccountId, view.Version, 2026, 10);
        var race = await Task.WhenAll(
            Task.Run(() => As(manager, a => a.TermsAsync(octoberReplacement))),
            Task.Run(() => As(second, a => a.RentAsync(octoberSchedule))));
        Assert.Equal(BillingCode.Success, race[0].Code);
        Assert.Contains(race[1].Code, new[] { BillingCode.Success, BillingCode.Conflict });
        view = (await As(manager, a => a.ReadAsync(true))).Single();
        var octoberIssued = await As(manager, a => a.RentAsync(octoberSchedule with { OperationId = Guid.NewGuid(), Version = view.Version }));
        Assert.Equal(BillingCode.Success, octoberIssued.Code);
        if (race[1].Code == BillingCode.Success) Assert.Equal(race[1].EntityId, octoberIssued.EntityId);
        var october = (await As(tenant, a => a.ReadAsync(false))).Single().Obligations.Single(x => x.PeriodStartsOn == new DateOnly(2026, 10, 1));
        Assert.Contains(october.IssuedMinorUnits, new long[] { 170_000, 180_000 });
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            Assert.Equal(1, await db.BillingPeriods.CountAsync(x => x.AccountId == open.AccountId && x.StartsOn == new DateOnly(2026, 3, 1)));
            var original = await db.RentAgreements.SingleAsync(x => x.Id == terms.EntityId);
            Assert.Equal(100_000, original.AmountMinorUnits); Assert.Null(original.OriginalEndsOn);
            var grant = await db.Memberships.SingleAsync(x => x.Id == membership.Id); grant.Revoke(managerUser.Id, "Access only revoked", DateTimeOffset.UtcNow);
            db.ActiveGrants.Remove(await db.ActiveGrants.SingleAsync(x => x.MembershipId == membership.Id)); await db.SaveChangesAsync();
        }
        Assert.Empty(await As(tenant, a => a.ReadAsync(false)));
        // Revoking portal access does not stop valid calendar debt or erase history.
        Assert.Equal(BillingCode.Success, (await As(manager, a => a.RentAsync(new(Guid.NewGuid(), open.AccountId, view.Version, 2026, 8)))).Code);
        Guid returningId;
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            var returning = Membership.Grant(suite.Id, payer.Id, DateTimeOffset.UtcNow.AddMinutes(-1)); returningId = returning.Id;
            db.AddRange(returning, new ActiveGrant { UserId = payer.Id, SuiteId = suite.Id, MembershipId = returning.Id }); await db.SaveChangesAsync();
        }
        var newAccount = await As(manager, a => a.OpenAsync(new(Guid.NewGuid(), Guid.NewGuid(), returningId)));
        Assert.Equal(BillingCode.Success, newAccount.Code);
        var returningView = (await As(tenant, a => a.ReadAsync(false))).Single(); Assert.Equal(newAccount.AccountId, returningView.Id); Assert.Empty(returningView.Obligations);
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            var calendar = RentCalendar.ForMonth(open.AccountId, 2026, 3, 1);
            db.BillingPeriods.Add(BillingPeriod.Create(calendar)); await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            var period = await db.BillingPeriods.SingleAsync(x => x.AccountId == open.AccountId && x.StartsOn == new DateOnly(2026, 3, 1));
            var agreement = await db.RentAgreements.SingleAsync(x => x.Id == changed.EntityId);
            var calendar = RentCalendar.ForMonth(open.AccountId, 2026, 3, 31);
            db.Obligations.Add(Obligation.Rent(period, agreement, calendar, managerUser.Id, DateTimeOffset.UtcNow));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            var unused = BillingPeriod.Create(RentCalendar.ForMonth(open.AccountId, 2027, 1, 1));
            db.BillingPeriods.Add(unused); await db.SaveChangesAsync();
            var mismatched = Obligation.Issue(newAccount.AccountId!.Value, new Money(5_000), "Invalid account allocation", new DateOnly(2027, 1, 1), managerUser.Id, DateTimeOffset.UtcNow);
            db.Obligations.Add(mismatched); db.Entry(mismatched).Property(x => x.PeriodId).CurrentValue = unused.Id;
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }
}
