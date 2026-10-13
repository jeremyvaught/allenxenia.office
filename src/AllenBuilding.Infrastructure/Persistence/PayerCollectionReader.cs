using AllenBuilding.Application.Access;
using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Infrastructure.Persistence;

public sealed class PayerCollectionReader(OfficeDbContext db, ICurrentActor current, CollectionPolicy policy, TimeProvider time) : IPayerCollectionReader
{
    public async Task<PayerCollectionSnapshot?> ReadAsync(Actor actor, Guid attemptId, CancellationToken token)
    {
        if (!policy.Enabled) return null;
        await using var tx = await AuthorityLocks.BeginAsync(db, token); await AuthorityLocks.UserAsync(db, actor.UserId, token);
        var owner = await (from original in db.PaymentAttempts.AsNoTracking()
                           join account in db.RentAccounts on original.AccountId equals account.Id
                           join member in db.Memberships on account.MembershipId equals member.Id
                           join suite in db.Suites on member.SuiteId equals suite.Id
                           where original.Id == attemptId && original.ActorId == actor.UserId && member.UserId == actor.UserId
                           select new { account.Id, memberId = member.Id, suite.Name }).SingleOrDefaultAsync(token);
        if (owner is null) return null;
        await AuthorityLocks.MembershipAsync(db, owner.memberId, token);
        await CustomerProvisionQueue.LockAccount(db, owner.Id, token);
        if (!await Authorized(actor, owner.memberId, token)) return null;
        var attempt = await db.PaymentAttempts.AsNoTracking().Include(x => x.Items).SingleAsync(x => x.Id == attemptId, token);
        if (attempt.ProviderAccount != policy.ProviderAccount || attempt.LiveMode != policy.LiveMode || attempt.Items.Count is < 1 or > 25) return null;
        var work = await db.CollectionWork.AsNoTracking().SingleOrDefaultAsync(x => x.AttemptId == attemptId, token);
        if (work is null) return null;
        var receipt = await db.SettlementPayments.AsNoTracking().Include(x => x.Allocations).SingleOrDefaultAsync(x => x.AttemptId == attemptId, token);
        if (attempt.State == PaymentAttemptState.Settled && receipt is null || receipt is not null &&
            (attempt.State != PaymentAttemptState.Settled || receipt.AccountId != attempt.AccountId || receipt.ProviderAccount != attempt.ProviderAccount || receipt.LiveMode != attempt.LiveMode ||
             receipt.CollectionReference != attempt.PaymentId || receipt.AmountMinorUnits != attempt.TotalMinorUnits || receipt.Currency != attempt.Currency ||
             receipt.Allocations.Count != attempt.Items.Count || receipt.Allocations.Any(a => !attempt.Items.Any(i => i.ObligationId == a.ObligationId && i.AccountId == a.AccountId && i.AmountMinorUnits == a.AmountMinorUnits)))) return null;
        var refunded = -(await db.CashMovements.Where(x => x.AttemptId == attemptId && x.Kind == CashMovementKind.Refund).SumAsync(x => (long?)x.SignedMinorUnits, token) ?? 0);
        var disputed = await db.CashMovements.Where(x => x.AttemptId == attemptId && x.Kind != CashMovementKind.Refund).SumAsync(x => (long?)x.SignedMinorUnits, token) ?? 0;
        var pending = await db.FinancialExceptions.AnyAsync(x => x.AccountId == attempt.AccountId && x.State != FinancialExceptionState.Resolved, token) ||
            await db.CashSourceWork.AnyAsync(x => x.AccountId == attempt.AccountId && x.State != CollectionWorkState.Completed, token) ||
            await db.CollectionWork.AnyAsync(x => x.ReadOnlyReconciliation && x.State != CollectionWorkState.Completed && db.PaymentAttempts.Any(a => a.Id == x.AttemptId && a.AccountId == attempt.AccountId), token) ||
            await db.CashMovements.AnyAsync(x => x.AccountId == attempt.AccountId && !db.CashMovementReviews.Any(r => r.MovementId == x.Id && r.State == CashMovementReviewState.Classified && r.VerifiedAt != null && r.AgreementHash != null && r.AgreementHash.Length == 64), token);
        var items = new List<PayerPaymentItem>();
        foreach (var item in attempt.Items.OrderBy(x => x.ObligationId))
        {
            var description = await db.Obligations.Where(x => x.Id == item.ObligationId && x.AccountId == attempt.AccountId).Select(x => x.Description).SingleAsync(token);
            var captured = receipt?.Allocations.Single(x => x.ObligationId == item.ObligationId).AmountMinorUnits ?? 0;
            var returned = receipt is null ? 0 : -(await db.RefundAllocations.Where(x => x.CaptureId == receipt.Id && x.ObligationId == item.ObligationId).SumAsync(x => (long?)x.SignedMinorUnits, token) ?? 0);
            items.Add(new(item.ObligationId, description, item.AmountMinorUnits, captured, returned));
        }
        if (!await Authorized(actor, owner.memberId, token)) return null;
        var canContinue = !pending && attempt.State == PaymentAttemptState.HandoffReady && attempt.ExpiresAt > time.GetUtcNow() && attempt.SessionId is not null;
        return new(new(attempt.Id, attempt.AccountId, owner.Name, attempt.State, attempt.TotalMinorUnits, attempt.CreatedAt, receipt?.AmountMinorUnits ?? 0, refunded, disputed, pending, canContinue, items), attempt, work.Profile);
    }
    public async Task<IReadOnlyList<PayerPaymentSummary>> RecentAsync(Actor actor, Guid accountId, CancellationToken token)
    {
        if (!policy.Enabled) return [];
        await using var tx = await AuthorityLocks.BeginAsync(db, token); await AuthorityLocks.UserAsync(db, actor.UserId, token);
        var membership = await (from account in db.RentAccounts.AsNoTracking()
                                join member in db.Memberships on account.MembershipId equals member.Id
                                where account.Id == accountId && member.UserId == actor.UserId
                                select (Guid?)member.Id).SingleOrDefaultAsync(token);
        if (membership is null) return [];
        await AuthorityLocks.MembershipAsync(db, membership.Value, token); await CustomerProvisionQueue.LockAccount(db, accountId, token);
        if (!await Authorized(actor, membership.Value, token)) return [];
        var rows = await db.PaymentAttempts.AsNoTracking().Where(x => x.AccountId == accountId && x.ActorId == actor.UserId &&
            x.ProviderAccount == policy.ProviderAccount && x.LiveMode == policy.LiveMode).OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Take(50).Select(x => new PayerPaymentSummary(x.Id, x.State, x.TotalMinorUnits, x.CreatedAt)).ToListAsync(token);
        return await Authorized(actor, membership.Value, token) ? rows : [];
    }
    private async Task<bool> Authorized(Actor original, Guid membershipId, CancellationToken token)
    {
        var fresh = await current.GetAsync(token); var now = time.GetUtcNow();
        return fresh is not null && fresh.UserId == original.UserId && (fresh.ManagedBuildings.Count == 0 || fresh.ChallengedMfa) &&
            await db.Memberships.AsNoTracking().AnyAsync(x => x.Id == membershipId && x.UserId == fresh.UserId && x.RevokedAt == null && x.StartsAt <= now &&
                (x.EndsAt == null || x.EndsAt > now) && db.ActiveGrants.Any(g => g.MembershipId == x.Id), token);
    }
}
