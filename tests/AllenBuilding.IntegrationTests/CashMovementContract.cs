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

public static class CashMovementContract
{
    public static async Task VerifyAsync(Func<OfficeDbContext> create)
    {
        var clock = new Clock(); var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider()); services.AddScoped<OfficeDbContext>(_ => create());
        services.AddIdentityCore<OfficeUser>().AddEntityFrameworkStores<OfficeDbContext>().AddDefaultTokenProviders(); services.AddScoped<AccountInvitations>();
        services.AddScoped<TestActor>(); services.AddScoped<ICurrentActor>(sp => sp.GetRequiredService<TestActor>());
        services.AddSingleton(new CollectionPolicy(true, "acct_cash", false, new("https://cash.example.test", "pmc_cash")));
        services.AddScoped<ICollectionStore, CollectionStore>(); services.AddScoped<CollectionActions>(); await using var provider = services.BuildServiceProvider();
        async Task<(PaymentAttempt Original, SettlementPayment Capture, Obligation Debt, Membership Membership)> Seed()
        {
            var user = new OfficeUser { UserName = "cash-" + Guid.NewGuid().ToString("N"), EmailConfirmed = true };
            var building = new Building { Name = "Cash contract" }; var suite = new Suite { BuildingId = building.Id, Name = "Cash office" };
            var membership = Membership.Grant(suite.Id, user.Id, clock.Now.AddDays(-1)); var account = RentAccount.Open(Guid.NewGuid(), membership.Id);
            var binding = BillingCustomerBinding.Create(account.Id, "acct_cash", false, "cus_" + account.Id.ToString("N"), clock.Now);
            var debt = Obligation.Issue(account.Id, new Money(1000), "Original captured debt", new DateOnly(2003, 1, 1), user.Id, clock.Now);
            var original = PaymentAttempt.Admit(Guid.NewGuid(), user.Id, binding, [new(debt.Id, account.Id, debt.Version, 1000)], clock.Now); original.BeginDispatch(clock.Now);
            var capture = SettlementPayment.Record(original, new("acct_cash", false, binding.CustomerId, "cs_" + original.Id.ToString("N"), "pi_" + original.Id.ToString("N"), 1000, "USD"), clock.Now);
            await using var db = create(); db.AddRange(user, building, suite, membership, account, binding, debt, original, capture,
                new ActiveGrant { UserId = user.Id, SuiteId = suite.Id, MembershipId = membership.Id }); await db.SaveChangesAsync(); return (original, capture, debt, membership);
        }
        CanonicalCashMovement Fact(PaymentAttempt original) => new(original.ProviderAccount, original.LiveMode, original.AccountId, original.Id, original.CustomerId, original.PaymentId!,
            "re_" + original.Id.ToString("N"), "txn_" + original.Id.ToString("N"), CashMovementKind.Refund, -200, "USD", clock.Now);
        async Task<ReconciliationResult> Apply(PaymentAttempt original, params CanonicalCashMovement[] facts)
        { await using var db = create(); return await new CashMovementStore(db, clock).ApplyAsync(original.Id, original.Version, new(CashObservationState.Verified, facts), CancellationToken.None); }

