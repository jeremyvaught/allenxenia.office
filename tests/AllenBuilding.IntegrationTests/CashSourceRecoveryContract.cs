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

public static class CashSourceRecoveryContract
{
    public static async Task VerifyAsync(Func<OfficeDbContext> create)
    {
        var clock = new Clock(); var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider()); services.AddScoped<OfficeDbContext>(_ => create());
        services.AddIdentityCore<OfficeUser>().AddEntityFrameworkStores<OfficeDbContext>().AddDefaultTokenProviders(); services.AddScoped<AccountInvitations>();
        services.AddScoped<TestActor>(); services.AddScoped<ICurrentActor>(sp => sp.GetRequiredService<TestActor>());
        services.AddSingleton(new CashSourceRecoveryPolicy(true)); services.AddScoped<ICashSourceRecoveryStore, CashSourceRecoveryStore>(); services.AddScoped<CashSourceRecoveryActions>();
        services.AddSingleton(new CollectionPolicy(true, "acct_source_queue", false, new("https://source.example.test", "pmc_source")));
        services.AddScoped<ICollectionStore, CollectionStore>(); services.AddScoped<CollectionActions>(); await using var provider = services.BuildServiceProvider();
        var manager = new OfficeUser { UserName = "source-manager-" + Guid.NewGuid().ToString("N") };
        var second = new OfficeUser { UserName = "source-second-" + Guid.NewGuid().ToString("N") };
        var tenant = new OfficeUser { UserName = "source-payer-" + Guid.NewGuid().ToString("N"), EmailConfirmed = true };
        var building = new Building { Name = "Cash source queue" };
        await using (var db = create())
        { db.AddRange(manager, second, tenant, building, new BuildingManager { UserId = manager.Id, BuildingId = building.Id }, new BuildingManager { UserId = second.Id, BuildingId = building.Id }); await db.SaveChangesAsync(); }
        var actor = new Actor(manager.Id, new HashSet<Guid> { building.Id }, true, true); var other = actor with { UserId = second.Id };
        var payer = new Actor(tenant.Id, new HashSet<Guid>(), false, false);
        async Task<T> As<T>(Actor who, Func<IServiceProvider, Task<T>> action)
        { using var scope = provider.CreateScope(); scope.ServiceProvider.GetRequiredService<TestActor>().Actor = who; return await action(scope.ServiceProvider); }
        Task<BillingResult> Request(Actor who, InvestigateCashSource command) => As(who, sp => sp.GetRequiredService<CashSourceRecoveryActions>().RequestAsync(command));
        async Task<(PaymentAttempt Original, FinancialException Case, Membership Membership, Obligation Debt)> Seed(bool settled = true)
        {
            var suite = new Suite { BuildingId = building.Id, Name = "Source office " + Guid.NewGuid().ToString("N") };
            var member = Membership.Grant(suite.Id, tenant.Id, clock.Now.AddDays(-1)); var account = RentAccount.Open(Guid.NewGuid(), member.Id);
            var binding = BillingCustomerBinding.Create(account.Id, "acct_source_queue", false, "cus_" + account.Id.ToString("N"), clock.Now);
            var debt = Obligation.Issue(account.Id, new Money(1000), "Source original debt", new DateOnly(2005, 1, 1), manager.Id, clock.Now);
            var original = PaymentAttempt.Admit(Guid.NewGuid(), tenant.Id, binding, [new(debt.Id, account.Id, debt.Version, 1000)], clock.Now); original.BeginDispatch(clock.Now);
            SettlementPayment? capture = settled ? SettlementPayment.Record(original, new(binding.ProviderAccount, false, binding.CustomerId, "cs_" + original.Id.ToString("N"), "pi_" + original.Id.ToString("N"), 1000, "USD"), clock.Now) : null;
            var issue = FinancialException.Open(original, FinancialFailure.ProviderUnknown, null, clock.Now);
            await using var db = create(); db.AddRange(suite, member, account, binding, debt, original, issue, new ActiveGrant { UserId = tenant.Id, MembershipId = member.Id, SuiteId = suite.Id }); if (capture is not null) db.Add(capture); await db.SaveChangesAsync(); return (original, issue, member, debt);
        }
        InvestigateCashSource Command(FinancialException issue, string? source = null) => new(Guid.NewGuid(), issue.Id, issue.Version, source ?? "re_" + issue.AttemptId.ToString("N"), "Investigate original provider cash evidence");
        Task<CashSourceLease?> Claim() => ClaimCore();
        async Task<CashSourceLease?> ClaimCore() { await using var db = create(); return await new CashSourceQueue(db, clock).ClaimAsync(CancellationToken.None); }
        async Task<bool> Complete(CashSourceLease lease, CashObservation observation) { await using var db = create(); return await new CashSourceQueue(db, clock).CompleteAsync(lease, observation, CancellationToken.None); }
        CashObservation Verified(CashSourceLease lease) => new(CashObservationState.Verified, [new(lease.Original.ProviderAccount, false, lease.Original.AccountId, lease.Original.Id,
            lease.Original.CustomerId, lease.Original.PaymentId!, lease.SourceId, "txn_" + lease.Id.ToString("N"), CashMovementKind.Refund, -200, "USD", clock.Now)]);
        async Task<FinancialException> Issue(Guid attempt) { await using var db = create(); return await db.FinancialExceptions.AsNoTracking().SingleAsync(x => x.AttemptId == attempt); }

