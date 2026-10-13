using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Infrastructure.Persistence;

public sealed class PaymentEventQueue(OfficeDbContext db, TimeProvider time) : IPaymentEventQueue
{
    public async Task<PaymentEventLease?> ClaimAsync(CancellationToken token)
    {
        var now = time.GetUtcNow(); await using var transaction = await AuthorityLocks.BeginAsync(db, token);
        var row = await db.PaymentEvents.Where(x => (x.State == PaymentEventState.Pending && x.NextAttemptAt <= now) ||
            (x.State == PaymentEventState.Processing && x.LeaseUntil <= now)).OrderBy(x => x.ReceivedAt).ThenBy(x => x.Id).FirstOrDefaultAsync(token);
        if (row is null) return null;
        await Lock(row.Id, token); await db.Entry(row).ReloadAsync(token); if (!row.Eligible(now)) return null;
        var claimed = row.Claim(now); await db.SaveChangesAsync(token); await transaction.CommitAsync(token);
        return claimed ? new(row, row.LeaseToken) : null;
    }
    public async Task<EventCollectionTarget?> LoadTargetAsync(PaymentEventLease lease, ProviderEventReference reference, CancellationToken token)
    {
        if (!ValidReference(reference) || !await Owns(lease, token)) return null;
        var original = await db.PaymentAttempts.AsNoTracking().Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == reference.AttemptId, token);
        if (original is null || !SameScope(lease, reference, original)) return null;
        var work = await db.CollectionWork.AsNoTracking().SingleOrDefaultAsync(x => x.AttemptId == original.Id, token);
        return work is null ? null : new(original, work.Profile);
    }
    public async Task<bool> CompleteAsync(PaymentEventLease lease, ProviderEventReference reference, EventCollectionTarget? target,
        ProviderCollectionObservation? observation, CancellationToken token)
    {
        ReconciliationResult? projection = null;
        if (observation is { State: ProviderCollectionState.Open or ProviderCollectionState.Processing or ProviderCollectionState.Paid or ProviderCollectionState.Uncollected } && observation.SessionId != reference.SessionId)
            observation = new(ProviderCollectionState.Invalid);
        if (reference.State == EventReferenceState.Known && target is not null && observation is not null && ValidReference(reference) && SameScope(lease, reference, target.Original))
            projection = await new CollectionReconciliationStore(db, time).ApplyEventAsync(lease, target.Original, observation, token);
        db.ChangeTracker.Clear();
        if (projection?.Code == ReconciliationCode.Unavailable) return false;
        await using var transaction = await AuthorityLocks.BeginAsync(db, token); await Lock(lease.Original.Id, token);
        var row = await db.PaymentEvents.SingleOrDefaultAsync(x => x.Id == lease.Original.Id, token); if (row is null) return false;
        await db.Entry(row).ReloadAsync(token); var now = time.GetUtcNow(); if (!row.Owns(lease.Token, now)) return false;
        var processed = projection?.Code is ReconciliationCode.Applied or ReconciliationCode.AlreadyApplied;
        var retry = reference.State == EventReferenceState.Unknown || observation?.State == ProviderCollectionState.Unknown || projection?.Code == ReconciliationCode.Conflict;
        var reason = reference.State switch
        {
            EventReferenceState.Unsupported => "UnsupportedResource",
            EventReferenceState.Invalid => "ProviderMismatch",
            EventReferenceState.Known when target is null || projection is null => "UnmappedResource",
            _ => "CanonicalReviewRequired"
        };
        row.Complete(lease.Token, processed && !retry, retry, reason, now);
        await db.SaveChangesAsync(token); await transaction.CommitAsync(token); return true;
    }
    private async Task<bool> Owns(PaymentEventLease lease, CancellationToken token)
    {
        var row = await db.PaymentEvents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == lease.Original.Id, token);
        return row is not null && row.Owns(lease.Token, time.GetUtcNow()) && row.SameEnvelope(lease.Original);
    }
    private Task<int> Lock(Guid id, CancellationToken token) => db is MySqlOfficeDbContext
        ? db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM payment_events WHERE id = {id.ToString("D")} FOR UPDATE", token) : Task.FromResult(0);
    private static bool ValidReference(ProviderEventReference reference) => reference.State == EventReferenceState.Known && reference.AttemptId is { } attempt && attempt != Guid.Empty &&
        reference.AccountId is { } account && account != Guid.Empty && reference.ReviewHash is { Length: 64 } hash && hash.All(Uri.IsHexDigit) &&
        reference.SessionId is { } session && PaymentReferences.Valid(session, "cs_");
    private static bool SameScope(PaymentEventLease lease, ProviderEventReference reference, PaymentAttempt original) =>
        original.ProviderAccount == lease.Original.ProviderAccount && original.LiveMode == lease.Original.LiveMode && original.Id == reference.AttemptId &&
        original.AccountId == reference.AccountId && original.PayloadHash == reference.ReviewHash && (original.SessionId is null || original.SessionId == reference.SessionId) &&
        (!lease.Original.EventType.StartsWith("checkout.session.", StringComparison.Ordinal) || lease.Original.ResourceId == reference.SessionId);
}
