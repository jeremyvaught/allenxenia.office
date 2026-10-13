using AllenBuilding.Application.Access;
using AllenBuilding.Application.Billing;
using AllenBuilding.Application.Diagnostics;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Infrastructure.Persistence;

public sealed class CollectionStore(OfficeDbContext db, ICurrentActor current, CollectionPolicy policy, AccountInvitations audit, TimeProvider time) : ICollectionStore
{
    public async Task<IReadOnlyList<ReviewedCollectionBalance>> ReviewAsync(Actor actor, Guid accountId, IReadOnlyList<Guid> obligations, CancellationToken cancellationToken)
    {
        await using var transaction = await AuthorityLocks.BeginAsync(db, cancellationToken);
        if (!await Authorize(actor, accountId, cancellationToken)) return [];
        var result = await Balances(accountId, obligations, cancellationToken);
        return await Authorize(actor, accountId, cancellationToken) ? result : [];
    }

    public async Task<CollectionResult> AdmitAsync(Actor actor, CollectReviewedBalances command, CancellationToken cancellationToken)
    {
        await using var transaction = await AuthorityLocks.BeginAsync(db, cancellationToken);
        if (!await Authorize(actor, command.AccountId, cancellationToken)) return new(CollectionCode.Unavailable);
        // Replays are checked only after current tenancy authorization, even for completed payments.
        var original = await db.PaymentAttempts.AsNoTracking().Include(x => x.Items)
            .SingleOrDefaultAsync(x => x.ActorId == actor.UserId && x.OperationId == command.OperationId, cancellationToken);
        if (original is not null)
        {
            var matches = original.AccountId == command.AccountId && original.ProviderAccount == policy.ProviderAccount && original.LiveMode == policy.LiveMode &&
                original.Items.Count == command.Items.Count && original.Items.All(x => command.Items.Any(y =>
                    y.ObligationId == x.ObligationId && y.Version == x.ReviewedVersion && y.RemainingMinorUnits == x.AmountMinorUnits && y.AccountId == x.AccountId && y.Currency == original.Currency));
            if (!await Authorize(actor, command.AccountId, cancellationToken)) return new(CollectionCode.Unavailable);
            return matches ? new(CollectionCode.Success, original.Id, original.State) : new(CollectionCode.Conflict);
        }
        var binding = await db.BillingCustomers.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == command.AccountId &&
            x.ProviderAccount == policy.ProviderAccount && x.LiveMode == policy.LiveMode, cancellationToken);
        if (binding is null) return new(CollectionCode.Unavailable);
        var balances = await Balances(command.AccountId, command.Items.Select(x => x.ObligationId).ToArray(), cancellationToken);
        if (balances.Count != command.Items.Count || balances.Any(x => x.HasProviderInstruction || x.Held || !command.Items.Any(y =>
            y.ObligationId == x.ObligationId && y.Version == x.Version && y.RemainingMinorUnits == x.RemainingMinorUnits))) return new(CollectionCode.Conflict);
        PaymentAttempt attempt;
        try { attempt = PaymentAttempt.Admit(command.OperationId, actor.UserId, binding, balances, time.GetUtcNow()); }
        catch (ArgumentException) { return new(CollectionCode.ValidationFailed); }
        catch (OverflowException) { return new(CollectionCode.ValidationFailed); }
        if (!await Authorize(actor, command.AccountId, cancellationToken)) return new(CollectionCode.Unavailable);
        db.PaymentAttempts.Add(attempt);
        var origin = OfficeTelemetry.Capture();
        db.CollectionWork.Add(CollectionWork.Enqueue(attempt, policy.Profile!, origin?.TraceId, origin?.SpanId));
        foreach (var item in attempt.Items) db.CollectionGuards.Add(new CollectionGuard { AttemptId = attempt.Id, AccountId = attempt.AccountId, ObligationId = item.ObligationId });
        audit.Audit(actor.UserId, "CollectionAdmitted", command.AccountId.ToString("D"), "Attempt " + attempt.Id.ToString("D"));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(CollectionCode.Success, attempt.Id, attempt.State);
        }
        catch (DbUpdateConcurrencyException) { return new(CollectionCode.Conflict); }
        catch (DbUpdateException e) when (e.InnerException is MySql.Data.MySqlClient.MySqlException { Number: 1062 } or Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 })
        { return new(CollectionCode.Conflict); }
    }

    private async Task<bool> Authorize(Actor actor, Guid accountId, CancellationToken cancellationToken)
    {
        if (!policy.Enabled || policy.Profile is null || !PaymentReferences.Valid(policy.ProviderAccount, "acct_")) return false;
        await AuthorityLocks.UserAsync(db, actor.UserId, cancellationToken);
        var membershipId = await db.RentAccounts.AsNoTracking().Where(x => x.Id == accountId).Select(x => (Guid?)x.MembershipId).SingleOrDefaultAsync(cancellationToken);
        if (membershipId is null) return false;
        await AuthorityLocks.MembershipAsync(db, membershipId.Value, cancellationToken);
        if (db is MySqlOfficeDbContext)
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM rent_accounts WHERE id = {accountId.ToString("D")} FOR UPDATE", cancellationToken);
        var fresh = await current.GetAsync(cancellationToken);
        if (fresh is null || fresh.UserId != actor.UserId || (fresh.ManagedBuildings.Count > 0 && !fresh.ChallengedMfa)) return false;
        var now = time.GetUtcNow();
        return await db.Memberships.AsNoTracking().AnyAsync(x => x.Id == membershipId && x.UserId == actor.UserId && x.RevokedAt == null &&
            x.StartsAt <= now && (x.EndsAt == null || x.EndsAt > now) && db.ActiveGrants.Any(g => g.MembershipId == x.Id), cancellationToken);
    }

    private async Task<IReadOnlyList<ReviewedCollectionBalance>> Balances(Guid accountId, IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        foreach (var id in ids.OrderBy(x => x.ToString("D"), StringComparer.Ordinal))
            if (db is MySqlOfficeDbContext)
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM obligations WHERE id = {id.ToString("D")} AND account_id = {accountId.ToString("D")} FOR UPDATE", cancellationToken);
        // Separate aggregate per obligation: joins cannot multiply cash allocations.
        // Original captures and reviewed refund reversals are separate aggregates. Pending provider agreement still holds collection.
        return await db.Obligations.AsNoTracking().Where(x => x.AccountId == accountId && ids.Contains(x.Id))
            .OrderBy(x => x.Id).Select(x => new ReviewedCollectionBalance(x.Id, x.AccountId, x.Version,
                x.AmountMinorUnits - (db.ObligationCredits.Where(c => c.ObligationId == x.Id).Sum(c => (long?)c.AmountMinorUnits) ?? 0) - (db.PaymentAllocations.Where(a => a.ObligationId == x.Id).Sum(a => (long?)a.AmountMinorUnits) ?? 0) - (db.RefundAllocations.Where(a => a.ObligationId == x.Id).Sum(a => (long?)a.SignedMinorUnits) ?? 0), x.Currency,
                db.CollectionGuards.Any(g => g.ObligationId == x.Id),
                db.TenancyClosures.Any(c => c.AccountId == x.AccountId && c.State != BillingClosureState.Verified) ||
                db.CollectionWork.Any(w => w.ReadOnlyReconciliation && w.State != CollectionWorkState.Completed && db.PaymentAttempts.Any(a => a.Id == w.AttemptId && a.AccountId == x.AccountId)) ||
                db.CashSourceWork.Any(w => w.AccountId == x.AccountId && w.State != CollectionWorkState.Completed) ||
                db.FinancialExceptions.Any(e => e.AccountId == x.AccountId && e.State != FinancialExceptionState.Resolved) ||
                db.CashMovements.Any(m => m.AccountId == x.AccountId && !db.CashMovementReviews.Any(r => r.MovementId == m.Id && r.State == CashMovementReviewState.Classified && r.VerifiedAt != null && r.AgreementHash != null && r.AgreementHash.Length == 64)) ||
                db.PaymentAttemptItems.Any(i => i.ObligationId == x.Id && db.PaymentAttempts.Any(a => a.Id == i.AttemptId &&
                    a.State != PaymentAttemptState.Settled && a.State != PaymentAttemptState.Uncollected &&
                    (a.State == PaymentAttemptState.NeedsReview || !db.CollectionGuards.Any(g => g.ObligationId == x.Id)))))).ToListAsync(cancellationToken);
    }
}