        var fixture = await Seed(); var command = Command(fixture.Case);
        Assert.Equal(BillingCode.Unavailable, (await Request(payer, command)).Code);
        Assert.Equal(BillingCode.Unavailable, (await Request(actor with { RecentMfa = false }, command)).Code);
        Assert.Equal(BillingCode.Unavailable, (await Request(actor with { ManagedBuildings = new HashSet<Guid>() }, command)).Code);
        Assert.Equal(BillingCode.ValidationFailed, (await Request(actor, command with { SourceId = "https://untrusted.test/re_source" })).Code);
        Assert.Equal(BillingCode.Conflict, (await Request(actor, command with { CaseVersion = Guid.NewGuid() })).Code);
        var unsettled = await Seed(false); Assert.Equal(BillingCode.Unavailable, (await Request(actor, Command(unsettled.Case))).Code);
        var race = await Task.WhenAll(Task.Run(() => Request(actor, command)), Task.Run(() => Request(other, command with { OperationId = Guid.NewGuid() })));
        Assert.Single(race, x => x.Code == BillingCode.Success); Assert.Single(race, x => x.Code == BillingCode.Conflict);
        var won = race[0].Code == BillingCode.Success; var creator = won ? actor.UserId : other.UserId;
        if (won)
        {
            Assert.Equal(race[0], await Request(actor, command));
            Assert.Equal(BillingCode.Conflict, (await Request(actor, command with { Reason = "Changed replay" })).Code);
        }
        await using (var db = create())
        {
            var work = await db.CashSourceWork.SingleAsync(x => x.AttemptId == fixture.Original.Id); Assert.Equal(creator, work.ActorId);
            Assert.Equal(1, await db.FinancialExceptionHistory.CountAsync(x => x.ExceptionId == fixture.Case.Id && x.Action == "CashReconcileRequested"));
            // Independent source hold survives an incorrectly resolved exception, before any cash fact exists.
            (await db.FinancialExceptions.SingleAsync(x => x.Id == fixture.Case.Id)).ResolveVerified(clock.Now); await db.SaveChangesAsync();
        }
        var pending = await As(payer, sp => sp.GetRequiredService<CollectionActions>().ReviewAsync(fixture.Original.AccountId, [fixture.Debt.Id])); Assert.True(pending.Single().Held);
        await using (var db = create())
        {
            var original = fixture.Original; var cash = new CanonicalSettlement(original.ProviderAccount, false, original.CustomerId, original.SessionId!, original.PaymentId!, 1000, "USD");
            Assert.Equal(ReconciliationCode.NeedsReview, (await new CollectionReconciliationStore(db, clock).ApplyAsync(original.Id, original.Version, new(ProviderCollectionState.Paid, cash.SessionId, Settlement: cash), CancellationToken.None)).Code);
        }
        var competing = await Task.WhenAll(Task.Run(Claim), Task.Run(Claim)); var lease = Assert.Single(competing, x => x is not null)!;
        Assert.Null(await Claim());
        Assert.Equal(BillingCode.Conflict, (await Request(actor, Command(await Issue(fixture.Original.Id), command.SourceId))).Code);
        Assert.False(await Complete(lease with { SourceId = "re_substitute" }, Verified(lease)));
        Assert.False(await Complete(lease with { ApiVersion = "other" }, Verified(lease)));
        Assert.False(await Complete(lease with { Token = Guid.NewGuid() }, Verified(lease)));
        clock.Now = clock.Now.AddMinutes(2); Assert.False(await Complete(lease, Verified(lease)));
        var reclaimed = (await Claim())!; Assert.NotEqual(lease.Token, reclaimed.Token);
        Assert.False(await Complete(lease, Verified(lease)));
        await using (var db = create()) { Assert.False(await db.CashMovements.AnyAsync(x => x.AttemptId == fixture.Original.Id)); }
        // Already-admitted evidence reads continue after portal revocation without granting any new user action.
        await using (var db = create())
        {
            (await db.Memberships.SingleAsync(x => x.Id == fixture.Membership.Id)).Revoke(manager.Id, "Ended during provider read", clock.Now);
            db.ActiveGrants.Remove(await db.ActiveGrants.SingleAsync(x => x.MembershipId == fixture.Membership.Id));
            db.BuildingManagers.Remove(await db.BuildingManagers.SingleAsync(x => x.UserId == manager.Id && x.BuildingId == building.Id)); await db.SaveChangesAsync();
        }
        Assert.True(await Complete(reclaimed, Verified(reclaimed))); Assert.False(await Complete(reclaimed, Verified(reclaimed)));
        Assert.Equal(BillingCode.Unavailable, (await Request(actor, command)).Code);
        await using (var db = create())
        {
            Assert.Single(await db.CashMovements.Where(x => x.AttemptId == fixture.Original.Id).ToListAsync());
            Assert.Equal(CollectionWorkState.Completed, (await db.CashSourceWork.SingleAsync(x => x.Id == reclaimed.Id)).State);
            Assert.Equal(CashMovementReviewState.Pending, (await db.CashMovementReviews.SingleAsync(x => x.AccountId == fixture.Original.AccountId)).State);
            Assert.Equal(FinancialExceptionState.Open, (await db.FinancialExceptions.SingleAsync(x => x.Id == fixture.Case.Id)).State);
        }
        // Re-reading keeps the original job identity/creator, records a new authorized request, and deduplicates cash.
        var again = Command(await Issue(fixture.Original.Id), reclaimed.SourceId); var againResult = await Request(other, again);
        Assert.Equal(BillingCode.Success, againResult.Code); Assert.Equal(reclaimed.Id, againResult.EntityId);
        var repeat = (await Claim())!; Assert.True(await Complete(repeat, Verified(repeat)));
        await using (var db = create()) { Assert.Equal(creator, (await db.CashSourceWork.SingleAsync(x => x.Id == repeat.Id)).ActorId); Assert.Equal(1, await db.CashMovements.CountAsync(x => x.AttemptId == fixture.Original.Id)); }

