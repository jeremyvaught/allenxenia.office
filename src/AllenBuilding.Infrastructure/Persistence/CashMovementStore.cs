using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Infrastructure.Persistence;

// Internal canonical projection. No HTTP money setter; authorized, fenced admission must precede runtime activation.
public sealed class CashMovementStore(OfficeDbContext db, TimeProvider time) : ICashMovementStore
{
    public Task<PaymentAttempt?> LoadAsync(Guid attemptId, CancellationToken token) => db.PaymentAttempts.AsNoTracking().Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == attemptId, token);
    public async Task<ReconciliationResult> ApplyAsync(Guid attemptId, Guid version, CashObservation observation, CancellationToken token)
    {
        await using var tx = await AuthorityLocks.BeginAsync(db, token);
        var accountId = await db.PaymentAttempts.AsNoTracking().Where(x => x.Id == attemptId).Select(x => (Guid?)x.AccountId).SingleOrDefaultAsync(token);
        if (accountId is null) return new(ReconciliationCode.Unavailable);
        await CustomerProvisionQueue.LockAccount(db, accountId.Value, token);
        if (db is MySqlOfficeDbContext) await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM payment_attempts WHERE id = {attemptId.ToString("D")} FOR UPDATE", token);
        var original = await db.PaymentAttempts.Include(x => x.Items).SingleAsync(x => x.Id == attemptId, token); await db.Entry(original).ReloadAsync(token);
        if (original.Version != version) return new(ReconciliationCode.Conflict);
        var capture = await db.SettlementPayments.AsNoTracking().SingleOrDefaultAsync(x => x.AttemptId == attemptId, token);
        if (original.State != PaymentAttemptState.Settled || capture is null || observation.State != CashObservationState.Verified ||
            observation.Movements is not { Count: >= 1 and <= 25 } || observation.Movements.Any(x => x is null) ||
            observation.Movements.Select(x => x.TransactionId).Distinct(StringComparer.Ordinal).Count() != observation.Movements.Count)
        {
            await Review(original, observation.State == CashObservationState.Unknown ? FinancialFailure.ProviderUnknown : FinancialFailure.ProviderMismatch, token);
            await db.SaveChangesAsync(token); await tx.CommitAsync(token); return new(ReconciliationCode.NeedsReview);
        }
        var additions = new List<CashMovement>();
        foreach (var fact in observation.Movements)
        {
            CashMovement movement;
            try { movement = CashMovement.Record(capture, original, fact, time.GetUtcNow()); }
            catch (InvalidOperationException)
            {
                await Review(original, FinancialFailure.ProviderMismatch, token); await db.SaveChangesAsync(token); await tx.CommitAsync(token); return new(ReconciliationCode.NeedsReview);
            }
            var recorded = await db.CashMovements.AsNoTracking().SingleOrDefaultAsync(x => x.ProviderAccount == fact.ProviderAccount && x.LiveMode == fact.LiveMode && x.TransactionId == fact.TransactionId, token);
            if (recorded is not null)
            {
                if (recorded.Matches(fact)) continue;
                await Review(original, FinancialFailure.ProviderMismatch, token); await db.SaveChangesAsync(token); await tx.CommitAsync(token); return new(ReconciliationCode.NeedsReview);
            }
            additions.Add(movement);
        }
        if (additions.Count == 0) return new(ReconciliationCode.AlreadyApplied, capture.Id);
        foreach (var item in original.Items.OrderBy(x => x.ObligationId.ToString("D"), StringComparer.Ordinal))
        {
            if (db is MySqlOfficeDbContext) await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM obligations WHERE id = {item.ObligationId.ToString("D")} FOR UPDATE", token);
            var debt = await db.Obligations.SingleAsync(x => x.Id == item.ObligationId, token); await db.Entry(debt).ReloadAsync(token); debt.FinancialStateChanged();
        }
        foreach (var movement in additions) { db.CashMovements.Add(movement); db.CashMovementReviews.Add(CashMovementReview.Open(movement)); }
        await Review(original, FinancialFailure.ExternalCashMovement, token);
        try { await db.SaveChangesAsync(token); await tx.CommitAsync(token); return new(ReconciliationCode.Applied, capture.Id); }
        catch (DbUpdateException) { await tx.RollbackAsync(token); db.ChangeTracker.Clear(); return new(ReconciliationCode.Conflict); }
    }
    private async Task Review(PaymentAttempt original, FinancialFailure failure, CancellationToken token)
    {
        var issue = await db.FinancialExceptions.SingleOrDefaultAsync(x => x.AttemptId == original.Id, token); var action = "Opened"; var now = time.GetUtcNow();
        if (issue is null) { issue = FinancialException.Open(original, failure, null, now); db.FinancialExceptions.Add(issue); }
        else { await db.Entry(issue).ReloadAsync(token); action = issue.State == FinancialExceptionState.Resolved ? "Reopened" : "Observed"; issue.Observe(failure, null, now); }
        db.FinancialExceptionHistory.Add(FinancialExceptionHistory.Record(issue.Id, action, null, failure.ToString(), now));
    }
}
