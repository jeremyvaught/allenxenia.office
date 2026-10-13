using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Infrastructure.Persistence;

// Use a new DI scope per operation; provider work never runs inside this queue's transactions.
public sealed class CollectionWorkQueue(OfficeDbContext db, TimeProvider time) : ICollectionWorkQueue
{
    public async Task<CollectionLease?> ClaimAsync(CancellationToken token)
    {
        var now = time.GetUtcNow();
        await using var transaction = await AuthorityLocks.BeginAsync(db, token);
        var candidate = await db.CollectionWork.AsNoTracking().Where(x => (x.State == CollectionWorkState.Queued && x.NextAttemptAt <= now) ||
            (x.State == CollectionWorkState.Leased && x.LeaseUntil <= now)).OrderBy(x => x.CreatedAt).ThenBy(x => x.AttemptId).Select(x => (Guid?)x.AttemptId).FirstOrDefaultAsync(token);
        if (candidate is null) return null;
        var owner = await (from attempt in db.PaymentAttempts.AsNoTracking()
                           join account in db.RentAccounts on attempt.AccountId equals account.Id
                           where attempt.Id == candidate
                           select new { attempt.ActorId, attempt.AccountId, account.MembershipId }).SingleAsync(token);
        await AuthorityLocks.UserAsync(db, owner.ActorId, token); await AuthorityLocks.MembershipAsync(db, owner.MembershipId, token);
        await Lock(owner.AccountId, candidate.Value, token);
        var original = await db.PaymentAttempts.Include(x => x.Items).SingleAsync(x => x.Id == candidate, token); await db.Entry(original).ReloadAsync(token);
        var work = await db.CollectionWork.SingleAsync(x => x.AttemptId == candidate, token); await db.Entry(work).ReloadAsync(token);
        if (!work.Eligible(now)) return null;
        if (!work.ReadOnlyReconciliation && original.State is (PaymentAttemptState.Settled or PaymentAttemptState.Uncollected))
        {
            work.FinishRecorded(now); await db.SaveChangesAsync(token); await transaction.CommitAsync(token); return null;
        }
        if (!work.Claim(now))
        {
            await Review(original, "RetryLimit", token); await db.SaveChangesAsync(token); await transaction.CommitAsync(token); return null;
        }
        if (work.ReadOnlyReconciliation && original.SessionId is null)
        {
            work.Review("CanonicalReviewRequired"); await Review(original, "OriginalWindowEnded", token);
            await db.SaveChangesAsync(token); await transaction.CommitAsync(token); return null;
        }
        if (original.State == PaymentAttemptState.Admitted)
        {
            var validAccess = await db.Memberships.AsNoTracking().AnyAsync(x => x.Id == owner.MembershipId && x.UserId == original.ActorId && x.RevokedAt == null &&
                x.StartsAt <= now && (x.EndsAt == null || x.EndsAt > now) && db.ActiveGrants.Any(g => g.MembershipId == x.Id), token) &&
                await db.Users.AsNoTracking().AnyAsync(x => x.Id == original.ActorId && x.EmailConfirmed && (!x.LockoutEnabled || x.LockoutEnd == null || x.LockoutEnd <= now), token);
            if (!validAccess || now < original.CreatedAt || now >= original.CreatedAt.AddHours(23))
            {
                original.CancelBeforeDispatch(); db.CollectionGuards.RemoveRange(await db.CollectionGuards.Where(x => x.AttemptId == original.Id).ToListAsync(token));
                work.Complete(work.LeaseToken, CollectionWorkOutcome.Suppressed, now);
                await db.SaveChangesAsync(token); await transaction.CommitAsync(token); return null;
            }
            var valid = !await db.CashSourceWork.AnyAsync(x => x.AccountId == original.AccountId && x.State != CollectionWorkState.Completed, token) &&
                !await db.FinancialExceptions.AnyAsync(x => x.AccountId == original.AccountId && x.State != FinancialExceptionState.Resolved, token) &&
                !await db.CashMovements.AnyAsync(m => m.AccountId == original.AccountId && !db.CashMovementReviews.Any(r => r.MovementId == m.Id && r.State == CashMovementReviewState.Classified && r.VerifiedAt != null && r.AgreementHash != null && r.AgreementHash.Length == 64), token);
            var guards = await db.CollectionGuards.AsNoTracking().Where(x => x.AttemptId == original.Id).ToListAsync(token);
            valid &= guards.Count == original.Items.Count;
            foreach (var item in original.Items.OrderBy(x => x.ObligationId.ToString("D"), StringComparer.Ordinal))
            {
                if (db is MySqlOfficeDbContext) await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM obligations WHERE id = {item.ObligationId.ToString("D")} FOR UPDATE", token);
                var obligation = await db.Obligations.AsNoTracking().SingleAsync(x => x.Id == item.ObligationId, token);
                var paid = await db.PaymentAllocations.Where(x => x.ObligationId == item.ObligationId).SumAsync(x => (long?)x.AmountMinorUnits, token) ?? 0;
                var returned = await db.RefundAllocations.Where(x => x.ObligationId == item.ObligationId).SumAsync(x => (long?)x.SignedMinorUnits, token) ?? 0;
                var credited = await db.ObligationCredits.Where(x => x.ObligationId == item.ObligationId).SumAsync(x => (long?)x.AmountMinorUnits, token) ?? 0;
                valid &= !await db.ProviderRentInvoices.AnyAsync(x => x.ObligationId == item.ObligationId, token) &&
                    obligation.Version == item.ReviewedVersion && obligation.AmountMinorUnits - credited - paid - returned == item.AmountMinorUnits &&
                    guards.Any(x => x.ObligationId == item.ObligationId && x.AccountId == original.AccountId);
            }
            if (!valid)
            {
                work.Review("AdmissionChanged"); await Review(original, "AdmissionChanged", token);
                await db.SaveChangesAsync(token); await transaction.CommitAsync(token); return null;
            }
            original.BeginDispatch(now);
        }
        if (original.SessionId is null && now >= original.CreatedAt.AddHours(23))
        {
            work.Review("OriginalWindowEnded"); await Review(original, "OriginalWindowEnded", token);
            await db.SaveChangesAsync(token); await transaction.CommitAsync(token); return null;
        }
        await db.SaveChangesAsync(token); await transaction.CommitAsync(token);
        return new(original, work.LeaseToken, work.Profile, work.OriginTraceId, work.OriginSpanId, work.ReadOnlyReconciliation);
    }

