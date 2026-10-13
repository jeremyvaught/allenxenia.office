using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.IntegrationTests;

public static class PaymentEventWorkContract
{
    public static async Task VerifyAsync(Func<OfficeDbContext> create)
    {
        var clock = new EventClock();
        PaymentEventInbox Event(string type = "checkout.session.completed", string? resource = null) => PaymentEventInbox.Receive("acct_events", false,
            "evt_" + Guid.NewGuid().ToString("N"), type, resource ?? "cs_" + Guid.NewGuid().ToString("N"), CheckoutProfile.ApiContract, new string('A', 64), true, clock.Now);
        async Task<(PaymentAttempt Attempt, PaymentEventInbox Event)> Seed()
        {
            var user = new OfficeUser { UserName = "event-" + Guid.NewGuid().ToString("N"), EmailConfirmed = true };
            var building = new Building { Name = "Event contract" }; var suite = new Suite { BuildingId = building.Id, Name = "Event suite" };
            var membership = Membership.Grant(suite.Id, user.Id, clock.Now.AddDays(-1)); membership.Revoke(user.Id, "Access ended before event", clock.Now);
            var account = RentAccount.Open(Guid.NewGuid(), membership.Id); var binding = BillingCustomerBinding.Create(account.Id, "acct_events", false, "cus_" + Guid.NewGuid().ToString("N"), clock.Now);
            var debt = Obligation.Issue(account.Id, new Money(1000), "Event debt", DateOnly.FromDateTime(clock.Now.UtcDateTime), user.Id, clock.Now);
            var attempt = PaymentAttempt.Admit(Guid.NewGuid(), user.Id, binding, [new(debt.Id, account.Id, debt.Version, 1000)], clock.Now);
            var work = CollectionWork.Enqueue(attempt, new("https://events.example.test", "pmc_events")); attempt.BeginDispatch(clock.Now); var received = Event();
            await using var db = create(); db.AddRange(user, building, suite, membership, account, binding, debt, attempt, work, received,
                new CollectionGuard { AttemptId = attempt.Id, AccountId = account.Id, ObligationId = debt.Id }); await db.SaveChangesAsync(); return (attempt, received);
        }
        static ProviderEventReference Reference(PaymentAttempt original, PaymentEventInbox received) => new(EventReferenceState.Known, original.Id, original.AccountId, original.PayloadHash, received.ResourceId);
        static ProviderCollectionObservation Paid(PaymentAttempt original, string session) => new(ProviderCollectionState.Paid, session, Settlement:
            new(original.ProviderAccount, original.LiveMode, original.CustomerId, session, "pi_" + original.Id.ToString("N"), original.TotalMinorUnits, original.Currency));
        async Task<PaymentEventLease?> Claim() { await using var db = create(); return await new PaymentEventQueue(db, clock).ClaimAsync(CancellationToken.None); }
        async Task<EventCollectionTarget?> Target(PaymentEventLease lease, ProviderEventReference reference)
        { await using var db = create(); return await new PaymentEventQueue(db, clock).LoadTargetAsync(lease, reference, CancellationToken.None); }
        async Task<bool> Complete(PaymentEventLease lease, ProviderEventReference reference, EventCollectionTarget? target, ProviderCollectionObservation? observation)
        { await using var db = create(); return await new PaymentEventQueue(db, clock).CompleteAsync(lease, reference, target, observation, CancellationToken.None); }
        var first = await Seed(); var contenders = await Task.WhenAll(Task.Run(Claim), Task.Run(Claim)); Assert.Single(contenders, x => x is not null);
        var stale = contenders.Single(x => x is not null)!; var reference = Reference(first.Attempt, first.Event); var target = (await Target(stale, reference))!;
        clock.Now = clock.Now.AddMinutes(2); var fresh = (await Claim())!; Assert.NotEqual(stale.Token, fresh.Token);
        Assert.Null(await Target(stale, reference)); Assert.False(await Complete(stale, reference, target, Paid(first.Attempt, reference.SessionId!)));
        await using (var db = create()) { Assert.False(await db.SettlementPayments.AnyAsync(x => x.AttemptId == first.Attempt.Id)); }
        Assert.True(await Complete(fresh, reference, (await Target(fresh, reference))!, Paid(first.Attempt, reference.SessionId!)));
        // A late Processing event is only a trigger: current canonical evidence still says Paid.
        var late = Event("payment_intent.processing", "pi_" + first.Attempt.Id.ToString("N"));
        await using (var db = create()) { db.Add(late); await db.SaveChangesAsync(); }
        var lateLease = (await Claim())!; var lateReference = reference; var lateTarget = (await Target(lateLease, lateReference))!;
        Assert.True(await Complete(lateLease, lateReference, lateTarget, Paid(lateTarget.Original, reference.SessionId!)));
        await using (var db = create())
        { Assert.Equal(1, await db.SettlementPayments.CountAsync(x => x.AttemptId == first.Attempt.Id)); Assert.Equal(PaymentEventState.Processed, (await db.PaymentEvents.SingleAsync(x => x.Id == late.Id)).State); }
        var adjusted = Event("charge.refunded", "ch_" + first.Attempt.Id.ToString("N"));
        await using (var db = create()) { db.Add(adjusted); await db.SaveChangesAsync(); }
        var adjustmentLease = (await Claim())!;
        Assert.True(await Complete(adjustmentLease, reference, (await Target(adjustmentLease, reference))!, new(ProviderCollectionState.Invalid)));
        await using (var db = create())
        {
            Assert.Equal(PaymentEventState.NeedsReview, (await db.PaymentEvents.SingleAsync(x => x.Id == adjusted.Id)).State);
            Assert.Equal(FinancialExceptionState.Open, (await db.FinancialExceptions.SingleAsync(x => x.AttemptId == first.Attempt.Id)).State);
            Assert.Equal(1, await db.SettlementPayments.CountAsync(x => x.AttemptId == first.Attempt.Id));
        }
        // A later Paid signal cannot clear an unclassified external-money case, even before its cash rows arrive.
        var afterAdjustment = Event("checkout.session.completed", reference.SessionId!);
        await using (var db = create()) { db.Add(afterAdjustment); await db.SaveChangesAsync(); }
        var afterLease = (await Claim())!; var afterTarget = (await Target(afterLease, reference))!;
        Assert.True(await Complete(afterLease, reference, afterTarget, Paid(afterTarget.Original, reference.SessionId!)));
        await using (var db = create())
        {
            Assert.Equal(FinancialExceptionState.Open, (await db.FinancialExceptions.SingleAsync(x => x.AttemptId == first.Attempt.Id)).State);
            Assert.Equal(PaymentEventState.NeedsReview, (await db.PaymentEvents.SingleAsync(x => x.Id == afterAdjustment.Id)).State);
        }
        var unmapped = await Seed(); var unmappedLease = (await Claim())!; var wrong = Reference(unmapped.Attempt, unmapped.Event) with { AccountId = Guid.NewGuid() };
        Assert.Null(await Target(unmappedLease, wrong)); Assert.True(await Complete(unmappedLease, wrong, null, null));
        await using (var db = create()) { Assert.Equal("UnmappedResource", (await db.PaymentEvents.SingleAsync(x => x.Id == unmapped.Event.Id)).ReviewReason); Assert.True(await db.CollectionGuards.AnyAsync(x => x.AttemptId == unmapped.Attempt.Id)); }
        var mismatched = await Seed(); var mismatchedLease = (await Claim())!; var mismatchedReference = Reference(mismatched.Attempt, mismatched.Event);
        var mismatchedTarget = (await Target(mismatchedLease, mismatchedReference))!;
        Assert.True(await Complete(mismatchedLease, mismatchedReference, mismatchedTarget, Paid(mismatched.Attempt, "cs_another")));
        await using (var db = create()) { Assert.False(await db.SettlementPayments.AnyAsync(x => x.AttemptId == mismatched.Attempt.Id)); Assert.True(await db.FinancialExceptions.AnyAsync(x => x.AttemptId == mismatched.Attempt.Id)); }
        var rollback = await Seed(); var rollbackLease = (await Claim())!; var rollbackReference = Reference(rollback.Attempt, rollback.Event); var rollbackTarget = (await Target(rollbackLease, rollbackReference))!;
        await using (var db = create())
        {
            db.SavingChanges += (_, _) => { foreach (var entry in db.ChangeTracker.Entries<PaymentAllocation>().Where(x => x.State == EntityState.Added)) entry.Property(x => x.AmountMinorUnits).CurrentValue = 0; };
            Assert.True(await new PaymentEventQueue(db, clock).CompleteAsync(rollbackLease, rollbackReference, rollbackTarget, Paid(rollback.Attempt, rollback.Event.ResourceId), CancellationToken.None));
        }
        await using (var db = create()) { Assert.False(await db.SettlementPayments.AnyAsync(x => x.AttemptId == rollback.Attempt.Id)); Assert.Equal(PaymentEventState.Pending, (await db.PaymentEvents.SingleAsync(x => x.Id == rollback.Event.Id)).State); }
        clock.Now = clock.Now.AddMinutes(1); var repair = (await Claim())!;
        Assert.True(await Complete(repair, rollbackReference, (await Target(repair, rollbackReference))!, Paid(rollback.Attempt, rollback.Event.ResourceId)));
        var uncertain = Event(); await using (var db = create()) { db.Add(uncertain); await db.SaveChangesAsync(); }
        for (var index = 0; index < 5; index++) { var retry = (await Claim())!; Assert.Equal(uncertain.Id, retry.Original.Id); Assert.True(await Complete(retry, new(EventReferenceState.Unknown), null, null)); clock.Now = clock.Now.AddMinutes(10); }
        await using (var db = create()) { var row = await db.PaymentEvents.SingleAsync(x => x.Id == uncertain.Id); Assert.Equal(5, row.Failures); Assert.Equal(PaymentEventState.NeedsReview, row.State); }
        var unsupported = Event("invoice.finalized", "in_unsupported"); await using (var db = create()) { db.Add(unsupported); await db.SaveChangesAsync(); }
        Assert.True(await Complete((await Claim())!, new(EventReferenceState.Unsupported), null, null));
        await using (var db = create()) { Assert.Equal("UnsupportedResource", (await db.PaymentEvents.SingleAsync(x => x.Id == unsupported.Id)).ReviewReason); }
        var aged = Event(); await using (var db = create()) { db.Add(aged); await db.SaveChangesAsync(); }
        clock.Now = clock.Now.AddHours(24); Assert.Null(await Claim());
        await using (var db = create()) { Assert.Equal("RetryLimit", (await db.PaymentEvents.SingleAsync(x => x.Id == aged.Id)).ReviewReason); }
        var worker = await Seed();
        await using (var db = create())
        {
            var synthetic = new CanonicalReader(Reference(worker.Attempt, worker.Event), original => { Assert.Null(db.Database.CurrentTransaction); return Paid(original, worker.Event.ResourceId); });
            Assert.True(await new PaymentEventWorker(new PaymentEventQueue(db, clock), synthetic, synthetic).ProcessOneAsync()); Assert.Equal(1, synthetic.Reads);
        }
        await using (var db = create()) { Assert.Equal(1, await db.SettlementPayments.CountAsync(x => x.AttemptId == worker.Attempt.Id)); Assert.Equal(PaymentEventState.Processed, (await db.PaymentEvents.SingleAsync(x => x.Id == worker.Event.Id)).State); }
    }
    private sealed class EventClock : TimeProvider
    { public DateTimeOffset Now { get; set; } = new(2021, 1, 1, 12, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class CanonicalReader(ProviderEventReference reference, Func<PaymentAttempt, ProviderCollectionObservation> read) : IPaymentEventResolver, ICollectionGatewayFactory, ICollectionGateway
    {
        public int Reads { get; private set; }
        public Task<ProviderEventReference> ResolveAsync(PaymentEventInbox original, CancellationToken cancellationToken) => Task.FromResult(reference);
        public ICollectionGateway Open(PaymentAttempt original, CheckoutProfile profile) => this;
        public Task<ProviderCollectionObservation> ReadAsync(PaymentAttempt original, string sessionId, CancellationToken cancellationToken = default) { Reads++; return Task.FromResult(read(original)); }
        public Task<ProviderCollectionObservation> CreateAsync(PaymentAttempt original, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Events must never create collection.");
        public Task<ProviderCollectionObservation> ExpireAsync(PaymentAttempt original, string sessionId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Events must never expire collection.");
    }
}
