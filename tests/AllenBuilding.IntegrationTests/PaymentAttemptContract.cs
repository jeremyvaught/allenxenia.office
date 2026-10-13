using AllenBuilding.Domain.Billing;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.IntegrationTests;

public static class PaymentAttemptContract
{
    public static async Task VerifyAsync(Func<OfficeDbContext> create)
    {
        await using (var schema = create())
        {
            if (schema is MySqlOfficeDbContext)
            {
                var collations = await schema.Database.SqlQueryRaw<string>("SELECT COLLATION_NAME AS Value FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME IN ('billing_customers', 'payment_attempts') AND COLUMN_NAME IN ('provider_account', 'customer_id', 'session_id', 'payment_id')").ToArrayAsync();
                Assert.Equal(6, collations.Length); Assert.All(collations, value => Assert.Equal("utf8mb4_bin", value));
            }
        }
        var now = DateTimeOffset.UtcNow;
        var payer = new OfficeUser { UserName = $"collection-{Guid.NewGuid():N}@example.test" };
        var peer = new OfficeUser { UserName = $"collection-peer-{Guid.NewGuid():N}@example.test" };
        var building = new Building { Name = "Collection contract" };
        var suite = new Suite { BuildingId = building.Id, Name = "Collections" };
        var member = Membership.Grant(suite.Id, payer.Id, now); var other = Membership.Grant(suite.Id, peer.Id, now);
        var account = RentAccount.Open(Guid.NewGuid(), member.Id); var second = RentAccount.Open(Guid.NewGuid(), other.Id);
        var binding = BillingCustomerBinding.Create(account.Id, "acct_collection", false, "cus_original", now);
        var small = Obligation.Issue(account.Id, new Money(1000), "Original debt", DateOnly.FromDateTime(now.UtcDateTime), payer.Id, now);
        var full = Obligation.Issue(account.Id, new Money(1000), "Current debt", DateOnly.FromDateTime(now.UtcDateTime), payer.Id, now);
        var race = Obligation.Issue(account.Id, new Money(1000), "Race debt", DateOnly.FromDateTime(now.UtcDateTime), payer.Id, now);
        var otherDebt = Obligation.Issue(second.Id, new Money(1000), "Other tenancy", DateOnly.FromDateTime(now.UtcDateTime), peer.Id, now);
        await using (var db = create()) { db.AddRange(payer, peer, building, suite, member, other, account, second, binding, small, full, race, otherDebt); await db.SaveChangesAsync(); }
        async Task Reject(BillingCustomerBinding value)
        { await using var db = create(); db.Add(value); await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()); }
        await Reject(BillingCustomerBinding.Create(second.Id, binding.ProviderAccount, false, binding.CustomerId, now));
        await Reject(BillingCustomerBinding.Create(account.Id, binding.ProviderAccount, false, "cus_duplicate_account", now));
        await using (var db = create())
        {
            // Exact provider identity is scoped by account and live/test mode; case is significant on MySQL too.
            db.AddRange(BillingCustomerBinding.Create(second.Id, binding.ProviderAccount, false, "cus_Original", now),
                BillingCustomerBinding.Create(account.Id, binding.ProviderAccount, true, binding.CustomerId, now),
                BillingCustomerBinding.Create(account.Id, "acct_Collection", false, binding.CustomerId, now));
            await db.SaveChangesAsync();
        }
        ReviewedCollectionBalance Item(Obligation obligation, long cents) => new(obligation.Id, obligation.AccountId, obligation.Version, cents);
        CollectionGuard Guard(PaymentAttempt attempt, PaymentAttemptItem item) => new() { AttemptId = attempt.Id, AccountId = item.AccountId, ObligationId = item.ObligationId };
        var operation = Guid.NewGuid();
        var original = PaymentAttempt.Admit(operation, payer.Id, binding, new[] { Item(small, 25), Item(full, 1000) }, now);
        await using (var db = create())
        {
            db.Add(original); db.AddRange(original.Items.Select(item => Guard(original, item))); await db.SaveChangesAsync();
        }
        await using (var db = create())
        {
            var saved = await db.PaymentAttempts.Include(x => x.Items).SingleAsync(x => x.Id == original.Id);
            Assert.Equal(1025, saved.TotalMinorUnits); Assert.Equal(original.PayloadHash, saved.PayloadHash);
            Assert.Equal(2, saved.Items.Count); Assert.Equal(1025, saved.Items.Sum(x => x.AmountMinorUnits));
            Assert.Equal(2, await db.CollectionGuards.CountAsync(x => x.AttemptId == original.Id));
        }
        var duplicateOperation = PaymentAttempt.Admit(operation, payer.Id, binding, new[] { Item(full, 1000) }, now);
        await using (var db = create()) { db.Add(duplicateOperation); await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()); }
        var forged = PaymentAttempt.Admit(Guid.NewGuid(), payer.Id, binding, new[] { Item(full, 1000) }, now);
        await using (var db = create())
        {
            db.Add(forged); db.Entry(forged).Property(x => x.CustomerId).CurrentValue = "cus_unrelated";
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        var wrongItem = PaymentAttempt.Admit(Guid.NewGuid(), payer.Id, binding, new[] { Item(full, 1000) }, now);
        await using (var db = create())
        {
            db.Add(wrongItem); db.Entry(wrongItem.Items.Single()).Property(x => x.ObligationId).CurrentValue = otherDebt.Id;
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        await using (var db = create())
        {
            db.Add(new CollectionGuard { AttemptId = original.Id, AccountId = account.Id, ObligationId = otherDebt.Id });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        await using (var first = create())
        await using (var stale = create())
        {
            var a = await first.PaymentAttempts.SingleAsync(x => x.Id == original.Id);
            var b = await stale.PaymentAttempts.SingleAsync(x => x.Id == original.Id);
            a.BeginDispatch(now); b.CancelBeforeDispatch();
            await first.SaveChangesAsync(); await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync());
            a.RequireReview(); await first.SaveChangesAsync();
            Assert.Equal(2, await first.CollectionGuards.CountAsync(x => x.AttemptId == original.Id));
        }
        var canonical = new CanonicalSettlement(binding.ProviderAccount, false, binding.CustomerId, "cs_unique_reference", "pi_unique_reference", 1000, "USD");
        var settled = PaymentAttempt.Admit(Guid.NewGuid(), payer.Id, binding, new[] { Item(full, 1000) }, now);
        settled.BeginDispatch(now); settled.ApplyVerifiedSettlement(canonical);
        await using (var db = create()) { db.Add(settled); await db.SaveChangesAsync(); }
        var duplicateSession = PaymentAttempt.Admit(Guid.NewGuid(), payer.Id, binding, new[] { Item(full, 1000) }, now);
        duplicateSession.BeginDispatch(now); duplicateSession.AttachHandoff(canonical.SessionId, now.AddHours(1), now);
        await using (var db = create()) { db.Add(duplicateSession); await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()); }
        var duplicatePayment = PaymentAttempt.Admit(Guid.NewGuid(), payer.Id, binding, new[] { Item(full, 1000) }, now);
        duplicatePayment.BeginDispatch(now); duplicatePayment.ApplyVerifiedSettlement(canonical with { SessionId = "cs_another_session" });
        await using (var db = create()) { db.Add(duplicatePayment); await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()); }
        var distinctCase = PaymentAttempt.Admit(Guid.NewGuid(), payer.Id, binding, new[] { Item(full, 1000) }, now);
        distinctCase.BeginDispatch(now); distinctCase.ApplyVerifiedSettlement(canonical with { SessionId = "cs_Unique_reference", PaymentId = "pi_Unique_reference" });
        await using (var db = create()) { db.Add(distinctCase); await db.SaveChangesAsync(); }
        var raceSingle = PaymentAttempt.Admit(Guid.NewGuid(), payer.Id, binding, new[] { Item(race, 1000) }, now);
        var raceAggregate = PaymentAttempt.Admit(Guid.NewGuid(), payer.Id, binding, new[] { Item(race, 1000), Item(small, 25) }, now);
        // The existing small-debt claim makes the aggregate fail atomically. Its first item must not leak.
        await using (var db = create())
        {
            db.Add(raceAggregate); db.AddRange(raceAggregate.Items.Select(item => Guard(raceAggregate, item)));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        await using (var db = create())
        {
            Assert.False(await db.PaymentAttempts.AnyAsync(x => x.Id == raceAggregate.Id));
            Assert.False(await db.CollectionGuards.AnyAsync(x => x.ObligationId == race.Id));
        }
        var competing = PaymentAttempt.Admit(Guid.NewGuid(), payer.Id, binding, new[] { Item(race, 1000) }, now);
        using var start = new Barrier(2);
        async Task<bool> Claim(PaymentAttempt attempt) => await Task.Run(async () =>
        {
            await using var db = create(); db.Add(attempt); db.Add(Guard(attempt, attempt.Items.Single()));
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(10)));
            try { await db.SaveChangesAsync(); return true; } catch (DbUpdateException) { return false; }
        });
        var claims = await Task.WhenAll(Claim(raceSingle), Claim(competing));
        Assert.Single(claims, x => x);
        await using (var db = create())
        {
            Assert.Equal(1, await db.CollectionGuards.CountAsync(x => x.ObligationId == race.Id));
            Assert.Equal(1, await db.PaymentAttempts.CountAsync(x => x.Id == raceSingle.Id || x.Id == competing.Id));
            var saved = await db.PaymentAttempts.SingleAsync(x => x.Id == original.Id);
            // Canonical reconciliation, not elapsed time, permits release. This test supplies synthetic evidence.
            saved.ConfirmVerifiedUncollected(binding.ProviderAccount, false, binding.CustomerId, "cs_original_expired");
            db.CollectionGuards.RemoveRange(await db.CollectionGuards.Where(x => x.AttemptId == original.Id).ToArrayAsync());
            await db.SaveChangesAsync();
            Assert.False(await db.CollectionGuards.AnyAsync(x => x.AttemptId == original.Id));
            Assert.Equal(2, await db.PaymentAttemptItems.CountAsync(x => x.AttemptId == original.Id));
        }
    }
}
