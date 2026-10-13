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

public static class CustomerProvisionContract
{
    public static async Task VerifyAsync(Func<OfficeDbContext> create)
    {
        var clock = new Clock(); var policy = new CustomerProvisionPolicy(true, "acct_provision", false);
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider()); services.AddScoped<OfficeDbContext>(_ => create());
        services.AddIdentityCore<OfficeUser>().AddEntityFrameworkStores<OfficeDbContext>().AddDefaultTokenProviders();
        services.AddScoped<TestActor>(); services.AddScoped<ICurrentActor>(sp => sp.GetRequiredService<TestActor>()); services.AddScoped<AccountInvitations>();
        services.AddSingleton(policy); services.AddScoped<ICustomerProvisionStore, CustomerProvisionStore>(); services.AddScoped<CustomerProvisionActions>();
        await using var provider = services.BuildServiceProvider();
        async Task<(RentAccount Account, Membership Membership, Actor Actor)> Seed()
        {
            var user = new OfficeUser { UserName = "customer-" + Guid.NewGuid().ToString("N"), EmailConfirmed = true };
            var building = new Building { Name = "Customer provisioning" }; var suite = new Suite { BuildingId = building.Id, Name = "Customer office" };
            var membership = Membership.Grant(suite.Id, user.Id, clock.Now.AddDays(-1)); var account = RentAccount.Open(Guid.NewGuid(), membership.Id);
            await using var db = create(); db.AddRange(user, building, suite, membership, account, new ActiveGrant { UserId = user.Id, SuiteId = suite.Id, MembershipId = membership.Id });
            await db.SaveChangesAsync(); return (account, membership, new(user.Id, new HashSet<Guid>(), false, false));
        }
        async Task<CustomerProvisionResult> Ensure(Actor actor, Guid account)
        {
            using var scope = provider.CreateScope(); scope.ServiceProvider.GetRequiredService<TestActor>().Actor = actor;
            return await scope.ServiceProvider.GetRequiredService<CustomerProvisionActions>().EnsureAsync(account);
        }
        async Task Revoke(Membership membership)
        {
            await using var db = create(); await using var tx = await AuthorityLocks.BeginAsync(db); await AuthorityLocks.MembershipAsync(db, membership.Id);
            (await db.Memberships.SingleAsync(x => x.Id == membership.Id)).Revoke(membership.UserId, "Ended", clock.Now);
            db.ActiveGrants.Remove(await db.ActiveGrants.SingleAsync(x => x.MembershipId == membership.Id)); await db.SaveChangesAsync(); await tx.CommitAsync();
        }
        async Task<CustomerProvisionLease?> Claim()
        { await using var db = create(); return await new CustomerProvisionQueue(db, clock).ClaimAsync(CancellationToken.None); }
        async Task<bool> Complete(CustomerProvisionLease lease, CustomerObservation observation)
        { await using var db = create(); return await new CustomerProvisionQueue(db, clock).CompleteAsync(lease, observation, CancellationToken.None); }
        static CustomerObservation Verified(CustomerProvisionLease lease, string customer) => new(CustomerProvisionOutcome.Verified,
            lease.Original.ProviderAccount, lease.Original.LiveMode, lease.Original.AccountId, lease.Original.Id, customer);

