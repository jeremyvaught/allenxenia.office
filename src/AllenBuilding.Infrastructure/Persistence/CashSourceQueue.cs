using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Infrastructure.Persistence;

// New scope per operation. Provider reads run outside these account/original/job transactions.
public sealed class CashSourceQueue(OfficeDbContext db, TimeProvider time) : ICashSourceQueue
{
    public async Task<CashSourceLease?> ClaimAsync(CancellationToken token)
    {
        var now = time.GetUtcNow();
        await using var tx = await AuthorityLocks.BeginAsync(db, token);
        var candidate = await db.CashSourceWork.AsNoTracking().Where(x =>
            (x.State == CollectionWorkState.Queued && x.NextAttemptAt <= now) || (x.State == CollectionWorkState.Leased && x.LeaseUntil <= now))
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Select(x => new { x.Id, x.AccountId, x.AttemptId }).FirstOrDefaultAsync(token);
        if (candidate is null) return null;
        await LockAsync(candidate.AccountId, candidate.AttemptId, candidate.Id, token);
        var work = await db.CashSourceWork.SingleAsync(x => x.Id == candidate.Id, token); await db.Entry(work).ReloadAsync(token);
        now = time.GetUtcNow(); if (!work.Eligible(now)) return null;
        var original = await db.PaymentAttempts.Include(x => x.Items).SingleAsync(x => x.Id == candidate.AttemptId, token); await db.Entry(original).ReloadAsync(token);
        if (!work.Claim(now))
        {
            await ReviewAsync(original, FinancialFailure.ProviderUnknown, token); await db.SaveChangesAsync(token); await tx.CommitAsync(token); return null;
        }
        await db.SaveChangesAsync(token); await tx.CommitAsync(token);
        return new(work.Id, original, work.SourceId, work.ApiVersion, work.LeaseToken, work.OperationId, work.OriginTraceId, work.OriginSpanId);
    }

    public async Task<bool> CompleteAsync(CashSourceLease lease, CashObservation observation, CancellationToken token)
    {
        // Never trust a worker-supplied replacement source, original or API contract.
        await using var tx = await AuthorityLocks.BeginAsync(db, token);
        var owner = await db.CashSourceWork.AsNoTracking().Where(x => x.Id == lease.Id).Select(x => new { x.AccountId, x.AttemptId }).SingleOrDefaultAsync(token);
        if (owner is null) return false;
        await LockAsync(owner.AccountId, owner.AttemptId, lease.Id, token);
        var work = await db.CashSourceWork.SingleAsync(x => x.Id == lease.Id, token); await db.Entry(work).ReloadAsync(token);
        if (!work.Owns(lease.Token, time.GetUtcNow()) || work.SourceId != lease.SourceId || work.ApiVersion != lease.ApiVersion || work.OperationId != lease.OperationId ||
            work.AttemptId != lease.Original.Id || work.AccountId != lease.Original.AccountId || work.ProviderAccount != lease.Original.ProviderAccount || work.LiveMode != lease.Original.LiveMode) return false;
        var original = await db.PaymentAttempts.Include(x => x.Items).SingleAsync(x => x.Id == work.AttemptId, token); await db.Entry(original).ReloadAsync(token);
        if (original.Version != lease.Original.Version || original.CustomerId != lease.Original.CustomerId || original.PaymentId != lease.Original.PaymentId) return false;
        var expiresAt = work.LeaseUntil!.Value;
        var outcome = CollectionWorkOutcome.Review;
        if (observation is null || !Enum.IsDefined(observation.State)) observation = new(CashObservationState.Invalid);
        if (observation.State == CashObservationState.Verified)
        {
            if (work.ApiVersion != CheckoutProfile.ApiContract || observation.Movements is not { Count: >= 1 and <= 25 } || observation.Movements.Any(x => x is null || x.SourceId != work.SourceId))
                observation = new(CashObservationState.Invalid);
            else
            {
                var projection = await new CashMovementStore(db, time).ApplyLockedAsync(original, original.Version, observation, token);
                outcome = projection.Code is ReconciliationCode.Applied or ReconciliationCode.AlreadyApplied ? CollectionWorkOutcome.Finished : CollectionWorkOutcome.Review;
            }
        }
        if (observation.State != CashObservationState.Verified)
        {
            outcome = observation.State switch { CashObservationState.Unknown => CollectionWorkOutcome.Retry, CashObservationState.Pending => CollectionWorkOutcome.Waiting, _ => CollectionWorkOutcome.Review };
            await ReviewAsync(original, observation.State == CashObservationState.Invalid ? FinancialFailure.ProviderMismatch : FinancialFailure.ProviderUnknown, token);
        }
        // Cash, review/history and job completion share one transaction and the same live lease fence.
        if (!work.Complete(lease.Token, outcome, time.GetUtcNow())) { await tx.RollbackAsync(token); db.ChangeTracker.Clear(); return false; }
        try
        {
            await db.SaveChangesAsync(token);
            // Completing changes State/LeaseUntil, so compare the expiry captured before completing instead of Owns.
            if (time.GetUtcNow() >= expiresAt) { await tx.RollbackAsync(token); db.ChangeTracker.Clear(); return false; }
            await tx.CommitAsync(token); return true;
        }
        catch (DbUpdateException) { await tx.RollbackAsync(token); db.ChangeTracker.Clear(); return false; }
    }
    private async Task LockAsync(Guid account, Guid attempt, Guid work, CancellationToken token)
    {
        await CustomerProvisionQueue.LockAccount(db, account, token);
        if (db is MySqlOfficeDbContext)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM payment_attempts WHERE id = {attempt.ToString("D")} FOR UPDATE", token);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM cash_source_work WHERE id = {work.ToString("D")} FOR UPDATE", token);
        }
    }
    private async Task ReviewAsync(PaymentAttempt original, FinancialFailure failure, CancellationToken token)
    {
        var issue = await db.FinancialExceptions.SingleOrDefaultAsync(x => x.AttemptId == original.Id, token); var action = "Opened"; var now = time.GetUtcNow();
        if (issue is null) { issue = FinancialException.Open(original, failure, null, now); db.FinancialExceptions.Add(issue); }
        else { await db.Entry(issue).ReloadAsync(token); action = issue.State == FinancialExceptionState.Resolved ? "Reopened" : "Observed"; issue.Observe(failure, null, now); }
        db.FinancialExceptionHistory.Add(FinancialExceptionHistory.Record(issue.Id, action, null, failure.ToString(), now));
    }
}
