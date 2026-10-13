using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Infrastructure.Persistence;

// Dormant until worker fencing and the authorized financial exception workflow are complete.
public sealed class CollectionReconciliationStore(OfficeDbContext db, TimeProvider time) : ICollectionReconciliationStore
{
    public Task<PaymentAttempt?> LoadAsync(Guid attemptId, CancellationToken cancellationToken) => db.PaymentAttempts.AsNoTracking().Include(x => x.Items)
        .SingleOrDefaultAsync(x => x.Id == attemptId, cancellationToken);

    public async Task<ReconciliationResult> ApplyAsync(Guid attemptId, Guid version, ProviderCollectionObservation observation, CancellationToken cancellationToken)
    {
        await using var transaction = await AuthorityLocks.BeginAsync(db, cancellationToken);
        var accountId = await db.PaymentAttempts.AsNoTracking().Where(x => x.Id == attemptId).Select(x => (Guid?)x.AccountId).SingleOrDefaultAsync(cancellationToken);
        if (accountId is null) return new(ReconciliationCode.Unavailable);
        if (db is MySqlOfficeDbContext)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM rent_accounts WHERE id = {accountId.Value.ToString("D")} FOR UPDATE", cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM payment_attempts WHERE id = {attemptId.ToString("D")} FOR UPDATE", cancellationToken);
        }
        // The same scope may already track a stale attempt; reload before the canonical projection.
        var attempt = await db.PaymentAttempts.Include(x => x.Items).SingleAsync(x => x.Id == attemptId, cancellationToken);
        await db.Entry(attempt).ReloadAsync(cancellationToken);
        if (attempt.Version != version) return new(ReconciliationCode.Conflict);
        var payment = await db.SettlementPayments.AsNoTracking().Include(x => x.Allocations).SingleOrDefaultAsync(x => x.AttemptId == attempt.Id, cancellationToken);
        if (attempt.State == PaymentAttemptState.Settled)
        {
            var fact = observation.Settlement;
            var agrees = observation.State == ProviderCollectionState.Paid && payment is not null && fact is not null && observation.SessionId == fact.SessionId &&
                fact.ProviderAccount == attempt.ProviderAccount && fact.LiveMode == attempt.LiveMode && fact.CustomerId == attempt.CustomerId &&
                fact.SessionId == attempt.SessionId && fact.PaymentId == attempt.PaymentId && payment.CollectionReference == fact.PaymentId &&
                fact.AmountMinorUnits == attempt.TotalMinorUnits && fact.AmountMinorUnits == payment.AmountMinorUnits && fact.Currency == payment.Currency &&
                payment.Allocations.Count == attempt.Items.Count && payment.Allocations.All(x => attempt.Items.Any(i =>
                    i.ObligationId == x.ObligationId && i.AccountId == x.AccountId && i.AmountMinorUnits == x.AmountMinorUnits));
            if (agrees) await ResolveIssue(attempt.Id, observation, cancellationToken);
            else await ObserveIssue(attempt, FinancialFailure.TerminalDisagreement, observation, cancellationToken);
            return await Commit(agrees ? new(ReconciliationCode.AlreadyApplied, payment!.Id) : new(ReconciliationCode.NeedsReview), transaction, cancellationToken);
        }
        if (attempt.State == PaymentAttemptState.Uncollected && observation.State == ProviderCollectionState.Uncollected &&
            observation.SessionId is { } finalSession && finalSession == attempt.SessionId && payment is null)
        {
            await ResolveIssue(attempt.Id, observation, cancellationToken);
            return await Commit(new(ReconciliationCode.AlreadyApplied), transaction, cancellationToken);
        }
        if (attempt.State is PaymentAttemptState.Admitted or PaymentAttemptState.Uncollected || payment is not null)
        {
            await ObserveIssue(attempt, FinancialFailure.TerminalDisagreement, observation, cancellationToken);
            return await Commit(new(ReconciliationCode.NeedsReview), transaction, cancellationToken);
        }
        var obligations = new List<Obligation>();
        foreach (var item in attempt.Items.OrderBy(x => x.ObligationId.ToString("D"), StringComparer.Ordinal))
        {
            if (db is MySqlOfficeDbContext)
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM obligations WHERE id = {item.ObligationId.ToString("D")} FOR UPDATE", cancellationToken);
            var obligation = await db.Obligations.SingleAsync(x => x.Id == item.ObligationId, cancellationToken);
            await db.Entry(obligation).ReloadAsync(cancellationToken); obligations.Add(obligation);
        }
        var guards = await db.CollectionGuards.Where(x => x.AttemptId == attempt.Id).ToListAsync(cancellationToken);
        var completeGuards = attempt.Items.Count is >= 1 and <= 25 && guards.Count == attempt.Items.Count &&
            attempt.Items.All(x => x.AccountId == attempt.AccountId && x.AmountMinorUnits > 0 && guards.Any(g => g.ObligationId == x.ObligationId && g.AccountId == x.AccountId));
        var changed = obligations.Any(x => attempt.Items.Single(i => i.ObligationId == x.Id).ReviewedVersion != x.Version);
        var review = observation.State is ProviderCollectionState.Invalid or ProviderCollectionState.Unknown || !completeGuards || changed;
        SettlementPayment? recorded = null;
        if (!review)
        {
            try
            {
                switch (observation.State)
                {
                    case ProviderCollectionState.Paid when observation.Settlement is { } fact && observation.SessionId == fact.SessionId:
                        foreach (var obligation in obligations)
                        {
                            var allocated = await db.PaymentAllocations.Where(x => x.ObligationId == obligation.Id).SumAsync(x => (long?)x.AmountMinorUnits, cancellationToken) ?? 0;
                            if (obligation.AmountMinorUnits - allocated != attempt.Items.Single(i => i.ObligationId == obligation.Id).AmountMinorUnits)
                                throw new InvalidOperationException("The ledger no longer matches the reviewed allocation.");
                        }
                        recorded = SettlementPayment.Record(attempt, fact, time.GetUtcNow()); db.SettlementPayments.Add(recorded);
                        foreach (var obligation in obligations) obligation.FinancialStateChanged();
                        db.CollectionGuards.RemoveRange(guards); break;
                    case ProviderCollectionState.Uncollected when observation.SessionId is { } expired:
                        attempt.ConfirmVerifiedUncollected(attempt.ProviderAccount, attempt.LiveMode, attempt.CustomerId, expired);
                        db.CollectionGuards.RemoveRange(guards); break;
                    case ProviderCollectionState.Processing when observation.SessionId is { } processing:
                        attempt.ObserveVerifiedProcessing(attempt.ProviderAccount, attempt.LiveMode, attempt.CustomerId, processing); break;
                    case ProviderCollectionState.Open when observation.SessionId is { } session && observation.ExpiresAt is { } expires:
                        if (attempt.State == PaymentAttemptState.HandoffReady && attempt.SessionId == session) break;
                        attempt.AttachHandoff(session, expires, time.GetUtcNow()); break;
                    default: review = true; break;
                }
            }
            catch (InvalidOperationException) { review = true; }
        }
        if (review)
        {
            attempt.RequireReview();
            var failure = !completeGuards ? FinancialFailure.ClaimsIncomplete : changed ? FinancialFailure.LedgerChanged :
                observation.State == ProviderCollectionState.Unknown ? FinancialFailure.ProviderUnknown : FinancialFailure.ProviderMismatch;
            await ObserveIssue(attempt, failure, observation, cancellationToken);
        }
        else await ResolveIssue(attempt.Id, observation, cancellationToken);
        return await Commit(new(review ? ReconciliationCode.NeedsReview : ReconciliationCode.Applied, recorded?.Id), transaction, cancellationToken);
    }
    private async Task ObserveIssue(PaymentAttempt attempt, FinancialFailure failure, ProviderCollectionObservation observation, CancellationToken token)
    {
        var issue = await db.FinancialExceptions.SingleOrDefaultAsync(x => x.AttemptId == attempt.Id, token);
        var known = observation.State is ProviderCollectionState.Open or ProviderCollectionState.Processing or ProviderCollectionState.Paid or ProviderCollectionState.Uncollected
            ? observation.State.ToString() : null;
        string action;
        if (issue is null)
        {
            issue = FinancialException.Open(attempt, failure, known, time.GetUtcNow()); db.FinancialExceptions.Add(issue); action = "Opened";
        }
        else
        {
            await db.Entry(issue).ReloadAsync(token);
            action = issue.State == FinancialExceptionState.Resolved ? "Reopened" : "Observed";
            issue.Observe(failure, known, time.GetUtcNow());
        }
        db.FinancialExceptionHistory.Add(FinancialExceptionHistory.Record(issue.Id, action, null, failure.ToString(), time.GetUtcNow()));
    }
    private async Task ResolveIssue(Guid attemptId, ProviderCollectionObservation observation, CancellationToken token)
    {
        var issue = await db.FinancialExceptions.SingleOrDefaultAsync(x => x.AttemptId == attemptId, token);
        if (issue is null) return;
        await db.Entry(issue).ReloadAsync(token);
        if (issue.State == FinancialExceptionState.Resolved) return;
        if (observation.State is ProviderCollectionState.Open or ProviderCollectionState.Processing or ProviderCollectionState.Paid or ProviderCollectionState.Uncollected)
            issue.Observe(issue.Failure, observation.State.ToString(), time.GetUtcNow());
        issue.ResolveVerified(time.GetUtcNow());
        db.FinancialExceptionHistory.Add(FinancialExceptionHistory.Record(issue.Id, "Resolved", null, "Original canonical outcome verified", time.GetUtcNow()));
    }
    private async Task<ReconciliationResult> Commit(ReconciliationResult result, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (DbUpdateConcurrencyException) { return new(ReconciliationCode.Conflict); }
        catch (DbUpdateException e) when (e.InnerException is MySql.Data.MySqlClient.MySqlException { Number: 1062 or 1452 or 3819 } or Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 })
        { return new(ReconciliationCode.Conflict); }
    }
}
