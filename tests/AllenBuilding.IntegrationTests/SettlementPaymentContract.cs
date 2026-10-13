using AllenBuilding.Domain.Billing;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.IntegrationTests;

public static class SettlementPaymentContract
{
    public static async Task VerifyAsync(Func<OfficeDbContext> create)
    {
        var now = DateTimeOffset.UtcNow;
        var payer = new OfficeUser { UserName = $"settlement-{Guid.NewGuid():N}@example.test" };
        var building = new Building { Name = "Settlement contract" };
        var suite = new Suite { BuildingId = building.Id, Name = "Settlement suite" };
        var member = Membership.Grant(suite.Id, payer.Id, now);
        var account = RentAccount.Open(Guid.NewGuid(), member.Id);
        var binding = BillingCustomerBinding.Create(account.Id, "acct_capture", false, "cus_capture", now);
        var first = Obligation.Issue(account.Id, new Money(1000), "Original charge", new DateOnly(2026, 9, 1), payer.Id, now);
        var second = Obligation.Issue(account.Id, new Money(1000), "Current charge", new DateOnly(2026, 10, 1), payer.Id, now);
        var attempt = PaymentAttempt.Admit(Guid.NewGuid(), payer.Id, binding,
            new[] { new ReviewedCollectionBalance(first.Id, account.Id, first.Version, 25), new ReviewedCollectionBalance(second.Id, account.Id, second.Version, 1000) }, now);
        attempt.BeginDispatch(now);
        await using (var db = create())
        {
            db.AddRange(payer, building, suite, member, account, binding, first, second, attempt);
            db.AddRange(attempt.Items.Select(x => new CollectionGuard { AttemptId = attempt.Id, AccountId = account.Id, ObligationId = x.ObligationId }));
            await db.SaveChangesAsync();
        }
        var fact = new CanonicalSettlement(binding.ProviderAccount, false, binding.CustomerId, "cs_capture", "pi_capture", 1025, "USD");
        await using (var db = create())
        {
            var missingItems = await db.PaymentAttempts.SingleAsync(x => x.Id == attempt.Id);
            Assert.Throws<InvalidOperationException>(() => SettlementPayment.Record(missingItems, fact, now));
            Assert.Equal(PaymentAttemptState.Dispatching, missingItems.State);
        }
        async Task<(PaymentAttempt Attempt, SettlementPayment Payment)> Prepare(OfficeDbContext db)
        {
            var original = await db.PaymentAttempts.Include(x => x.Items).SingleAsync(x => x.Id == attempt.Id);
            var payment = SettlementPayment.Record(original, fact, now);
            db.Add(payment);
            foreach (var obligation in await db.Obligations.Where(x => x.Id == first.Id || x.Id == second.Id).ToArrayAsync()) obligation.FinancialStateChanged();
            db.CollectionGuards.RemoveRange(await db.CollectionGuards.Where(x => x.AttemptId == attempt.Id).ToArrayAsync());
            return (original, payment);
        }
        await using (var rejected = create())
        {
            var prepared = await Prepare(rejected);
            rejected.Entry(prepared.Payment.Allocations[0]).Property(x => x.AccountId).CurrentValue = Guid.NewGuid();
            await Assert.ThrowsAsync<DbUpdateException>(() => rejected.SaveChangesAsync());
        }
        await using (var verification = create())
        {
            Assert.Equal(PaymentAttemptState.Dispatching, (await verification.PaymentAttempts.SingleAsync(x => x.Id == attempt.Id)).State);
            Assert.Equal(2, await verification.CollectionGuards.CountAsync(x => x.AttemptId == attempt.Id));
            Assert.False(await verification.SettlementPayments.AnyAsync(x => x.AttemptId == attempt.Id));
            Assert.Equal(first.Version, (await verification.Obligations.SingleAsync(x => x.Id == first.Id)).Version);
        }
        await using (var firstWriter = create())
        await using (var duplicateWriter = create())
        {
            await Prepare(firstWriter); await Prepare(duplicateWriter);
            await firstWriter.SaveChangesAsync();
            await Assert.ThrowsAnyAsync<DbUpdateException>(() => duplicateWriter.SaveChangesAsync());
        }
        Guid receiptId;
        await using (var db = create())
        {
            var recorded = await db.SettlementPayments.Include(x => x.Allocations).SingleAsync(x => x.AttemptId == attempt.Id);
            receiptId = recorded.Id;
            Assert.Equal(1025, recorded.AmountMinorUnits); Assert.Equal(recorded.AmountMinorUnits, recorded.Allocations.Sum(x => x.AmountMinorUnits));
            Assert.Equal(2, recorded.Allocations.Count); Assert.Equal(25, recorded.Allocations.Single(x => x.ObligationId == first.Id).AmountMinorUnits);
            Assert.Equal(PaymentAttemptState.Settled, (await db.PaymentAttempts.SingleAsync(x => x.Id == attempt.Id)).State);
            Assert.False(await db.CollectionGuards.AnyAsync(x => x.AttemptId == attempt.Id));
            Assert.NotEqual(first.Version, (await db.Obligations.SingleAsync(x => x.Id == first.Id)).Version);
            Assert.Equal(1000, (await db.Obligations.SingleAsync(x => x.Id == first.Id)).AmountMinorUnits);
            // This contract tests allocation facts, not the still-unimplemented balance admission or prior-credit ledger.
            Assert.Equal(975, first.AmountMinorUnits - recorded.Allocations.Single(x => x.ObligationId == first.Id).AmountMinorUnits);
        }
        await using (var replay = create())
        {
            var original = await replay.PaymentAttempts.Include(x => x.Items).SingleAsync(x => x.Id == attempt.Id);
            replay.Add(SettlementPayment.Record(original, fact, now.AddMinutes(1)));
            await Assert.ThrowsAsync<DbUpdateException>(() => replay.SaveChangesAsync());
        }
        await using (var negative = create())
        {
            var allocation = await negative.PaymentAllocations.SingleAsync(x => x.PaymentId == receiptId && x.ObligationId == first.Id);
            negative.Entry(allocation).Property(x => x.AmountMinorUnits).CurrentValue = 0;
            await Assert.ThrowsAsync<DbUpdateException>(() => negative.SaveChangesAsync());
        }
        await using (var inspection = create())
        {
            Assert.Equal(1, await inspection.SettlementPayments.CountAsync(x => x.AttemptId == attempt.Id));
            Assert.Equal(2, await inspection.PaymentAllocations.CountAsync(x => x.AttemptId == attempt.Id));
            if (inspection is MySqlOfficeDbContext)
            {
                var collations = await inspection.Database.SqlQueryRaw<string>("SELECT COLLATION_NAME AS Value FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'settlement_payments' AND COLUMN_NAME IN ('provider_account', 'collection_reference')").ToArrayAsync();
                Assert.Equal(2, collations.Length); Assert.All(collations, x => Assert.Equal("utf8mb4_bin", x));
            }
        }
    }
}
