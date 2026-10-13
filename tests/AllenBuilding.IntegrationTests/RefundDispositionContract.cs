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

public static class RefundDispositionContract
{
    public static async Task VerifyAsync(Func<OfficeDbContext> create)
    {
        var clock = new Clock(); var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider()); services.AddScoped<OfficeDbContext>(_ => create());
        services.AddIdentityCore<OfficeUser>().AddEntityFrameworkStores<OfficeDbContext>().AddDefaultTokenProviders(); services.AddScoped<AccountInvitations>();
        services.AddScoped<TestActor>(); services.AddScoped<ICurrentActor>(sp => sp.GetRequiredService<TestActor>());
        services.AddSingleton(new RefundDispositionPolicy(true)); services.AddScoped<IRefundDispositionStore, RefundDispositionStore>(); services.AddScoped<RefundDispositionActions>();
        services.AddSingleton(new CollectionPolicy(true, "acct_disposition", false, new("https://disposition.example.test", "pmc_disposition")));
        services.AddScoped<ICollectionStore, CollectionStore>(); services.AddScoped<CollectionActions>(); await using var provider = services.BuildServiceProvider();
        var manager = new OfficeUser { UserName = "refund-manager-" + Guid.NewGuid().ToString("N") };
        var second = new OfficeUser { UserName = "refund-second-" + Guid.NewGuid().ToString("N") };
        var tenant = new OfficeUser { UserName = "refund-payer-" + Guid.NewGuid().ToString("N"), EmailConfirmed = true };
        var building = new Building { Name = "Refund contract" }; var suite = new Suite { BuildingId = building.Id, Name = "Refund office" };
        await using (var db = create()) { db.AddRange(manager, second, tenant, building, suite, new BuildingManager { BuildingId = building.Id, UserId = manager.Id }, new BuildingManager { BuildingId = building.Id, UserId = second.Id }); await db.SaveChangesAsync(); }
        var actor = new Actor(manager.Id, new HashSet<Guid> { building.Id }, true, true); var other = actor with { UserId = second.Id }; var payer = new Actor(tenant.Id, new HashSet<Guid>(), false, false);
        async Task<T> As<T>(Actor who, Func<IServiceProvider, Task<T>> action)
        { using var scope = provider.CreateScope(); scope.ServiceProvider.GetRequiredService<TestActor>().Actor = who; return await action(scope.ServiceProvider); }
        Task<BillingResult> Apply(Actor who, ApplyRefundDisposition command) => As(who, sp => sp.GetRequiredService<RefundDispositionActions>().ApplyAsync(command));
        async Task<(ApplyRefundDisposition Command, SettlementPayment Capture, PaymentAttempt Original)> Seed(RefundDisposition disposition, bool multi = false)
        {
            var office = new Suite { BuildingId = building.Id, Name = "Isolated refund fixture " + Guid.NewGuid().ToString("N") };
            var membership = Membership.Grant(office.Id, tenant.Id, clock.Now.AddDays(-1)); var account = RentAccount.Open(Guid.NewGuid(), membership.Id);
            var binding = BillingCustomerBinding.Create(account.Id, "acct_disposition", false, "cus_" + account.Id.ToString("N"), clock.Now);
            var debt = Obligation.Issue(account.Id, new Money(1000), "Refund original", new DateOnly(2004, 1, 1), manager.Id, clock.Now);
            var debts = new List<Obligation> { debt }; if (multi) debts.Add(Obligation.Issue(account.Id, new Money(1000), "Second refund item", new DateOnly(2004, 2, 1), manager.Id, clock.Now));
            var priorCredit = multi ? ObligationCredit.RecordLocal(Guid.NewGuid(), manager.Id, new(debt.Id, account.Id, debt.Version, 1000), 975, ObligationCreditKind.Credit, "Carried remainder from earlier reduction", clock.Now) : null;
            if (priorCredit is not null) debt.FinancialStateChanged();
            var original = PaymentAttempt.Admit(Guid.NewGuid(), tenant.Id, binding, debts.Select(x => new ReviewedCollectionBalance(x.Id, account.Id, x.Version, multi && x.Id == debt.Id ? 25 : 1000)).ToArray(), clock.Now); original.BeginDispatch(clock.Now);
            var capture = SettlementPayment.Record(original, new("acct_disposition", false, binding.CustomerId, "cs_" + original.Id.ToString("N"), "pi_" + original.Id.ToString("N"), multi ? 1025 : 1000, "USD"), clock.Now);
            await using (var db = create()) { db.AddRange(office, membership, account, binding, original, capture, new ActiveGrant { UserId = tenant.Id, SuiteId = office.Id, MembershipId = membership.Id }); db.Obligations.AddRange(debts); if (priorCredit is not null) db.ObligationCredits.Add(priorCredit); await db.SaveChangesAsync(); }
            await using (var db = create())
            {
                var fact = new CanonicalCashMovement("acct_disposition", false, account.Id, original.Id, binding.CustomerId, original.PaymentId!, "re_" + original.Id.ToString("N"), "txn_" + original.Id.ToString("N"), CashMovementKind.Refund, -200, "USD", clock.Now);
                Assert.Equal(ReconciliationCode.Applied, (await new CashMovementStore(db, clock).ApplyAsync(original.Id, original.Version, new(CashObservationState.Verified, [fact]), CancellationToken.None)).Code);
            }
            await using (var db = create())
            {
                var movement = await db.CashMovements.SingleAsync(x => x.AttemptId == original.Id); var review = await db.CashMovementReviews.SingleAsync(x => x.MovementId == movement.Id);
                var items = new List<ReviewedRefundItem>(); foreach (var d in debts) items.Add(new(d.Id, (await db.Obligations.SingleAsync(x => x.Id == d.Id)).Version, multi ? (d.Id == debt.Id ? 25 : 175) : 200));
                return (new(Guid.NewGuid(), account.Id, movement.Id, review.Version, disposition, items, "Manager reviewed original refund"), capture, original);
            }
        }
        var fixture = await Seed(RefundDisposition.CreditDebt, true); var command = fixture.Command;
        var snapshot = await As(actor, sp => sp.GetRequiredService<RefundDispositionActions>().ReadAsync(command.MovementId));
        Assert.NotNull(snapshot); Assert.Equal(command.ReviewVersion, snapshot.ReviewVersion); Assert.Equal(200, snapshot.AmountMinorUnits);
        Assert.Equal(1025, snapshot.Items.Sum(x => x.AvailableCapturedMinorUnits)); Assert.Equal(2, snapshot.Items.Count);
        Assert.Null(await As(payer, sp => sp.GetRequiredService<RefundDispositionActions>().ReadAsync(command.MovementId)));
        Assert.Null(await As(actor with { RecentMfa = false }, sp => sp.GetRequiredService<RefundDispositionActions>().ReadAsync(command.MovementId)));
        foreach (var changed in new[] { actor with { UserId = second.Id }, actor with { RecentMfa = false }, actor with { ChallengedMfa = false } })
        {
            using var scope = provider.CreateScope(); var store = new RefundDispositionStore(scope.ServiceProvider.GetRequiredService<OfficeDbContext>(),
                new RefreshActor(actor, changed), new(true), scope.ServiceProvider.GetRequiredService<AccountInvitations>(), clock);
            Assert.Null(await store.ReadAsync(actor, command.MovementId, CancellationToken.None));
        }

