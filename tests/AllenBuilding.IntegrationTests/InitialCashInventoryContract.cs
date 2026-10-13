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

public static class InitialCashInventoryContract
{
    public static async Task VerifyAsync(Func<OfficeDbContext> create)
    {
        var clock = new Clock();
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider()); services.AddScoped<OfficeDbContext>(_ => create());
        services.AddIdentityCore<OfficeUser>().AddEntityFrameworkStores<OfficeDbContext>().AddDefaultTokenProviders(); services.AddScoped<AccountInvitations>();
        services.AddScoped<TestActor>(); services.AddScoped<ICurrentActor>(sp => sp.GetRequiredService<TestActor>());
        services.AddSingleton(new RefundDispositionPolicy(true)); services.AddScoped<IRefundDispositionStore, RefundDispositionStore>(); services.AddScoped<RefundDispositionActions>();
        await using var servicesProvider = services.BuildServiceProvider();
        async Task<(PaymentAttempt Original, Obligation Debt, Guid BuildingId)> Seed()
        {
            var user = new OfficeUser { UserName = "initial-cash-" + Guid.NewGuid().ToString("N"), EmailConfirmed = true };
            var building = new Building { Name = "Initial cash" }; var suite = new Suite { BuildingId = building.Id, Name = "Initial cash office" };
            var member = Membership.Grant(suite.Id, user.Id, clock.Now.AddDays(-1)); var account = RentAccount.Open(Guid.NewGuid(), member.Id);
            var binding = BillingCustomerBinding.Create(account.Id, "acct_initial_cash", false, "cus_" + account.Id.ToString("N"), clock.Now);
            var debt = Obligation.Issue(account.Id, new Money(1000), "Initial capture debt", new DateOnly(2007, 1, 1), user.Id, clock.Now);
            var original = PaymentAttempt.Admit(Guid.NewGuid(), user.Id, binding, [new(debt.Id, account.Id, debt.Version, 1000)], clock.Now); var work = CollectionWork.Enqueue(original, new("https://initial.example.test", "pmc_initial"));
            await using var db = create(); db.AddRange(user, building, suite, member, account, binding, debt, original, work,
                new ActiveGrant { UserId = user.Id, SuiteId = suite.Id, MembershipId = member.Id }, new CollectionGuard { AttemptId = original.Id, AccountId = account.Id, ObligationId = debt.Id }); await db.SaveChangesAsync(); return (original, debt, building.Id);
        }
        async Task<CollectionLease> Claim(Guid id) { await using var db = create(); var lease = await new CollectionWorkQueue(db, clock).ClaimAsync(CancellationToken.None); Assert.NotNull(lease); Assert.Equal(id, lease.Original.Id); return lease; }
        ProviderCollectionObservation Observation(PaymentAttempt original, string mode)
        {
            var settlement = new CanonicalSettlement(original.ProviderAccount, false, original.CustomerId, "cs_" + original.Id.ToString("N"), "pi_" + original.Id.ToString("N"), 1000, "USD");
            var fact = new CanonicalCashMovement(original.ProviderAccount, false, original.AccountId, original.Id, original.CustomerId, settlement.PaymentId,
                (mode == "dispute" ? "du_" : "re_") + original.Id.ToString("N"), "txn_" + original.Id.ToString("N"), mode == "dispute" ? CashMovementKind.DisputeWithdrawal : CashMovementKind.Refund, mode == "dispute" ? -1000 : -200, "USD", clock.Now);
            IReadOnlyList<CanonicalCashMovement> facts = mode == "none" ? [] : mode == "malformed" ? [fact, fact with { TransactionId = "txn_foreign", CustomerId = "cus_foreign" }] : [fact];
            var inventory = new VerifiedCashInventory(original.ProviderAccount, false, original.AccountId, original.Id, original.CustomerId, settlement.PaymentId, "ch_" + original.Id.ToString("N"), 1000,
                facts.Where(x => x.Kind == CashMovementKind.Refund).Sum(x => -x.SignedMinorUnits), facts.Select(x => x.SourceId).Distinct().ToArray(), facts, clock.Now);
            return new(ProviderCollectionState.Paid, settlement.SessionId, Settlement: settlement, CashInventory: mode == "unknown" ? new(CashObservationState.Unknown) : new(CashObservationState.Verified, inventory));
        }
        foreach (var mode in new[] { "refund", "dispute", "unknown", "malformed", "none" })
        {
            var fixture = await Seed(); var lease = await Claim(fixture.Original.Id); var version = lease.Original.Version; var observed = Observation(lease.Original, mode);
            await using (var db = create()) { Assert.True(await new CollectionWorkQueue(db, clock).CompleteAsync(lease, observed, CancellationToken.None)); }
            Assert.Equal(version, lease.Original.Version); Assert.Equal(PaymentAttemptState.Dispatching, lease.Original.State); Assert.Null(lease.Original.PaymentId);
            Guid capturedVersion;
            await using (var db = create())
            {
                var recorded = await db.PaymentAttempts.AsNoTracking().SingleAsync(x => x.Id == fixture.Original.Id); capturedVersion = recorded.Version;
                Assert.Equal(PaymentAttemptState.Settled, recorded.State); Assert.Equal(1000, (await db.SettlementPayments.SingleAsync(x => x.AttemptId == fixture.Original.Id)).AmountMinorUnits);
                Assert.Equal(1000, (await db.PaymentAllocations.SingleAsync(x => x.ObligationId == fixture.Debt.Id)).AmountMinorUnits);
                Assert.Equal(mode is "refund" or "dispute" ? 1 : 0, await db.CashMovements.CountAsync(x => x.AttemptId == fixture.Original.Id));
                Assert.Equal(mode == "none" ? 0 : 1, await db.CollectionGuards.CountAsync(x => x.AttemptId == fixture.Original.Id));
                Assert.False(await db.ObligationCredits.AnyAsync(x => x.AccountId == fixture.Original.AccountId));
                Assert.Equal(mode == "none" ? CollectionWorkState.Completed : CollectionWorkState.NeedsReview, (await db.CollectionWork.SingleAsync(x => x.AttemptId == fixture.Original.Id)).State);
                if (mode != "none") Assert.Equal(FinancialExceptionState.Open, (await db.FinancialExceptions.SingleAsync(x => x.AttemptId == fixture.Original.Id)).State);
            }
            // A gross Paid replay never releases the held original items or imports only part of malformed evidence.
            if (mode != "none")
            {
                await using var db = create(); var result = await new CollectionReconciliationStore(db, clock).ApplyAsync(fixture.Original.Id, capturedVersion, observed with { CashInventory = null }, CancellationToken.None);
                Assert.Equal(ReconciliationCode.NeedsReview, result.Code); Assert.True(await db.CollectionGuards.AnyAsync(x => x.AttemptId == fixture.Original.Id));
            }
        }
        // Full recovery cycle: the authorized disposition cannot clear an initial capture hold; a later complete provider read can.
        foreach (var disposition in new[] { RefundDisposition.CreditDebt, RefundDisposition.RetainDebt })
        {
            var fixture = await Seed(); var lease = await Claim(fixture.Original.Id);
            await using (var db = create()) Assert.True(await new CollectionWorkQueue(db, clock).CompleteAsync(lease, Observation(lease.Original, "refund"), CancellationToken.None));
            ApplyRefundDisposition command;
            await using (var db = create())
            {
                db.BuildingManagers.Add(new() { BuildingId = fixture.BuildingId, UserId = fixture.Original.ActorId }); await db.SaveChangesAsync();
                var movement = await db.CashMovements.SingleAsync(x => x.AttemptId == fixture.Original.Id);
                var review = await db.CashMovementReviews.SingleAsync(x => x.MovementId == movement.Id);
                var debt = await db.Obligations.SingleAsync(x => x.Id == fixture.Debt.Id);
                command = new(Guid.NewGuid(), fixture.Original.AccountId, movement.Id, review.Version, disposition, [new(debt.Id, debt.Version, 200)], "Review return discovered before local capture");
            }
            using (var scope = servicesProvider.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<TestActor>().Actor = new(fixture.Original.ActorId, new HashSet<Guid> { fixture.BuildingId }, true, true);
                Assert.Equal(BillingCode.Success, (await scope.ServiceProvider.GetRequiredService<RefundDispositionActions>().ApplyAsync(command)).Code);
            }
            await using (var db = create())
            {
                Assert.True(await db.CollectionGuards.AnyAsync(x => x.AttemptId == fixture.Original.Id));
                var original = await db.PaymentAttempts.Include(x => x.Items).SingleAsync(x => x.Id == fixture.Original.Id);
                (await db.CollectionWork.SingleAsync(x => x.AttemptId == original.Id)).RequestReconciliation(original, clock.Now); await db.SaveChangesAsync();
            }
            var recovery = await Claim(fixture.Original.Id); Assert.True(recovery.ReadOnly);
            await using (var db = create()) Assert.True(await new CollectionWorkQueue(db, clock).CompleteAsync(recovery, Observation(recovery.Original, "refund"), CancellationToken.None));
            await using (var db = create())
            {
                Assert.False(await db.CollectionGuards.AnyAsync(x => x.AttemptId == fixture.Original.Id));
                Assert.Equal(CashMovementReviewState.Classified, (await db.CashMovementReviews.SingleAsync(x => x.AccountId == fixture.Original.AccountId)).State);
                Assert.Equal(FinancialExceptionState.Resolved, (await db.FinancialExceptions.SingleAsync(x => x.AttemptId == fixture.Original.Id)).State);
                Assert.Equal(disposition == RefundDisposition.CreditDebt ? 200 : 0, await db.ObligationCredits.Where(x => x.AccountId == fixture.Original.AccountId).SumAsync(x => (long?)x.AmountMinorUnits) ?? 0);
                Assert.Equal(-200, await db.RefundAllocations.Where(x => x.AccountId == fixture.Original.AccountId).SumAsync(x => x.SignedMinorUnits));
            }
        }
        // Cash and the positive capture are one fenced transaction. A failed save or expired lease leaves neither receipt nor return.
        foreach (var failure in new[] { "expired", "save" })
        {
            var fixture = await Seed(); var lease = await Claim(fixture.Original.Id); var observation = Observation(lease.Original, "refund");
            await using (var db = create())
            {
                if (failure == "save") db.SavingChanges += (_, _) => { foreach (var entry in db.ChangeTracker.Entries<CashMovement>().Where(x => x.State == EntityState.Added)) entry.Property(x => x.SignedMinorUnits).CurrentValue = 0; };
                else clock.Now = clock.Now.AddMinutes(2);
                Assert.Equal(failure == "save", await new CollectionWorkQueue(db, clock).CompleteAsync(lease, observation, CancellationToken.None));
            }
            await using (var db = create())
            {
                Assert.False(await db.SettlementPayments.AnyAsync(x => x.AttemptId == fixture.Original.Id)); Assert.False(await db.CashMovements.AnyAsync(x => x.AttemptId == fixture.Original.Id));
                Assert.True(await db.CollectionGuards.AnyAsync(x => x.AttemptId == fixture.Original.Id)); Assert.Equal(PaymentAttemptState.Dispatching, (await db.PaymentAttempts.SingleAsync(x => x.Id == fixture.Original.Id)).State);
                (await db.CollectionWork.SingleAsync(x => x.AttemptId == fixture.Original.Id)).Review("CanonicalReviewRequired"); await db.SaveChangesAsync();
            }
        }
    }
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now { get; set; } = new(2007, 1, 1, 12, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
}
