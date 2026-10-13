using AllenBuilding.Application.Access;
using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.IntegrationTests;

public static class PayerCollectionContract
{
    public static async Task VerifyAsync(Func<OfficeDbContext> create)
    {
        var now = DateTimeOffset.UtcNow; var policy = new CollectionPolicy(true, "acct_payer_read", false, new("https://payer.example.test", "pmc_payer"));
        var user = new OfficeUser { UserName = "receipt-" + Guid.NewGuid().ToString("N"), EmailConfirmed = true };
        var other = new OfficeUser { UserName = "other-receipt-" + Guid.NewGuid().ToString("N"), EmailConfirmed = true };
        var building = new Building { Name = "Payer receipts" }; var suite = new Suite { BuildingId = building.Id, Name = "Private receipt office" };
        var membership = Membership.Grant(suite.Id, user.Id, now.AddDays(-1)); var account = RentAccount.Open(Guid.NewGuid(), membership.Id);
        var binding = BillingCustomerBinding.Create(account.Id, policy.ProviderAccount, false, "cus_" + account.Id.ToString("N"), now);
        var debt = Obligation.Issue(account.Id, new Money(1000), "Original item <script>private()</script>", new DateOnly(2026, 10, 1), user.Id, now);
        var attempt = PaymentAttempt.Admit(Guid.NewGuid(), user.Id, binding, [new(debt.Id, account.Id, debt.Version, 1000)], now);
        var work = CollectionWork.Enqueue(attempt, policy.Profile!); attempt.BeginDispatch(now); attempt.AttachHandoff("cs_" + attempt.Id.ToString("N"), now.AddHours(1), now);
        await using (var db = create()) { db.AddRange(user, other, building, suite, membership, account, binding, debt, attempt, work, new ActiveGrant { UserId = user.Id, SuiteId = suite.Id, MembershipId = membership.Id }); await db.SaveChangesAsync(); }
        var actor = new Actor(user.Id, new HashSet<Guid>(), false, false);
        async Task<PayerCollectionSnapshot?> Read(Actor who, CollectionPolicy? settings = null, ICurrentActor? current = null)
        { await using var db = create(); return await new PayerCollectionReader(db, current ?? new Fixed(who), settings ?? policy, TimeProvider.System).ReadAsync(who, attempt.Id, CancellationToken.None); }
        await using (var db = create()) { var reader = new PayerCollectionReader(db, new Fixed(actor), policy, TimeProvider.System); Assert.Single(await reader.RecentAsync(actor, account.Id, CancellationToken.None)); Assert.Empty(await reader.RecentAsync(actor with { UserId = other.Id }, account.Id, CancellationToken.None)); }
        var handoff = await Read(actor); Assert.NotNull(handoff); Assert.True(handoff.View.CanContinue); Assert.Equal(0, handoff.View.CapturedMinorUnits); Assert.Single(handoff.View.Items);
        Assert.Null(await Read(actor with { UserId = other.Id })); Assert.Null(await Read(actor, policy with { LiveMode = true })); Assert.Null(await Read(actor, policy with { Enabled = false }));
        Assert.Null(await Read(actor, current: new Refresh(actor, null))); Assert.Null(await Read(actor, current: new Refresh(actor, actor with { ManagedBuildings = new HashSet<Guid> { building.Id }, ChallengedMfa = false })));
        await using (var db = create())
        {
            var original = await db.PaymentAttempts.Include(x => x.Items).SingleAsync(x => x.Id == attempt.Id);
            var capture = SettlementPayment.Record(original, new(binding.ProviderAccount, false, binding.CustomerId, original.SessionId!, "pi_" + original.Id.ToString("N"), 1000, "USD"), now);
            var movement = CashMovement.Record(capture, original, new(binding.ProviderAccount, false, account.Id, original.Id, binding.CustomerId, original.PaymentId!, "re_" + original.Id.ToString("N"), "txn_" + original.Id.ToString("N"), CashMovementKind.Refund, -200, "USD", now), now);
            db.AddRange(capture, movement, CashMovementReview.Open(movement), FinancialException.Open(original, FinancialFailure.ExternalCashMovement, "Paid", now)); await db.SaveChangesAsync();
        }
        var recorded = await Read(actor); Assert.NotNull(recorded); Assert.False(recorded.View.CanContinue); Assert.True(recorded.View.AgreementPending);
        Assert.Equal(1000, recorded.View.CapturedMinorUnits); Assert.Equal(200, recorded.View.RefundedMinorUnits); Assert.Equal(1000, recorded.View.Items.Single().CapturedMinorUnits); Assert.Equal(0, recorded.View.Items.Single().ReturnedMinorUnits);
        await using (var db = create()) { var capture = await db.SettlementPayments.SingleAsync(x => x.AttemptId == attempt.Id); db.Entry(capture).Property(x => x.AmountMinorUnits).CurrentValue = 999; await db.SaveChangesAsync(); }
        Assert.Null(await Read(actor));
        await using (var db = create()) { var capture = await db.SettlementPayments.SingleAsync(x => x.AttemptId == attempt.Id); db.Entry(capture).Property(x => x.AmountMinorUnits).CurrentValue = 1000; await db.SaveChangesAsync(); }
        await using (var db = create())
        {
            var member = await db.Memberships.SingleAsync(x => x.Id == membership.Id); member.Revoke(user.Id, "Original tenancy ended", now);
            db.ActiveGrants.Remove(await db.ActiveGrants.SingleAsync(x => x.MembershipId == member.Id));
            var returning = Membership.Grant(suite.Id, user.Id, now); db.AddRange(returning, new ActiveGrant { UserId = user.Id, SuiteId = suite.Id, MembershipId = returning.Id }); await db.SaveChangesAsync();
        }
        Assert.Null(await Read(actor));
        await using (var db = create()) Assert.Empty(await new PayerCollectionReader(db, new Fixed(actor), policy, TimeProvider.System).RecentAsync(actor, account.Id, CancellationToken.None));
    }
    private sealed class Fixed(Actor actor) : ICurrentActor { public Task<Actor?> GetAsync(CancellationToken token = default) => Task.FromResult<Actor?>(actor); }
    private sealed class Refresh(Actor first, Actor? next) : ICurrentActor
    { private int _reads; public Task<Actor?> GetAsync(CancellationToken token = default) => Task.FromResult(++_reads == 1 ? first : next); }
}
