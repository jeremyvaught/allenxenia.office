using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.IntegrationTests;

public static class CollectionReconciliationContract
{
    public static async Task VerifyAsync(Func<OfficeDbContext> create)
    {
        var now = DateTimeOffset.UtcNow;
        var payer = new OfficeUser { UserName = "reconcile-" + Guid.NewGuid().ToString("N") };
        var building = new Building { Name = "Reconciliation contract" }; var suite = new Suite { BuildingId = building.Id, Name = "Reconciliation suite" };
        var membership = Membership.Grant(suite.Id, payer.Id, now.AddYears(-1));
        var account = RentAccount.Open(Guid.NewGuid(), membership.Id);
        var binding = BillingCustomerBinding.Create(account.Id, "acct_reconciliation", false, "cus_" + Guid.NewGuid().ToString("N"), now);
        await using (var db = create()) { db.AddRange(payer, building, suite, membership, account, binding); await db.SaveChangesAsync(); }
        async Task<(PaymentAttempt Attempt, Obligation Debt, CanonicalSettlement Fact)> Scenario()
        {
            var debt = Obligation.Issue(account.Id, new Money(1000), "Reviewed debt", new DateOnly(2026, 10, 1), payer.Id, now);
            var attempt = PaymentAttempt.Admit(Guid.NewGuid(), payer.Id, binding, [new(debt.Id, account.Id, debt.Version, 1000)], now); attempt.BeginDispatch(now);
            await using var db = create(); db.AddRange(debt, attempt, new CollectionGuard { AccountId = account.Id, AttemptId = attempt.Id, ObligationId = debt.Id }); await db.SaveChangesAsync();
            return (attempt, debt, new(binding.ProviderAccount, false, binding.CustomerId, "cs_" + attempt.Id.ToString("N"), "pi_" + attempt.Id.ToString("N"), 1000, "USD"));
        }
        ProviderCollectionObservation Paid(CanonicalSettlement fact) => new(ProviderCollectionState.Paid, fact.SessionId, Settlement: fact);
        async Task<ReconciliationResult> Reconcile(Guid id, string session, Func<PaymentAttempt, Task<ProviderCollectionObservation>> observation)
        {
            await using var db = create();
            var gateway = new Gateway(async original => { Assert.Null(db.Database.CurrentTransaction); return await observation(original); });
            return await new CollectionReconciliation(new CollectionReconciliationStore(db, TimeProvider.System), gateway).ReconcileAsync(id, session);
        }
        var successful = await Scenario();
        // Independently ended access cannot prevent reconciliation of already-dispatched cash.
        await using (var db = create()) { var ended = await db.Memberships.SingleAsync(x => x.Id == membership.Id); ended.Revoke(payer.Id, "Access ended", now); await db.SaveChangesAsync(); }
        var first = await Reconcile(successful.Attempt.Id, successful.Fact.SessionId, _ => Task.FromResult(Paid(successful.Fact)));
        Assert.Equal(ReconciliationCode.Applied, first.Code); Assert.NotNull(first.PaymentId);
        var replay = await Reconcile(successful.Attempt.Id, successful.Fact.SessionId, _ => Task.FromResult(Paid(successful.Fact)));
        Assert.Equal(ReconciliationCode.AlreadyApplied, replay.Code); Assert.Equal(first.PaymentId, replay.PaymentId);
        Assert.Equal(ReconciliationCode.NeedsReview, (await Reconcile(successful.Attempt.Id, successful.Fact.SessionId, _ => Task.FromResult(new ProviderCollectionObservation(ProviderCollectionState.Unknown)))).Code);
        await using (var db = create())
        {
            var issue = await db.FinancialExceptions.SingleAsync(x => x.AttemptId == successful.Attempt.Id);
            Assert.Equal(FinancialExceptionState.Open, issue.State); Assert.Equal(FinancialFailure.TerminalDisagreement, issue.Failure);
            Assert.Equal(PaymentAttemptState.Settled, (await db.PaymentAttempts.SingleAsync(x => x.Id == successful.Attempt.Id)).State);
        }
        Assert.Equal(ReconciliationCode.AlreadyApplied, (await Reconcile(successful.Attempt.Id, successful.Fact.SessionId, _ => Task.FromResult(Paid(successful.Fact)))).Code);
        Assert.Equal(ReconciliationCode.NeedsReview, (await Reconcile(successful.Attempt.Id, successful.Fact.SessionId, _ => Task.FromResult(new ProviderCollectionObservation(ProviderCollectionState.Unknown)))).Code);
        await using (var db = create())
        {
            var issue = await db.FinancialExceptions.SingleAsync(x => x.AttemptId == successful.Attempt.Id);
            Assert.Equal(FinancialExceptionState.Open, issue.State);
            Assert.Equal(new[] { "Opened", "Resolved", "Reopened" }, await db.FinancialExceptionHistory.Where(x => x.ExceptionId == issue.Id).OrderBy(x => x.OccurredAt).Select(x => x.Action).ToArrayAsync());
            Assert.Equal(1, await db.FinancialExceptions.CountAsync(x => x.AttemptId == successful.Attempt.Id));
        }
        await using (var db = create())
        {
            var cash = await db.SettlementPayments.Include(x => x.Allocations).SingleAsync(x => x.AttemptId == successful.Attempt.Id);
            Assert.Equal(1000, cash.AmountMinorUnits); Assert.Single(cash.Allocations); Assert.Equal(successful.Debt.Id, cash.Allocations.Single().ObligationId);
            Assert.False(await db.CollectionGuards.AnyAsync(x => x.AttemptId == successful.Attempt.Id));
            var debt = await db.Obligations.SingleAsync(x => x.Id == successful.Debt.Id); Assert.Equal(1000, debt.AmountMinorUnits); Assert.NotEqual(successful.Debt.Version, debt.Version);
        }
        var expired = await Scenario();
        Assert.Equal(ReconciliationCode.Applied, (await Reconcile(expired.Attempt.Id, expired.Fact.SessionId,
            _ => Task.FromResult(new ProviderCollectionObservation(ProviderCollectionState.Uncollected, expired.Fact.SessionId)))).Code);
        var processing = await Scenario();
        Assert.Equal(ReconciliationCode.Applied, (await Reconcile(processing.Attempt.Id, processing.Fact.SessionId,
            _ => Task.FromResult(new ProviderCollectionObservation(ProviderCollectionState.Processing, processing.Fact.SessionId)))).Code);
        var changed = await Scenario();
        Assert.Equal(ReconciliationCode.NeedsReview, (await Reconcile(changed.Attempt.Id, changed.Fact.SessionId, async _ =>
        {
            await using var writer = create(); var debt = await writer.Obligations.SingleAsync(x => x.Id == changed.Debt.Id); debt.FinancialStateChanged(); await writer.SaveChangesAsync();
            return Paid(changed.Fact);
        })).Code);
        var wrong = await Scenario();
        Assert.Equal(ReconciliationCode.NeedsReview, (await Reconcile(wrong.Attempt.Id, wrong.Fact.SessionId, _ => Task.FromResult(Paid(wrong.Fact with { AmountMinorUnits = 999 })))).Code);
        var missing = await Scenario();
        await using (var db = create()) { db.CollectionGuards.Remove(await db.CollectionGuards.SingleAsync(x => x.AttemptId == missing.Attempt.Id)); await db.SaveChangesAsync(); }
        Assert.Equal(ReconciliationCode.NeedsReview, (await Reconcile(missing.Attempt.Id, missing.Fact.SessionId, _ => Task.FromResult(Paid(missing.Fact)))).Code);
        var raced = await Scenario();
        async Task<ReconciliationResult> ApplyOriginal()
        {
            await using var db = create(); return await new CollectionReconciliationStore(db, TimeProvider.System)
                .ApplyAsync(raced.Attempt.Id, raced.Attempt.Version, Paid(raced.Fact), CancellationToken.None);
        }
        var contenders = await Task.WhenAll(Task.Run(ApplyOriginal), Task.Run(ApplyOriginal));
        Assert.Single(contenders, x => x.Code == ReconciliationCode.Applied); Assert.Single(contenders, x => x.Code == ReconciliationCode.Conflict);
        var rolledBack = await Scenario(); var invalid = await Scenario();
        await using (var db = create())
        {
            // A second deliberately invalid allocation makes SaveChanges fail after preparation.
            var badCash = SettlementPayment.Record(invalid.Attempt, invalid.Fact, now); db.SettlementPayments.Add(badCash);
            db.Entry(badCash.Allocations.Single()).Property(x => x.AmountMinorUnits).CurrentValue = 0;
            Assert.Equal(ReconciliationCode.Conflict, (await new CollectionReconciliationStore(db, TimeProvider.System)
                .ApplyAsync(rolledBack.Attempt.Id, rolledBack.Attempt.Version, Paid(rolledBack.Fact), CancellationToken.None)).Code);
        }
        await using (var db = create())
        {
            foreach (var id in new[] { changed.Attempt.Id, wrong.Attempt.Id })
            {
                Assert.Equal(PaymentAttemptState.NeedsReview, (await db.PaymentAttempts.SingleAsync(x => x.Id == id)).State);
                Assert.True(await db.CollectionGuards.AnyAsync(x => x.AttemptId == id)); Assert.False(await db.SettlementPayments.AnyAsync(x => x.AttemptId == id));
                Assert.Equal(FinancialExceptionState.Open, (await db.FinancialExceptions.SingleAsync(x => x.AttemptId == id)).State);
            }
            Assert.Equal(FinancialFailure.LedgerChanged, (await db.FinancialExceptions.SingleAsync(x => x.AttemptId == changed.Attempt.Id)).Failure);
            Assert.Equal(FinancialFailure.ClaimsIncomplete, (await db.FinancialExceptions.SingleAsync(x => x.AttemptId == missing.Attempt.Id)).Failure);
            Assert.Equal(PaymentAttemptState.Processing, (await db.PaymentAttempts.SingleAsync(x => x.Id == processing.Attempt.Id)).State);
            Assert.True(await db.CollectionGuards.AnyAsync(x => x.AttemptId == processing.Attempt.Id));
            Assert.False(await db.SettlementPayments.AnyAsync(x => x.AttemptId == processing.Attempt.Id));
            Assert.Equal(PaymentAttemptState.Uncollected, (await db.PaymentAttempts.SingleAsync(x => x.Id == expired.Attempt.Id)).State);
            Assert.False(await db.CollectionGuards.AnyAsync(x => x.AttemptId == expired.Attempt.Id));
            Assert.Equal(1, await db.SettlementPayments.CountAsync(x => x.AttemptId == raced.Attempt.Id));
            Assert.Equal(PaymentAttemptState.Dispatching, (await db.PaymentAttempts.SingleAsync(x => x.Id == rolledBack.Attempt.Id)).State);
            Assert.Equal(rolledBack.Debt.Version, (await db.Obligations.SingleAsync(x => x.Id == rolledBack.Debt.Id)).Version);
            Assert.True(await db.CollectionGuards.AnyAsync(x => x.AttemptId == rolledBack.Attempt.Id));
            Assert.False(await db.SettlementPayments.AnyAsync(x => x.AttemptId == rolledBack.Attempt.Id || x.AttemptId == invalid.Attempt.Id));
        }
    }
    private sealed class Gateway(Func<PaymentAttempt, Task<ProviderCollectionObservation>> read) : ICollectionGateway
    {
        public Task<ProviderCollectionObservation> ReadAsync(PaymentAttempt original, string sessionId, CancellationToken cancellationToken = default) => read(original);
        public Task<ProviderCollectionObservation> CreateAsync(PaymentAttempt original, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Reconciliation cannot create a payment.");
        public Task<ProviderCollectionObservation> ExpireAsync(PaymentAttempt original, string sessionId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Read-only reconciliation cannot expire a payment.");
    }
}