        Assert.Equal(BillingCode.Unavailable, (await Apply(payer, command)).Code);
        Assert.Equal(BillingCode.Unavailable, (await Apply(actor with { RecentMfa = false }, command)).Code);
        Assert.Equal(BillingCode.ValidationFailed, (await Apply(actor, command with { Items = [command.Items[0]] })).Code);
        Assert.Equal(BillingCode.ValidationFailed, (await Apply(actor, command with { Items = [command.Items[0], command.Items[0]] })).Code);
        Assert.Equal(BillingCode.Conflict, (await Apply(actor, command with { ReviewVersion = Guid.NewGuid() })).Code);
        var stale = command with { Items = [command.Items[0], command.Items[1] with { Version = Guid.NewGuid() }] };
        Assert.Equal(BillingCode.Conflict, (await Apply(actor, stale)).Code);
        await using (var db = create()) { Assert.False(await db.RefundAllocations.AnyAsync(x => x.AccountId == command.AccountId)); Assert.False(await db.ObligationCredits.AnyAsync(x => x.AccountId == command.AccountId && x.SourceMovementId != null)); }
        var race = await Task.WhenAll(Task.Run(() => Apply(actor, command)), Task.Run(() => Apply(other, command with { OperationId = Guid.NewGuid(), Disposition = RefundDisposition.RetainDebt })));
        Assert.Single(race, x => x.Code == BillingCode.Success); Assert.Single(race, x => x.Code == BillingCode.Conflict);
        // The winning disposition is immutable, including replay and item order.
        var creditWon = race[0].Code == BillingCode.Success;
        Assert.Null(await As(actor, sp => sp.GetRequiredService<RefundDispositionActions>().ReadAsync(command.MovementId)));
        await using (var db = create())
        {
            Assert.Equal(-200, await db.RefundAllocations.Where(x => x.AccountId == command.AccountId).SumAsync(x => x.SignedMinorUnits));
            Assert.Equal(creditWon ? 200 : 0, await db.ObligationCredits.Where(x => x.AccountId == command.AccountId && x.SourceMovementId != null).SumAsync(x => (long?)x.AmountMinorUnits) ?? 0);
            Assert.Equal(1025, (await db.SettlementPayments.SingleAsync(x => x.Id == fixture.Capture.Id)).AmountMinorUnits);
            Assert.Equal(1025, await db.PaymentAllocations.Where(x => x.PaymentId == fixture.Capture.Id).SumAsync(x => x.AmountMinorUnits));
            var review = await db.CashMovementReviews.SingleAsync(x => x.MovementId == command.MovementId); Assert.Equal(CashMovementReviewState.AppliedPendingVerification, review.State);
            Assert.Equal(creditWon ? manager.Id : second.Id, review.ActorId); Assert.NotEqual(command.ReviewVersion, review.Version);
            // Independent return review still blocks collection if someone incorrectly resolves the exception.
            var issue = await db.FinancialExceptions.SingleAsync(x => x.AttemptId == fixture.Original.Id); issue.ResolveVerified(clock.Now); await db.SaveChangesAsync();
        }
        if (creditWon)
        {
            Assert.Equal(race[0], await Apply(actor, command with { Items = command.Items.Reverse().ToArray() }));
            Assert.Equal(BillingCode.Conflict, (await Apply(actor, command with { Reason = "Changed disposition" })).Code);
        }
        var balances = await As(payer, sp => sp.GetRequiredService<CollectionActions>().ReviewAsync(command.AccountId, command.Items.Select(x => x.ObligationId).ToArray()));
        Assert.Equal(creditWon ? 0 : 200, balances.Sum(x => x.RemainingMinorUnits)); Assert.All(balances, x => Assert.True(x.Held));
        Assert.Equal(CollectionCode.Conflict, (await As(payer, sp => sp.GetRequiredService<CollectionActions>().AdmitAsync(new(Guid.NewGuid(), command.AccountId, balances.Select(x => x with { Held = false, RemainingMinorUnits = 100 }).ToArray())))).Code);