        var first = await Seed(); var peer = await Seed();
        Assert.Equal(CollectionCode.Unavailable, (await Ensure(peer.Actor, first.Account.Id)).Code);
        var admissions = await Task.WhenAll(Task.Run(() => Ensure(first.Actor, first.Account.Id)), Task.Run(() => Ensure(first.Actor, first.Account.Id)));
        Assert.All(admissions, x => Assert.Equal(CollectionCode.Success, x.Code)); Assert.Equal(admissions[0].OperationId, admissions[1].OperationId);
        var contenders = await Task.WhenAll(Task.Run(Claim), Task.Run(Claim)); Assert.Single(contenders, x => x is not null);
        var lease = contenders.Single(x => x is not null)!; Assert.Equal(admissions[0].OperationId, lease.Original.Id);
        await Revoke(first.Membership); Assert.True(await Complete(lease, Verified(lease, "cus_" + lease.Original.Id.ToString("N"))));
        Assert.False(await Complete(lease, Verified(lease, "cus_replacement"))); Assert.Equal(CollectionCode.Unavailable, (await Ensure(first.Actor, first.Account.Id)).Code);
        await using (var db = create())
        {
            Assert.Equal(1, await db.CustomerProvisions.CountAsync(x => x.AccountId == first.Account.Id));
            Assert.Equal(1, await db.BillingCustomers.CountAsync(x => x.AccountId == first.Account.Id));
            Assert.Equal(1, await db.AccountAudits.CountAsync(x => x.Action == "CustomerProvisionRequested" && x.SubjectId == first.Account.Id.ToString("D")));
        }
        // The same user returning to the same suite gets a new account and original customer operation.
        RentAccount returned;
        await using (var db = create())
        {
            var membership = Membership.Grant(first.Membership.SuiteId, first.Actor.UserId, clock.Now.AddSeconds(-1)); returned = RentAccount.Open(Guid.NewGuid(), membership.Id);
            db.AddRange(membership, returned, new ActiveGrant { UserId = first.Actor.UserId, SuiteId = membership.SuiteId, MembershipId = membership.Id }); await db.SaveChangesAsync();
        }
        var freshAdmission = await Ensure(first.Actor, returned.Id); Assert.NotEqual(admissions[0].OperationId, freshAdmission.OperationId);
        var freshLease = (await Claim())!; Assert.NotEqual(lease.Original.ProviderIdempotencyKey, freshLease.Original.ProviderIdempotencyKey);
        Assert.True(await Complete(freshLease, Verified(freshLease, "cus_returned")));
        await using (var db = create()) { Assert.Equal("cus_returned", (await db.BillingCustomers.SingleAsync(x => x.AccountId == returned.Id)).CustomerId); }

        await Ensure(peer.Actor, peer.Account.Id); var stale = (await Claim())!; clock.Now = clock.Now.AddMinutes(2);
        var recovered = (await Claim())!; Assert.Equal(stale.Original.Id, recovered.Original.Id); Assert.NotEqual(stale.Token, recovered.Token);
        Assert.False(await Complete(stale, Verified(stale, "cus_stale"))); Assert.True(await Complete(recovered, new(CustomerProvisionOutcome.Unknown)));
        clock.Now = clock.Now.AddMinutes(1); var retry = (await Claim())!; Assert.Equal(stale.Original.ProviderIdempotencyKey, retry.Original.ProviderIdempotencyKey);
        Assert.True(await Complete(retry, Verified(retry, "cus_peer")));
        var ended = await Seed(); await Ensure(ended.Actor, ended.Account.Id); await Revoke(ended.Membership); Assert.Null(await Claim());
        await using (var db = create()) { var row = await db.CustomerProvisions.SingleAsync(x => x.AccountId == ended.Account.Id); Assert.Equal(CustomerProvisionState.Suppressed, row.State); Assert.Null(row.DispatchedAt); }

        var mismatch = await Seed(); await Ensure(mismatch.Actor, mismatch.Account.Id); var wrong = (await Claim())!;
        Assert.True(await Complete(wrong, Verified(wrong, "cus_wrong") with { AccountId = returned.Id }));
        var reused = await Seed(); await Ensure(reused.Actor, reused.Account.Id); var reusedLease = (await Claim())!;
        Assert.True(await Complete(reusedLease, Verified(reusedLease, "cus_returned")));
        await using (var db = create())
        {
            Assert.False(await db.BillingCustomers.AnyAsync(x => x.AccountId == mismatch.Account.Id || x.AccountId == reused.Account.Id));
            Assert.Equal(CustomerProvisionState.NeedsReview, (await db.CustomerProvisions.SingleAsync(x => x.AccountId == mismatch.Account.Id)).State);
            Assert.Equal("BindingConflict", (await db.CustomerProvisions.SingleAsync(x => x.AccountId == reused.Account.Id)).LastError);
        }
        var aged = await Seed(); var agedAdmission = await Ensure(aged.Actor, aged.Account.Id); var agedLease = (await Claim())!;
        Assert.True(await Complete(agedLease, new(CustomerProvisionOutcome.Unknown))); clock.Now = clock.Now.AddHours(23);
        Assert.Null(await Claim()); var same = await Ensure(aged.Actor, aged.Account.Id); Assert.Equal(agedAdmission.OperationId, same.OperationId); Assert.Equal(CustomerProvisionState.NeedsReview, same.State);
        await using (var db = create()) { Assert.False(await db.BillingCustomers.AnyAsync(x => x.AccountId == aged.Account.Id)); }
    }
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now { get; set; } = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
}