    public async Task<bool> CompleteAsync(CollectionLease lease, ProviderCollectionObservation observation, CancellationToken token)
    {
        // Projection checks the same live fence inside its account/attempt transaction.
        var projection = await new CollectionReconciliationStore(db, time).ApplyLeasedAsync(lease, observation, token);
        // A failed projection must never leak tracked financial writes into bookkeeping.
        db.ChangeTracker.Clear();
        if (projection.Code == ReconciliationCode.Unavailable) return false;
        await using var transaction = await AuthorityLocks.BeginAsync(db, token); await Lock(lease.Original.AccountId, lease.Original.Id, token);
        var work = await db.CollectionWork.SingleOrDefaultAsync(x => x.AttemptId == lease.Original.Id, token); if (work is null) return false;
        await db.Entry(work).ReloadAsync(token); var now = time.GetUtcNow(); if (!work.Owns(lease.Token, now)) return false;
        var state = await db.PaymentAttempts.AsNoTracking().Where(x => x.Id == lease.Original.Id).Select(x => x.State).SingleAsync(token);
        var outcome = state == PaymentAttemptState.Settled && projection.Code == ReconciliationCode.NeedsReview ? CollectionWorkOutcome.Review :
            state is PaymentAttemptState.Settled or PaymentAttemptState.Uncollected ? CollectionWorkOutcome.Finished :
            projection.Code == ReconciliationCode.Conflict || observation.State == ProviderCollectionState.Unknown ? CollectionWorkOutcome.Retry :
            projection.Code == ReconciliationCode.NeedsReview ? CollectionWorkOutcome.Review : CollectionWorkOutcome.Waiting;
        if (work.ReadOnlyReconciliation)
            outcome = projection.Code is ReconciliationCode.Applied or ReconciliationCode.AlreadyApplied ? CollectionWorkOutcome.Finished :
                projection.Code == ReconciliationCode.Conflict || observation.State == ProviderCollectionState.Unknown || observation.CashInventory?.State == CashObservationState.Unknown ? CollectionWorkOutcome.Retry :
                observation.CashInventory?.State == CashObservationState.Pending ? CollectionWorkOutcome.Waiting : CollectionWorkOutcome.Review;
        work.Complete(lease.Token, outcome, now);
        await db.SaveChangesAsync(token); await transaction.CommitAsync(token); return true;
    }
    internal Task<int> Lock(Guid accountId, Guid attemptId, CancellationToken token) => db is MySqlOfficeDbContext
        ? LockMySql(accountId, attemptId, token) : Task.FromResult(0);
    private async Task<int> LockMySql(Guid accountId, Guid attemptId, CancellationToken token)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM rent_accounts WHERE id = {accountId.ToString("D")} FOR UPDATE", token);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM payment_attempts WHERE id = {attemptId.ToString("D")} FOR UPDATE", token);
        return await db.Database.ExecuteSqlInterpolatedAsync($"SELECT attempt_id FROM collection_work WHERE attempt_id = {attemptId.ToString("D")} FOR UPDATE", token);
    }
    private async Task Review(PaymentAttempt original, string reason, CancellationToken token)
    {
        if (original.State is not (PaymentAttemptState.Settled or PaymentAttemptState.Uncollected)) original.RequireReview();
        var issue = await db.FinancialExceptions.SingleOrDefaultAsync(x => x.AttemptId == original.Id, token); var now = time.GetUtcNow();
        var failure = reason == "AdmissionChanged" ? FinancialFailure.LedgerChanged : FinancialFailure.ProviderUnknown;
        var action = "Opened";
        if (issue is null) { issue = FinancialException.Open(original, failure, null, now); db.FinancialExceptions.Add(issue); }
        else { await db.Entry(issue).ReloadAsync(token); action = issue.State == FinancialExceptionState.Resolved ? "Reopened" : "Observed"; issue.Observe(failure, null, now); }
        db.FinancialExceptionHistory.Add(FinancialExceptionHistory.Record(issue.Id, action, null, reason, now));
    }
}