        var fixture = await Seed(); var fact = Fact(fixture.Original);
        var competing = await Task.WhenAll(Task.Run(() => Apply(fixture.Original, fact)), Task.Run(() => Apply(fixture.Original, fact)));
        Assert.Single(competing, x => x.Code == ReconciliationCode.Applied); Assert.Single(competing, x => x.Code == ReconciliationCode.AlreadyApplied);
        await using (var db = create())
        {
            Assert.Equal(1, await db.CashMovements.CountAsync(x => x.AttemptId == fixture.Original.Id));
            Assert.Equal(-200, await db.CashMovements.Where(x => x.AttemptId == fixture.Original.Id).SumAsync(x => x.SignedMinorUnits));
            Assert.Equal(1000, (await db.SettlementPayments.SingleAsync(x => x.Id == fixture.Capture.Id)).AmountMinorUnits);
            Assert.Equal(1000, (await db.PaymentAllocations.SingleAsync(x => x.ObligationId == fixture.Debt.Id)).AmountMinorUnits);
            Assert.NotEqual(fixture.Debt.Version, (await db.Obligations.SingleAsync(x => x.Id == fixture.Debt.Id)).Version);
            Assert.False(await db.ObligationCredits.AnyAsync(x => x.AccountId == fixture.Original.AccountId));
        }
        // New debt in the account stays held even if a corrupted/missing review row or resolved case would otherwise hide the return.
        var newDebt = Obligation.Issue(fixture.Original.AccountId, new Money(1000), "New debt under cash review", new DateOnly(2003, 2, 1), fixture.Original.ActorId, clock.Now);
        await using (var db = create())
        {
            db.Obligations.Add(newDebt); var issue = await db.FinancialExceptions.SingleAsync(x => x.AttemptId == fixture.Original.Id); issue.ResolveVerified(clock.Now);
            db.CashMovementReviews.Remove(await db.CashMovementReviews.SingleAsync(x => x.AccountId == fixture.Original.AccountId)); await db.SaveChangesAsync();
        }
        using (var scope = provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<TestActor>().Actor = new(fixture.Original.ActorId, new HashSet<Guid>(), false, false);
            var actions = scope.ServiceProvider.GetRequiredService<CollectionActions>(); var review = await actions.ReviewAsync(fixture.Original.AccountId, [newDebt.Id]); Assert.True(review.Single().Held);
            Assert.Equal(CollectionCode.Conflict, (await actions.AdmitAsync(new(Guid.NewGuid(), fixture.Original.AccountId, [review.Single() with { Held = false }]))).Code);
        }
        await using (var db = create())
        {
            var settlement = new CanonicalSettlement(fixture.Original.ProviderAccount, false, fixture.Original.CustomerId, fixture.Original.SessionId!, fixture.Original.PaymentId!, 1000, "USD");
            Assert.Equal(ReconciliationCode.NeedsReview, (await new CollectionReconciliationStore(db, clock).ApplyAsync(fixture.Original.Id, fixture.Original.Version, new(ProviderCollectionState.Paid, settlement.SessionId, Settlement: settlement), CancellationToken.None)).Code);
        }
        // A bad item rejects the whole imported batch, rather than persisting its earlier valid money fact.
        var invalid = await Apply(fixture.Original, fact with { TransactionId = "txn_second", SourceId = "re_second", SignedMinorUnits = -100 }, fact with { TransactionId = "txn_foreign", CustomerId = "cus_foreign" });
        Assert.Equal(ReconciliationCode.NeedsReview, invalid.Code);
        await using (var db = create())
        {
            Assert.Equal(1, await db.CashMovements.CountAsync(x => x.AttemptId == fixture.Original.Id));
            Assert.Equal(FinancialExceptionState.Open, (await db.FinancialExceptions.SingleAsync(x => x.AttemptId == fixture.Original.Id)).State);
            var bad = CashMovement.Record(fixture.Capture, fixture.Original, fact with { TransactionId = "txn_constraint" }, clock.Now);
            db.CashMovements.Add(bad); db.Entry(bad).Property(x => x.SignedMinorUnits).CurrentValue = 0; await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        var ended = await Seed();
        await using (var db = create())
        {
            var reader = new Reader(async original =>
            {
                Assert.Null(db.Database.CurrentTransaction);
                await using var revoker = create(); (await revoker.Memberships.SingleAsync(x => x.Id == ended.Membership.Id)).Revoke(original.ActorId, "Ended during cash retrieval", clock.Now);
                revoker.ActiveGrants.Remove(await revoker.ActiveGrants.SingleAsync(x => x.MembershipId == ended.Membership.Id)); await revoker.SaveChangesAsync();
                return new(CashObservationState.Verified, [Fact(original)]);
            });
            Assert.Equal(ReconciliationCode.Applied, (await new CashMovementReconciliation(new CashMovementStore(db, clock), reader).ReconcileAsync(ended.Original.Id, Fact(ended.Original).SourceId)).Code);
        }
        var restored = await Seed(); var withdrawn = Fact(restored.Original) with { SourceId = "du_restored", Kind = CashMovementKind.DisputeWithdrawal, SignedMinorUnits = -1000 };
        Assert.Equal(ReconciliationCode.Applied, (await Apply(restored.Original, withdrawn, withdrawn with { TransactionId = "txn_restored", Kind = CashMovementKind.DisputeReinstatement, SignedMinorUnits = 1000 })).Code);
        await using (var db = create())
        {
            Assert.Equal(0, await db.CashMovements.Where(x => x.AttemptId == restored.Original.Id).SumAsync(x => x.SignedMinorUnits));
            Assert.Equal(2, await db.CashMovementReviews.CountAsync(x => x.AccountId == restored.Original.AccountId && x.State == CashMovementReviewState.Pending));
            Assert.Equal(1000, (await db.SettlementPayments.SingleAsync(x => x.Id == restored.Capture.Id)).AmountMinorUnits);
        }
    }
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now { get; set; } = new(2003, 1, 1, 12, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Reader(Func<PaymentAttempt, Task<CashObservation>> read) : ICashMovementReader
    { public Task<CashObservation> ReadAsync(PaymentAttempt original, string sourceId, CancellationToken cancellationToken) => read(original); }
}