        // Malformed batches, even with a valid first fact, import no economic rows.
        var malformed = await Seed(); Assert.Equal(BillingCode.Success, (await Request(other, Command(malformed.Case))).Code); var badLease = (await Claim())!;
        var valid = Verified(badLease).Movements!.Single();
        Assert.True(await Complete(badLease, new(CashObservationState.Verified, [valid, valid with { TransactionId = "txn_foreign", CustomerId = "cus_foreign" }])));
        await using (var db = create()) { Assert.False(await db.CashMovements.AnyAsync(x => x.AttemptId == malformed.Original.Id)); Assert.Equal(CollectionWorkState.NeedsReview, (await db.CashSourceWork.SingleAsync(x => x.Id == badLease.Id)).State); }
        // Unknown observations retry without cash, terminal review is visible, and source substitution fails even on internal completion.
        var unknown = await Seed(); Assert.Equal(BillingCode.Success, (await Request(other, Command(unknown.Case))).Code); var unknownLease = (await Claim())!;
        Assert.True(await Complete(unknownLease, new(CashObservationState.Unknown))); Assert.Null(await Claim());
        clock.Now = clock.Now.AddSeconds(30); var retried = (await Claim())!;
        Assert.True(await Complete(retried, new(CashObservationState.Verified, [Verified(retried).Movements!.Single() with { SourceId = "re_wrong_source" }])));
        await using (var db = create()) { Assert.False(await db.CashMovements.AnyAsync(x => x.AttemptId == unknown.Original.Id)); Assert.Equal(CollectionWorkState.NeedsReview, (await db.CashSourceWork.SingleAsync(x => x.Id == retried.Id)).State); }
        // A lease can expire during projection: rollback includes facts, debt versions, history and job completion.
        var expires = await Seed(); Assert.Equal(BillingCode.Success, (await Request(other, Command(expires.Case))).Code); var expiring = (await Claim())!;
        Guid debtVersion; int historyCount;
        await using (var db = create()) { debtVersion = (await db.Obligations.SingleAsync(x => x.Id == expires.Debt.Id)).Version; historyCount = await db.FinancialExceptionHistory.CountAsync(x => x.ExceptionId == expires.Case.Id); }
        var delayed = new ProjectionExpiryClock(clock.Now);
        await using (var db = create()) { Assert.False(await new CashSourceQueue(db, delayed).CompleteAsync(expiring, Verified(expiring), CancellationToken.None)); Assert.Empty(db.ChangeTracker.Entries()); }
        await using (var db = create())
        {
            Assert.False(await db.CashMovements.AnyAsync(x => x.AttemptId == expires.Original.Id)); Assert.Equal(debtVersion, (await db.Obligations.SingleAsync(x => x.Id == expires.Debt.Id)).Version);
            Assert.Equal(historyCount, await db.FinancialExceptionHistory.CountAsync(x => x.ExceptionId == expires.Case.Id)); Assert.Equal(CollectionWorkState.Leased, (await db.CashSourceWork.SingleAsync(x => x.Id == expiring.Id)).State);
        }
        // Second authorization check prevents changed identity/MFA from persisting admission or an audit receipt.
        var revoked = await Seed(); var revokeCommand = Command(revoked.Case);
        using (var scope = provider.CreateScope())
        {
            var store = new CashSourceRecoveryStore(scope.ServiceProvider.GetRequiredService<OfficeDbContext>(), new ChangedActor(other), new(true), scope.ServiceProvider.GetRequiredService<AccountInvitations>(), clock);
            Assert.Equal(BillingCode.Unavailable, (await store.RequestAsync(other, revokeCommand, "hash", CancellationToken.None)).Code);
        }
        await using (var db = create()) { Assert.False(await db.CashSourceWork.AnyAsync(x => x.AttemptId == revoked.Original.Id)); Assert.False(await db.BillingReceipts.AnyAsync(x => x.OperationId == revokeCommand.OperationId)); }
    }
    private sealed class ChangedActor(Actor original) : ICurrentActor
    { private int _calls; public Task<Actor?> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult<Actor?>(++_calls == 1 ? original : original with { RecentMfa = false }); }
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now { get; set; } = new(2005, 1, 1, 12, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class ProjectionExpiryClock(DateTimeOffset now) : TimeProvider
    { private int _calls; public override DateTimeOffset GetUtcNow() => ++_calls <= 2 ? now : now.AddMinutes(2); }
}