        // Known credit and retain-debt outcomes are both exercised independently of the race winner.
        foreach (var disposition in new[] { RefundDisposition.CreditDebt, RefundDisposition.RetainDebt })
        {
            var known = await Seed(disposition); Assert.Equal(BillingCode.Success, (await Apply(actor, known.Command)).Code);
            var after = await As(payer, sp => sp.GetRequiredService<CollectionActions>().ReviewAsync(known.Command.AccountId, [known.Command.Items[0].ObligationId]));
            Assert.Equal(disposition == RefundDisposition.CreditDebt ? 0 : 200, after.Single().RemainingMinorUnits); Assert.True(after.Single().Held);
            Assert.Equal(BillingCode.Success, (await Apply(actor, known.Command)).Code);
            // A second verified return cannot allocate more than the remaining original funds.
            await using (var db = create())
            {
                var fact = new CanonicalCashMovement("acct_disposition", false, known.Original.AccountId, known.Original.Id, known.Original.CustomerId, known.Original.PaymentId!, "re_excess_" + known.Original.Id.ToString("N"), "txn_excess_" + known.Original.Id.ToString("N"), CashMovementKind.Refund, -900, "USD", clock.Now);
                Assert.Equal(ReconciliationCode.Applied, (await new CashMovementStore(db, clock).ApplyAsync(known.Original.Id, known.Original.Version, new(CashObservationState.Verified, [fact]), CancellationToken.None)).Code);
            }
            ApplyRefundDisposition excessive;
            await using (var db = create())
            {
                var m = await db.CashMovements.SingleAsync(x => x.AttemptId == known.Original.Id && x.SourceId.StartsWith("re_excess_")); var r = await db.CashMovementReviews.SingleAsync(x => x.MovementId == m.Id);
                var d = await db.Obligations.SingleAsync(x => x.Id == known.Command.Items[0].ObligationId);
                excessive = known.Command with { OperationId = Guid.NewGuid(), MovementId = m.Id, ReviewVersion = r.Version, Items = [new(d.Id, d.Version, 900)] };
            }
            Assert.Equal(BillingCode.ValidationFailed, (await Apply(actor, excessive)).Code);
            await using (var db = create()) { Assert.Equal(-200, await db.RefundAllocations.Where(x => x.AccountId == known.Command.AccountId).SumAsync(x => x.SignedMinorUnits)); }
        }
        // Same-building permission cannot substitute a different account/capture or bypass a missing review record.
        var foreign = await Seed(RefundDisposition.CreditDebt);
        Assert.Equal(BillingCode.Unavailable, (await Apply(actor, foreign.Command with { AccountId = command.AccountId })).Code);
        Assert.Equal(BillingCode.Unavailable, (await Apply(actor, foreign.Command with { Items = [new(command.Items[0].ObligationId, command.Items[0].Version, 200)] })).Code);
        await using (var db = create()) { db.CashMovementReviews.Remove(await db.CashMovementReviews.SingleAsync(x => x.MovementId == foreign.Command.MovementId)); await db.SaveChangesAsync(); }
        Assert.Equal(BillingCode.Conflict, (await Apply(actor, foreign.Command)).Code);
        await using (var db = create()) { db.BuildingManagers.Remove(await db.BuildingManagers.SingleAsync(x => x.UserId == manager.Id && x.BuildingId == building.Id)); await db.SaveChangesAsync(); }
        Assert.Equal(BillingCode.Unavailable, (await Apply(actor, command)).Code); // Current authority also governs replay.
    }
    private sealed class RefreshActor(Actor original, Actor changed) : ICurrentActor
    {
        private int _calls;
        public Task<Actor?> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult<Actor?>(++_calls == 1 ? original : changed);
    }
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now { get; set; } = new(2004, 1, 1, 12, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
}
