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

public static class CashAgreementContract
{
    public static async Task VerifyAsync(Func<OfficeDbContext> create)
    {
        var clock = new Clock(); var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider()); services.AddScoped<OfficeDbContext>(_ => create());
        services.AddIdentityCore<OfficeUser>().AddEntityFrameworkStores<OfficeDbContext>().AddDefaultTokenProviders(); services.AddScoped<AccountInvitations>();
        services.AddScoped<TestActor>(); services.AddScoped<ICurrentActor>(sp => sp.GetRequiredService<TestActor>());
        services.AddSingleton(new CollectionPolicy(true, "acct_agreement", false, new("https://agreement.example.test", "pmc_agreement")));
        services.AddScoped<ICollectionStore, CollectionStore>(); services.AddScoped<CollectionActions>(); await using var provider = services.BuildServiceProvider();
        var payer = new OfficeUser { UserName = "agreement-" + Guid.NewGuid().ToString("N"), EmailConfirmed = true };
        var building = new Building { Name = "Cash agreement" };
        await using (var db = create()) { db.AddRange(payer, building); await db.SaveChangesAsync(); }
        async Task<(PaymentAttempt Original, CashMovement[] Movements, Obligation Debt, FinancialException Case)> Seed(bool applied = true, bool restored = false, RefundDisposition disposition = RefundDisposition.CreditDebt, bool additionalPending = false)
        {
            var suite = new Suite { BuildingId = building.Id, Name = "Agreement office " + Guid.NewGuid().ToString("N") };
            var member = Membership.Grant(suite.Id, payer.Id, clock.Now.AddDays(-1)); var account = RentAccount.Open(Guid.NewGuid(), member.Id);
            var binding = BillingCustomerBinding.Create(account.Id, "acct_agreement", false, "cus_" + account.Id.ToString("N"), clock.Now);
            var debt = Obligation.Issue(account.Id, new Money(1000), "Original agreed debt", new DateOnly(2006, 1, 1), payer.Id, clock.Now);
            var original = PaymentAttempt.Admit(Guid.NewGuid(), payer.Id, binding, [new(debt.Id, account.Id, debt.Version, 1000)], clock.Now);
            var work = CollectionWork.Enqueue(original, new("https://agreement.example.test", "pmc_agreement")); original.BeginDispatch(clock.Now);
            var capture = SettlementPayment.Record(original, new(binding.ProviderAccount, false, binding.CustomerId, "cs_" + original.Id.ToString("N"), "pi_" + original.Id.ToString("N"), 1000, "USD"), clock.Now); work.RequestReconciliation(original, clock.Now);
            var fact = new CanonicalCashMovement(binding.ProviderAccount, false, account.Id, original.Id, binding.CustomerId, original.PaymentId!,
                (restored ? "du_" : "re_") + original.Id.ToString("N"), "txn_" + original.Id.ToString("N"), restored ? CashMovementKind.DisputeWithdrawal : CashMovementKind.Refund, restored ? -1000 : -200, "USD", clock.Now);
            var movement = CashMovement.Record(capture, original, fact, clock.Now); var review = CashMovementReview.Open(movement); var movements = new List<CashMovement> { movement }; var reviews = new List<CashMovementReview> { review };
            RefundAllocation? returned = null; ObligationCredit? credit = null;
            if (restored)
            {
                var reinstated = CashMovement.Record(capture, original, fact with { TransactionId = "txn_restored_" + original.Id.ToString("N"), Kind = CashMovementKind.DisputeReinstatement, SignedMinorUnits = 1000 }, clock.Now);
                movements.Add(reinstated); reviews.Add(CashMovementReview.Open(reinstated));
            }
            else if (applied)
            {
                var operation = Guid.NewGuid(); returned = RefundAllocation.Record(movement, capture.Allocations.Single(), debt.Version, 200, 1000);
                if (disposition == RefundDisposition.CreditDebt) credit = ObligationCredit.RecordRefundCredit(operation, payer.Id, returned, 1000, "Reviewed returned debt", clock.Now);
                review.ApplyRefund(movement, operation, payer.Id, disposition, "Reviewed returned debt", clock.Now);
            }
            if (additionalPending)
            {
                var second = CashMovement.Record(capture, original, fact with { SourceId = "re_second_" + original.Id.ToString("N"), TransactionId = "txn_second_" + original.Id.ToString("N") }, clock.Now);
                movements.Add(second); reviews.Add(CashMovementReview.Open(second));
            }
            debt.FinancialStateChanged(); var issue = FinancialException.Open(original, FinancialFailure.ExternalCashMovement, "Paid", clock.Now);
            await using var db = create(); db.AddRange(suite, member, account, binding, debt, original, work, capture, issue, new ActiveGrant { UserId = payer.Id, SuiteId = suite.Id, MembershipId = member.Id });
            db.AddRange(movements); db.AddRange(reviews); if (returned is not null) db.Add(returned); if (credit is not null) db.Add(credit); await db.SaveChangesAsync(); return (original, movements.ToArray(), debt, issue);
        }
        ProviderCollectionObservation Observation(PaymentAttempt original, CashMovement[] movements, CashObservationState state = CashObservationState.Verified)
        {
            var settlement = new CanonicalSettlement(original.ProviderAccount, false, original.CustomerId, original.SessionId!, original.PaymentId!, original.TotalMinorUnits, "USD");
            var facts = movements.Select(x => new CanonicalCashMovement(x.ProviderAccount, x.LiveMode, x.AccountId, x.AttemptId, x.CustomerId, x.PaymentId, x.SourceId, x.TransactionId, x.Kind, x.SignedMinorUnits, x.Currency, x.ProviderAt)).ToArray();
            var inventory = new VerifiedCashInventory(original.ProviderAccount, false, original.AccountId, original.Id, original.CustomerId, original.PaymentId!, "ch_" + original.Id.ToString("N"), 1000,
                facts.Where(x => x.Kind == CashMovementKind.Refund).Sum(x => -x.SignedMinorUnits), facts.Select(x => x.SourceId).Distinct().ToArray(), facts, clock.Now);
            return new(ProviderCollectionState.Paid, original.SessionId, Settlement: settlement, CashInventory: new(state, state == CashObservationState.Verified ? inventory : null));
        }
        async Task<CollectionLease> Claim(Guid original)
        { await using var db = create(); var lease = await new CollectionWorkQueue(db, clock).ClaimAsync(CancellationToken.None); Assert.NotNull(lease); Assert.Equal(original, lease.Original.Id); Assert.True(lease.ReadOnly); return lease; }
        async Task<bool> Complete(CollectionLease lease, ProviderCollectionObservation observation, TimeProvider? useClock = null)
        { await using var db = create(); return await new CollectionWorkQueue(db, useClock ?? clock).CompleteAsync(lease, observation, CancellationToken.None); }
        async Task<ReviewedCollectionBalance> Balance(Guid account, Guid debt)
        {
            using var scope = provider.CreateScope(); scope.ServiceProvider.GetRequiredService<TestActor>().Actor = new(payer.Id, new HashSet<Guid>(), false, false);
            return (await scope.ServiceProvider.GetRequiredService<CollectionActions>().ReviewAsync(account, [debt])).Single();
        }
        foreach (var disposition in new[] { RefundDisposition.CreditDebt, RefundDisposition.RetainDebt })
        {
            var fixture = await Seed(disposition: disposition); var lease = await Claim(fixture.Original.Id); Assert.True((await Balance(fixture.Original.AccountId, fixture.Debt.Id)).Held);
            Assert.True(await Complete(lease, Observation(fixture.Original, fixture.Movements))); Assert.False(await Complete(lease, Observation(fixture.Original, fixture.Movements)));
            await using (var db = create())
            {
                var review = await db.CashMovementReviews.SingleAsync(x => x.AccountId == fixture.Original.AccountId); Assert.Equal(CashMovementReviewState.Classified, review.State); Assert.Equal(64, review.AgreementHash!.Length); Assert.Equal(clock.Now, review.VerifiedAt); Assert.Equal(disposition, review.Disposition);
                Assert.Equal(FinancialExceptionState.Resolved, (await db.FinancialExceptions.SingleAsync(x => x.Id == fixture.Case.Id)).State); Assert.Equal(CollectionWorkState.Completed, (await db.CollectionWork.SingleAsync(x => x.AttemptId == fixture.Original.Id)).State);
                Assert.Equal(1, await db.FinancialExceptionHistory.CountAsync(x => x.ExceptionId == fixture.Case.Id && x.Action == "CashAgreementVerified")); Assert.Equal(1000, (await db.SettlementPayments.SingleAsync(x => x.AttemptId == fixture.Original.Id)).AmountMinorUnits);
            }
            var balance = await Balance(fixture.Original.AccountId, fixture.Debt.Id); Assert.False(balance.Held); Assert.Equal(disposition == RefundDisposition.CreditDebt ? 0 : 200, balance.RemainingMinorUnits);
        }
        var incompleteProof = await Seed(); Assert.True(await Complete(await Claim(incompleteProof.Original.Id), Observation(incompleteProof.Original, incompleteProof.Movements)));
        await using (var db = create())
        {
            var review = await db.CashMovementReviews.SingleAsync(x => x.AccountId == incompleteProof.Original.AccountId);
            db.Entry(review).Property(x => x.VerifiedAt).CurrentValue = null; db.Entry(review).Property(x => x.AgreementHash).CurrentValue = null; await db.SaveChangesAsync();
        }
        Assert.True((await Balance(incompleteProof.Original.AccountId, incompleteProof.Debt.Id)).Held);
        var mixed = await Seed(additionalPending: true);
        Assert.True(await Complete(await Claim(mixed.Original.Id), Observation(mixed.Original, mixed.Movements)));
        await using (var db = create())
        {
            Assert.False(await db.CashMovementReviews.AnyAsync(x => x.AccountId == mixed.Original.AccountId && x.State == CashMovementReviewState.Classified));
            Assert.False(await db.FinancialExceptionHistory.AnyAsync(x => x.ExceptionId == mixed.Case.Id && x.Action == "CashAgreementVerified"));
        }
        var won = await Seed(restored: true); Assert.True(await Complete(await Claim(won.Original.Id), Observation(won.Original, won.Movements)));
        await using (var db = create()) { Assert.Equal(2, await db.CashMovementReviews.CountAsync(x => x.AccountId == won.Original.AccountId && x.State == CashMovementReviewState.Classified)); Assert.False(await db.RefundAllocations.AnyAsync(x => x.AccountId == won.Original.AccountId)); }
        Assert.False((await Balance(won.Original.AccountId, won.Debt.Id)).Held); Assert.Equal(0, (await Balance(won.Original.AccountId, won.Debt.Id)).RemainingMinorUnits);

