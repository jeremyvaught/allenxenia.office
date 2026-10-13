using AllenBuilding.Application.Access;
using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AllenBuilding.IntegrationTests;

public static class FinancialExceptionContract
{
    public static async Task VerifyAsync(Func<OfficeDbContext> create)
    {
        var now = DateTimeOffset.UtcNow;
        OfficeUser User(string kind) => new() { UserName = "financial-" + kind + "-" + Guid.NewGuid().ToString("N"), Email = kind + "@example.test", TwoFactorEnabled = true };
        var manager = User("manager"); var otherManager = User("other-manager"); var outsider = User("outsider"); var payer = User("payer");
        var building = new Building { Name = "Financial management" }; var foreign = new Building { Name = "Foreign financial management" };
        var suite = new Suite { BuildingId = building.Id, Name = "Managed financial suite" }; var hiddenSuite = new Suite { BuildingId = foreign.Id, Name = "Hidden financial suite" };
        var membership = Membership.Grant(suite.Id, payer.Id, now.AddYears(-1)); membership.Revoke(manager.Id, "Original access ended", now);
        var hiddenMembership = Membership.Grant(hiddenSuite.Id, payer.Id, now.AddYears(-1));
        var account = RentAccount.Open(Guid.NewGuid(), membership.Id); var hiddenAccount = RentAccount.Open(Guid.NewGuid(), hiddenMembership.Id);
        var binding = BillingCustomerBinding.Create(account.Id, "acct_cases", false, "cus_" + Guid.NewGuid().ToString("N"), now);
        var hiddenBinding = BillingCustomerBinding.Create(hiddenAccount.Id, "acct_cases", false, "cus_" + Guid.NewGuid().ToString("N"), now);
        var debt = Obligation.Issue(account.Id, new Money(1000), "Reviewed case debt", new DateOnly(2026, 10, 1), payer.Id, now);
        var attempt = PaymentAttempt.Admit(Guid.NewGuid(), payer.Id, binding, [new(debt.Id, account.Id, debt.Version, 1000)], now);
        var work = CollectionWork.Enqueue(attempt, new("https://cases.example.test", "pmc_cases")); attempt.BeginDispatch(now);
        var issue = FinancialException.Open(attempt, FinancialFailure.ProviderUnknown, null, now);
        await using (var db = create())
        {
            db.AddRange(manager, otherManager, outsider, payer, building, foreign, suite, hiddenSuite, membership, hiddenMembership, account, hiddenAccount, binding, hiddenBinding, debt, attempt, issue, work,
                new CollectionGuard { AccountId = account.Id, AttemptId = attempt.Id, ObligationId = debt.Id },
                new BuildingManager { UserId = manager.Id, BuildingId = building.Id }, new BuildingManager { UserId = otherManager.Id, BuildingId = building.Id },
                new BuildingManager { UserId = outsider.Id, BuildingId = foreign.Id });
            // Foreign cases would fill the bound first if scope were applied after materialization.
            for (var index = 0; index < 55; index++)
            {
                var hiddenDebt = Obligation.Issue(hiddenAccount.Id, new Money(1000), "Hidden debt", new DateOnly(2026, 10, 1), payer.Id, now);
                var hiddenAttempt = PaymentAttempt.Admit(Guid.NewGuid(), payer.Id, hiddenBinding, [new(hiddenDebt.Id, hiddenAccount.Id, hiddenDebt.Version, 1000)], now);
                db.AddRange(hiddenDebt, hiddenAttempt, FinancialException.Open(hiddenAttempt, FinancialFailure.ProviderUnknown, null, now.AddDays(-100)));
            }
            await db.SaveChangesAsync();
        }
        var services = new ServiceCollection(); services.AddSingleton(TimeProvider.System); services.AddScoped<OfficeDbContext>(_ => create());
        services.AddScoped<TestActor>(); services.AddScoped<ICurrentActor>(sp => sp.GetRequiredService<TestActor>());
        services.AddScoped<IFinancialExceptionStore, FinancialExceptionStore>(); services.AddScoped<FinancialExceptionActions>();
        await using var provider = services.BuildServiceProvider();
        var actor = new Actor(manager.Id, new HashSet<Guid> { building.Id }, true, true);
        var second = new Actor(otherManager.Id, new HashSet<Guid> { building.Id }, true, true);
        var tenant = new Actor(payer.Id, new HashSet<Guid>(), false, false);
        async Task<T> As<T>(Actor principal, Func<FinancialExceptionActions, Task<T>> action)
        {
            using var scope = provider.CreateScope(); scope.ServiceProvider.GetRequiredService<TestActor>().Actor = principal;
            return await action(scope.ServiceProvider.GetRequiredService<FinancialExceptionActions>());
        }
        var rows = await As(actor, x => x.ReadAsync()); Assert.Single(rows); Assert.Equal(issue.Id, rows.Single().Id);
        Assert.Equal(CollectionWorkState.Queued, rows.Single().WorkState); Assert.Equal(0, rows.Single().AutomaticAttempts);
        Assert.Equal(2, rows.Single().Owners.Count); Assert.Equal(debt.Id, rows.Single().Items.Single().ObligationId);
        Assert.Empty(await As(tenant, x => x.ReadAsync())); Assert.Empty(await As(actor with { ChallengedMfa = false }, x => x.ReadAsync()));
        Assert.Single(await As(actor with { ManagedBuildings = new HashSet<Guid> { building.Id, foreign.Id } }, x => x.ReadAsync()));
        var assign = new FinancialCaseCommand(Guid.NewGuid(), issue.Id, issue.Version, FinancialCaseAction.Assign, " Review provider history ", otherManager.Id);
        Assert.Equal(BillingCode.Unavailable, (await As(tenant, x => x.ChangeAsync(assign))).Code);
        Assert.Equal(BillingCode.Unavailable, (await As(actor with { RecentMfa = false }, x => x.ChangeAsync(assign))).Code);
        Assert.Equal(BillingCode.Unavailable, (await As(actor, x => x.ChangeAsync(assign with { OwnerUserId = outsider.Id }))).Code);
        var assigned = await As(actor, x => x.ChangeAsync(assign)); Assert.Equal(BillingCode.Success, assigned.Code);
        Assert.Equal(assigned, await As(actor, x => x.ChangeAsync(assign with { Reason = assign.Reason.Trim() })));
        Assert.Equal(BillingCode.Conflict, (await As(actor, x => x.ChangeAsync(assign with { Reason = "Different request" }))).Code);
        Assert.Equal(BillingCode.Conflict, (await As(actor, x => x.ChangeAsync(assign with { OperationId = Guid.NewGuid() }))).Code);
        rows = await As(actor, x => x.ReadAsync()); Assert.Equal(otherManager.Id, rows.Single().OwnerUserId); Assert.Equal("Review provider history", rows.Single().History.Single().Reason);
        var escalate = new FinancialCaseCommand(Guid.NewGuid(), issue.Id, rows.Single().Version, FinancialCaseAction.Escalate, "Provider needs investigation");
        var contenders = await Task.WhenAll(Task.Run(() => As(actor, x => x.ChangeAsync(escalate))), Task.Run(() => As(second, x => x.ChangeAsync(escalate with { OperationId = Guid.NewGuid() }))));
        Assert.Single(contenders, x => x.Code == BillingCode.Success); Assert.Single(contenders, x => x.Code == BillingCode.Conflict);
        rows = await As(actor, x => x.ReadAsync()); Assert.Equal(FinancialExceptionState.Escalated, rows.Single().State);
        await using (var db = create())
        {
            var original = await db.PaymentAttempts.AsNoTracking().SingleAsync(x => x.Id == attempt.Id);
            var fact = new CanonicalSettlement(binding.ProviderAccount, false, binding.CustomerId, "cs_" + attempt.Id.ToString("N"), "pi_" + attempt.Id.ToString("N"), 1000, "USD");
            Assert.Equal(ReconciliationCode.Applied, (await new CollectionReconciliationStore(db, TimeProvider.System)
                .ApplyAsync(original.Id, original.Version, new(ProviderCollectionState.Paid, fact.SessionId, Settlement: fact), CancellationToken.None)).Code);
        }
        Assert.Empty(await As(actor, x => x.ReadAsync()));
        rows = await As(actor, x => x.ReadAsync(true)); Assert.Single(rows); Assert.Equal(FinancialExceptionState.Resolved, rows.Single().State);
        Assert.Equal("Paid", rows.Single().LastVerifiedState); Assert.NotNull(rows.Single().LastVerifiedAt);
        Assert.Equal(BillingCode.Conflict, (await As(actor, x => x.ChangeAsync(assign with { OperationId = Guid.NewGuid(), Version = rows.Single().Version }))).Code);
        await using (var db = create())
        {
            var changedActor = new CallbackActor(async () =>
            {
                await using var revoker = create(); revoker.BuildingManagers.Remove(await revoker.BuildingManagers.SingleAsync(x => x.UserId == manager.Id && x.BuildingId == building.Id));
                await revoker.SaveChangesAsync(); return actor;
            });
            Assert.Empty(await new FinancialExceptionStore(db, changedActor, TimeProvider.System).ReadAsync(actor, true, CancellationToken.None));
        }
        Assert.Equal(BillingCode.Unavailable, (await As(actor, x => x.ChangeAsync(assign))).Code);
        await using (var db = create())
        {
            Assert.Equal(3, await db.FinancialExceptionHistory.CountAsync(x => x.ExceptionId == issue.Id));
            Assert.Equal(2, await db.BillingReceipts.CountAsync(x => x.EntityId == issue.Id));
        }
    }
    private sealed class CallbackActor(Func<Task<Actor?>> callback) : ICurrentActor
    { public Task<Actor?> GetAsync(CancellationToken cancellationToken = default) => callback(); }
}
