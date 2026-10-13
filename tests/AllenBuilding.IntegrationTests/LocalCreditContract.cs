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

public static class LocalCreditContract
{
    public static async Task VerifyAsync(Func<OfficeDbContext> create)
    {
        var clock = new Clock(); var policy = new LocalCreditPolicy(true);
        var collection = new CollectionPolicy(true, "acct_credit", false, new("https://credits.example.test", "pmc_credit"));
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider()); services.AddScoped<OfficeDbContext>(_ => create());
        services.AddIdentityCore<OfficeUser>().AddEntityFrameworkStores<OfficeDbContext>().AddDefaultTokenProviders(); services.AddScoped<AccountInvitations>();
        services.AddScoped<TestActor>(); services.AddScoped<ICurrentActor>(sp => sp.GetRequiredService<TestActor>());
        services.AddSingleton(policy); services.AddScoped<ILocalCreditStore, LocalCreditStore>(); services.AddScoped<LocalCreditActions>();
        services.AddSingleton(collection); services.AddScoped<ICollectionStore, CollectionStore>(); services.AddScoped<CollectionActions>();
        services.AddScoped<IBillingStore, BillingStore>(); services.AddScoped<BillingActions>();
        await using var provider = services.BuildServiceProvider();
        var user = new OfficeUser { UserName = "credit-payer-" + Guid.NewGuid().ToString("N"), EmailConfirmed = true };
        var manager = new OfficeUser { UserName = "credit-manager-" + Guid.NewGuid().ToString("N") };
        var second = new OfficeUser { UserName = "credit-second-" + Guid.NewGuid().ToString("N") };
        var building = new Building { Name = "Credit contract" }; var suite = new Suite { BuildingId = building.Id, Name = "Credit office" };
        var membership = Membership.Grant(suite.Id, user.Id, clock.Now.AddDays(-1)); var account = RentAccount.Open(Guid.NewGuid(), membership.Id);
        var debt = Obligation.Issue(account.Id, new Money(1000), "Original ten dollar debt", new DateOnly(1998, 1, 1), manager.Id, clock.Now);
        var secondDebt = Obligation.Issue(account.Id, new Money(1000), "Eligible later debt", new DateOnly(1998, 2, 1), manager.Id, clock.Now);
        await using (var db = create())
        {
            db.AddRange(user, manager, second, building, suite, membership, account, debt, secondDebt,
                new ActiveGrant { UserId = user.Id, SuiteId = suite.Id, MembershipId = membership.Id },
                new BuildingManager { UserId = manager.Id, BuildingId = building.Id }, new BuildingManager { UserId = second.Id, BuildingId = building.Id }); await db.SaveChangesAsync();
        }
        var actor = new Actor(manager.Id, new HashSet<Guid> { building.Id }, true, true); var otherManager = actor with { UserId = second.Id };
        var tenant = new Actor(user.Id, new HashSet<Guid>(), false, false);
        async Task<T> As<T>(Actor who, Func<IServiceProvider, Task<T>> action)
        { using var scope = provider.CreateScope(); scope.ServiceProvider.GetRequiredService<TestActor>().Actor = who; return await action(scope.ServiceProvider); }
        Task<BillingResult> Credit(Actor who, CreditLocalDebt command) => As(who, sp => sp.GetRequiredService<LocalCreditActions>().ApplyAsync(command));
        var command = new CreditLocalDebt(Guid.NewGuid(), account.Id, debt.Id, debt.Version, 975, ObligationCreditKind.Credit, "Manager approved reduction");
        Assert.Equal(BillingCode.Unavailable, (await Credit(tenant, command)).Code);
        Assert.Equal(BillingCode.Unavailable, (await Credit(actor with { RecentMfa = false }, command)).Code);
        Assert.Equal(BillingCode.ValidationFailed, (await Credit(actor, command with { AmountMinorUnits = 1001 })).Code);
        var applied = await Credit(actor, command); Assert.Equal(BillingCode.Success, applied.Code); Assert.Equal(applied, await Credit(actor, command));
        Assert.Equal(BillingCode.Conflict, (await Credit(actor, command with { AmountMinorUnits = 950 })).Code);
        var view = (await As(tenant, sp => sp.GetRequiredService<BillingActions>().ReadAsync(false))).Single().Obligations.Single(x => x.Id == debt.Id);
        Assert.Equal(1000, view.IssuedMinorUnits); Assert.Equal(975, view.CreditedMinorUnits); Assert.Equal(25, view.NetChargeMinorUnits);