        foreach (var mutation in new[] { "subset", "amount", "foreign", "stale", "pending", "unknown", "missing", "unreviewed", "source-pending", "missing-credit", "lost", "stale-worker" })
        {
            var fixture = await Seed(applied: mutation != "unreviewed", restored: mutation == "lost"); var lease = await Claim(fixture.Original.Id); var observed = Observation(fixture.Original, fixture.Movements); var inventory = observed.CashInventory!.Inventory!;
            observed = mutation switch
            {
                "subset" => observed with { CashInventory = new(CashObservationState.Verified, inventory with { Movements = [] }) },
                "amount" => observed with { CashInventory = new(CashObservationState.Verified, inventory with { CapturedMinorUnits = 999 }) },
                "foreign" => observed with { CashInventory = new(CashObservationState.Verified, inventory with { CustomerId = "cus_foreign" }) },
                "stale" => observed with { CashInventory = new(CashObservationState.Verified, inventory with { CheckedAt = clock.Now.AddMinutes(-2) }) },
                "pending" => Observation(fixture.Original, fixture.Movements, CashObservationState.Pending),
                "unknown" => Observation(fixture.Original, fixture.Movements, CashObservationState.Unknown),
                "missing" => observed with { CashInventory = null },
                _ => observed
            };
            await using (var db = create())
            {
                if (mutation == "source-pending") db.CashSourceWork.Add(CashSourceWork.Enqueue(fixture.Original, fixture.Movements[0].SourceId, Guid.NewGuid(), payer.Id, clock.Now));
                if (mutation == "missing-credit") db.ObligationCredits.Remove(await db.ObligationCredits.SingleAsync(x => x.SourceMovementId == fixture.Movements[0].Id));
                if (mutation == "lost")
                {
                    db.CashMovementReviews.Remove(await db.CashMovementReviews.SingleAsync(x => x.MovementId == fixture.Movements[1].Id)); db.CashMovements.Remove(await db.CashMovements.SingleAsync(x => x.Id == fixture.Movements[1].Id));
                    observed = Observation(fixture.Original, [fixture.Movements[0]]);
                }
                await db.SaveChangesAsync();
            }
            if (mutation == "stale-worker") clock.Now = clock.Now.AddMinutes(2);
            Assert.Equal(mutation != "stale-worker", await Complete(lease, observed));
            await using (var db = create())
            {
                Assert.False(await db.CashMovementReviews.AnyAsync(x => x.AccountId == fixture.Original.AccountId && x.State == CashMovementReviewState.Classified));
                Assert.NotEqual(FinancialExceptionState.Resolved, (await db.FinancialExceptions.SingleAsync(x => x.Id == fixture.Case.Id)).State);
                Assert.False(await db.FinancialExceptionHistory.AnyAsync(x => x.ExceptionId == fixture.Case.Id && x.Action == "CashAgreementVerified"));
                var work = await db.CollectionWork.SingleAsync(x => x.AttemptId == fixture.Original.Id);
                Assert.Equal(mutation == "unknown" ? CollectionWorkState.Queued : mutation is "pending" ? CollectionWorkState.Queued : mutation == "stale-worker" ? CollectionWorkState.Leased : CollectionWorkState.NeedsReview, work.State);
                // Keep the mutation fixture from consuming the next contract claim; retain a terminal independent hold.
                work.Review("CanonicalReviewRequired"); await db.SaveChangesAsync();
            }
            Assert.True((await Balance(fixture.Original.AccountId, fixture.Debt.Id)).Held);
        }
        var rollback = await Seed(); var originalLease = await Claim(rollback.Original.Id); var snapshot = Observation(rollback.Original, rollback.Movements);
        foreach (var readsBeforeExpiry in new[] { 5, 6 })
        {
            Assert.False(await Complete(originalLease, snapshot, new ExpiryClock(clock.Now, readsBeforeExpiry)));
            await using var db = create(); Assert.Equal(CashMovementReviewState.AppliedPendingVerification, (await db.CashMovementReviews.SingleAsync(x => x.AccountId == rollback.Original.AccountId)).State);
            Assert.NotEqual(FinancialExceptionState.Resolved, (await db.FinancialExceptions.SingleAsync(x => x.Id == rollback.Case.Id)).State); Assert.False(await db.FinancialExceptionHistory.AnyAsync(x => x.ExceptionId == rollback.Case.Id));
        }
        Assert.True(await Complete(originalLease, snapshot));
    }
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now { get; set; } = new(2006, 1, 1, 12, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class ExpiryClock(DateTimeOffset now, int allowed) : TimeProvider
    { private int _reads; public override DateTimeOffset GetUtcNow() => ++_reads <= allowed ? now : now.AddMinutes(2); }
}
