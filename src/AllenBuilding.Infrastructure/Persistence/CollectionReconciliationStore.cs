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
        var payment = await db.SettlementPayments.AsNoTracking().SingleOrDefaultAsync(x => x.AttemptId == attempt.Id, cancellationToken);
        if (attempt.State == PaymentAttemptState.Settled)
        {
            var fact = observation.Settlement;
            return observation.State == ProviderCollectionState.Paid && payment is not null && fact is not null && observation.SessionId == fact.SessionId &&
                fact.ProviderAccount == attempt.ProviderAccount && fact.LiveMode == attempt.LiveMode && fact.CustomerId == attempt.CustomerId &&
                fact.SessionId == attempt.SessionId && fact.PaymentId == attempt.PaymentId && payment.CollectionReference == fact.PaymentId &&
                fact.AmountMinorUnits == payment.AmountMinorUnits && fact.Currency == payment.Currency
                ? new(ReconciliationCode.AlreadyApplied, payment.Id) : new(ReconciliationCode.NeedsReview);
        }
        if (attempt.State is PaymentAttemptState.Admitted or PaymentAttemptState.Uncollected || payment is not null) return new(ReconciliationCode.NeedsReview);
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
        if (review) attempt.RequireReview();
        try
        {
            await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
            return new(review ? ReconciliationCode.NeedsReview : ReconciliationCode.Applied, recorded?.Id);
        }
        catch (DbUpdateConcurrencyException) { return new(ReconciliationCode.Conflict); }
        catch (DbUpdateException e) when (e.InnerException is MySql.Data.MySqlClient.MySqlException { Number: 1062 or 1452 or 3819 } or Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 })
        { return new(ReconciliationCode.Conflict); }
    }
}