        // A small remaining amount cannot be collected alone. The later full charge carries it exactly once.
        await using (var db = create()) { db.BillingCustomers.Add(BillingCustomerBinding.Create(account.Id, collection.ProviderAccount, false, "cus_credit_" + account.Id.ToString("N"), clock.Now)); await db.SaveChangesAsync(); }
        var small = await As(tenant, sp => sp.GetRequiredService<CollectionActions>().ReviewAsync(account.Id, [debt.Id])); Assert.Equal(25, small.Single().RemainingMinorUnits);
        Assert.Equal(CollectionCode.ValidationFailed, (await As(tenant, sp => sp.GetRequiredService<CollectionActions>().AdmitAsync(new(Guid.NewGuid(), account.Id, small)))).Code);
        var review = await As(tenant, sp => sp.GetRequiredService<CollectionActions>().ReviewAsync(account.Id, [debt.Id, secondDebt.Id]));
        var admitted = await As(tenant, sp => sp.GetRequiredService<CollectionActions>().AdmitAsync(new(Guid.NewGuid(), account.Id, review))); Assert.Equal(CollectionCode.Success, admitted.Code);
        CollectionLease lease;
        await using (var db = create()) { lease = (await new CollectionWorkQueue(db, clock).ClaimAsync(CancellationToken.None))!; Assert.Equal(admitted.AttemptId, lease.Original.Id); Assert.Equal(1025, lease.Original.TotalMinorUnits); }
        var fact = new CanonicalSettlement(lease.Original.ProviderAccount, false, lease.Original.CustomerId, "cs_credit", "pi_credit", 1025, "USD");
        await using (var db = create()) { Assert.True(await new CollectionWorkQueue(db, clock).CompleteAsync(lease, new(ProviderCollectionState.Paid, fact.SessionId, Settlement: fact), CancellationToken.None)); }
        await using (var db = create())
        {
            Assert.Equal(1000, (await db.Obligations.SingleAsync(x => x.Id == debt.Id)).AmountMinorUnits);
            Assert.Equal(25, (await db.PaymentAllocations.SingleAsync(x => x.ObligationId == debt.Id)).AmountMinorUnits);
            Assert.Equal(1025, (await db.SettlementPayments.SingleAsync(x => x.AttemptId == admitted.AttemptId)).AmountMinorUnits);
            Assert.Equal(1, await db.ObligationCredits.CountAsync(x => x.ObligationId == debt.Id));
        }
        var newDebt = Obligation.Issue(account.Id, new Money(1000), "Provider-bound reduction requires review", new DateOnly(1998, 3, 1), manager.Id, clock.Now);
        await using (var db = create()) { db.Obligations.Add(newDebt); await db.SaveChangesAsync(); }
        Assert.Equal(BillingCode.Conflict, (await Credit(actor, command with { OperationId = Guid.NewGuid(), ObligationId = newDebt.Id, ReviewedVersion = newDebt.Version })).Code);

        // An exact local write-off is separate from payment and concurrent credits cannot over-reduce debt.
        var another = Membership.Grant(suite.Id, second.Id, clock.Now.AddDays(-1)); var otherAccount = RentAccount.Open(Guid.NewGuid(), another.Id);
        var localDebt = Obligation.Issue(otherAccount.Id, new Money(1000), "Writeoff test", new DateOnly(1998, 1, 1), manager.Id, clock.Now);
        await using (var db = create()) { db.AddRange(another, otherAccount, localDebt, new ActiveGrant { UserId = second.Id, SuiteId = suite.Id, MembershipId = another.Id }); await db.SaveChangesAsync(); }
        var localCommand = command with { OperationId = Guid.NewGuid(), AccountId = otherAccount.Id, ObligationId = localDebt.Id, ReviewedVersion = localDebt.Version };
        var race = await Task.WhenAll(Task.Run(() => Credit(actor, localCommand)), Task.Run(() => Credit(otherManager, localCommand with { OperationId = Guid.NewGuid() })));
        Assert.Single(race, x => x.Code == BillingCode.Success); Assert.Single(race, x => x.Code == BillingCode.Conflict);
        await using (var db = create()) { localDebt = await db.Obligations.AsNoTracking().SingleAsync(x => x.Id == localDebt.Id); }
        var writtenOff = await Credit(actor, localCommand with { OperationId = Guid.NewGuid(), ReviewedVersion = localDebt.Version, AmountMinorUnits = 25, Kind = ObligationCreditKind.WriteOff });
        Assert.Equal(BillingCode.Success, writtenOff.Code);
        await using (var db = create())
        {
            Assert.Equal(1000, await db.ObligationCredits.Where(x => x.ObligationId == localDebt.Id).SumAsync(x => x.AmountMinorUnits));
            Assert.False(await db.PaymentAllocations.AnyAsync(x => x.ObligationId == localDebt.Id));
            var invalid = ObligationCredit.RecordLocal(Guid.NewGuid(), manager.Id, new(localDebt.Id, account.Id, localDebt.Version, 10), 10, ObligationCreditKind.Credit, "Invalid account link", clock.Now);
            db.ObligationCredits.Add(invalid); await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        await using (var db = create())
        {
            db.BuildingManagers.Remove(await db.BuildingManagers.SingleAsync(x => x.UserId == manager.Id && x.BuildingId == building.Id)); await db.SaveChangesAsync();
        }
        Assert.Equal(BillingCode.Unavailable, (await Credit(actor, command)).Code); // Even replay checks present authority.
    }
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now { get; set; } = new(1998, 1, 1, 12, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
}
