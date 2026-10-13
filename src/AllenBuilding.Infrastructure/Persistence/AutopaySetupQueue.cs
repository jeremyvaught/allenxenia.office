using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Infrastructure.Persistence;

// Only setup is dispatched by this coordinator. Activation and stop work remain durable, separate stages.
public sealed class AutopaySetupQueue(OfficeDbContext db, TimeProvider time) : IAutopaySetupQueue
{
    public async Task<AutopaySetupLease?> ClaimAsync(CancellationToken token)
    {
        var now = time.GetUtcNow(); await using var tx = await AuthorityLocks.BeginAsync(db, token);
        var candidate = await db.AutopayWork.AsNoTracking().Where(x => x.Stage == AutopayWorkStage.Setup &&
            (x.State == CollectionWorkState.Queued && x.NextAttemptAt <= now || x.State == CollectionWorkState.Leased && x.LeaseUntil <= now))
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).FirstOrDefaultAsync(token);
        if (candidate is null) return null;
        var authSnapshot = await db.AutopayAuthorizations.AsNoTracking().SingleAsync(x => x.Id == candidate.AuthorizationId, token);
        await Lock(authSnapshot, candidate.Id, token);
        var original = await db.AutopayWork.SingleAsync(x => x.Id == candidate.Id, token); await db.Entry(original).ReloadAsync(token);
        var auth = await db.AutopayAuthorizations.SingleAsync(x => x.Id == original.AuthorizationId, token); await db.Entry(auth).ReloadAsync(token);
        now = time.GetUtcNow(); if (!original.Eligible(now)) return null;
        var current = await CanActivate(auth, now, token);
        if (original.DispatchedAt is null && !current)
        {
            original.Suppress(now); ReviewAuthorization(auth); await db.SaveChangesAsync(token); await tx.CommitAsync(token); return null;
        }
        if (original.Profile is null)
        {
            original.Review("OriginalProfileMissing"); ReviewAuthorization(auth); await db.SaveChangesAsync(token); await tx.CommitAsync(token); return null;
        }
        if (!original.Claim(now)) { ReviewAuthorization(auth); await db.SaveChangesAsync(token); await tx.CommitAsync(token); return null; }
        if (original.DispatchedAt is null) original.BeginDispatch(now);
        var readOnly = !current || auth.SetupSessionId is not null || !original.WithinOriginalWriteWindow(now);
        await db.SaveChangesAsync(token); await tx.CommitAsync(token);
        return new(original, auth, original.LeaseToken, readOnly);
    }
    public async Task<bool> CompleteAsync(AutopaySetupLease lease, AutopaySetupObservation observation, CancellationToken token)
    {
        await using var tx = await AuthorityLocks.BeginAsync(db, token); await Lock(lease.Authorization, lease.Original.Id, token);
        var original = await db.AutopayWork.SingleOrDefaultAsync(x => x.Id == lease.Original.Id, token); if (original is null) return false;
        await db.Entry(original).ReloadAsync(token); var now = time.GetUtcNow(); if (!original.Owns(lease.Token, now) || original.Stage != AutopayWorkStage.Setup || original.AuthorizationId != lease.Authorization.Id) return false;
        var until = original.LeaseUntil!.Value;
        var auth = await db.AutopayAuthorizations.SingleAsync(x => x.Id == original.AuthorizationId, token); await db.Entry(auth).ReloadAsync(token);
        var outcome = CollectionWorkOutcome.Retry;
        if (!Enum.IsDefined(observation.Outcome) || observation.Outcome == AutopaySetupOutcome.Invalid || observation.Outcome == AutopaySetupOutcome.Verified && observation.Setup is null) outcome = CollectionWorkOutcome.Review;
        else if (observation is { Outcome: AutopaySetupOutcome.Verified, Setup: { } fact })
        {
            var canActivate = await CanActivate(auth, now, token);
            try
            {
                if (fact.CheckedAt > now) throw new InvalidOperationException("A future observation cannot establish current setup.");
                auth.ObserveVerifiedSetup(fact, canActivate);
                outcome = fact.State == AutopaySetupState.Waiting ? CollectionWorkOutcome.Waiting : CollectionWorkOutcome.Finished;
                if (!canActivate || fact.State == AutopaySetupState.Expired) ReviewAuthorization(auth);
                if (canActivate && fact.State == AutopaySetupState.Ready && auth.State == AutopayState.AwaitingProvider &&
                    !await db.AutopayWork.AnyAsync(x => x.AuthorizationId == auth.Id && x.Stage == AutopayWorkStage.Activate, token))
                    db.AutopayWork.Add(AutopayWork.Enqueue(auth, AutopayWorkStage.Activate, now, original.Profile));
            }
            catch (InvalidOperationException) { outcome = CollectionWorkOutcome.Review; }
        }
        if (!original.Complete(lease.Token, outcome, time.GetUtcNow())) { await tx.RollbackAsync(token); db.ChangeTracker.Clear(); return false; }
        if (original.State == CollectionWorkState.NeedsReview) ReviewAuthorization(auth);
        if (time.GetUtcNow() >= until) { await tx.RollbackAsync(token); db.ChangeTracker.Clear(); return false; }
        try
        {
            await db.SaveChangesAsync(token);
            if (time.GetUtcNow() >= until) { await tx.RollbackAsync(token); db.ChangeTracker.Clear(); return false; }
            await tx.CommitAsync(token); return true;
        }
        catch (DbUpdateException) { await tx.RollbackAsync(token); db.ChangeTracker.Clear(); return false; }
    }
    private static void ReviewAuthorization(AutopayAuthorization auth)
    { if (auth.State is not (AutopayState.StopPending or AutopayState.Stopped or AutopayState.NeedsReview)) auth.RequireReview(); }
    private async Task Lock(AutopayAuthorization auth, Guid workId, CancellationToken token)
    {
        await AuthorityLocks.UserAsync(db, auth.ActorId, token);
        var membership = await db.RentAccounts.AsNoTracking().Where(x => x.Id == auth.AccountId).Select(x => x.MembershipId).SingleAsync(token);
        await AuthorityLocks.MembershipAsync(db, membership, token); await CustomerProvisionQueue.LockAccount(db, auth.AccountId, token);
        if (db is MySqlOfficeDbContext)
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM autopay_work WHERE id = {workId.ToString("D")} FOR UPDATE", token);
    }
    private async Task<bool> CanActivate(AutopayAuthorization auth, DateTimeOffset now, CancellationToken token)
    {
        if (auth.State != AutopayState.AwaitingMethod || auth.StopOperationId is not null || now >= auth.FirstAnchorUtc) return false;
        var current = await (from account in db.RentAccounts.AsNoTracking()
                             join member in db.Memberships on account.MembershipId equals member.Id
                             join user in db.Users on member.UserId equals user.Id
                             where account.Id == auth.AccountId && account.EndedAt == null && member.UserId == auth.ActorId && member.RevokedAt == null &&
                               member.StartsAt <= now && (member.EndsAt == null || member.EndsAt > now) && user.EmailConfirmed && (user.LockoutEnd == null || user.LockoutEnd <= now) &&
                               db.ActiveGrants.Any(x => x.MembershipId == member.Id) && db.AutopaySlots.Any(x => x.AccountId == account.Id && x.AuthorizationId == auth.Id) &&
                               db.RentAgreements.Any(x => x.Id == auth.AgreementId && x.AccountId == account.Id && x.Version == auth.AgreementVersion && x.SupersededBy == null)
                             select account.Id).AnyAsync(token);
        if (!current) return false;
        if (await db.FinancialExceptions.AnyAsync(x => x.AccountId == auth.AccountId && x.State != FinancialExceptionState.Resolved, token) ||
            await db.TenancyClosures.AnyAsync(x => x.AccountId == auth.AccountId && x.State != BillingClosureState.Verified, token) ||
            await db.CashSourceWork.AnyAsync(x => x.AccountId == auth.AccountId && x.State != CollectionWorkState.Completed, token) ||
            await db.CollectionWork.AnyAsync(x => x.ReadOnlyReconciliation && x.State != CollectionWorkState.Completed && db.PaymentAttempts.Any(a => a.Id == x.AttemptId && a.AccountId == auth.AccountId), token) ||
            await db.PaymentAttempts.AnyAsync(x => x.AccountId == auth.AccountId && x.State == PaymentAttemptState.NeedsReview, token) ||
            await db.CashMovements.AnyAsync(x => x.AccountId == auth.AccountId && !db.CashMovementReviews.Any(r => r.MovementId == x.Id && r.State == CashMovementReviewState.Classified && r.VerifiedAt != null && r.AgreementHash != null && r.AgreementHash.Length == 64), token)) return false;
        return !await (from period in db.BillingPeriods.AsNoTracking()
                       join debt in db.Obligations on period.Id equals debt.PeriodId
                       where period.AccountId == auth.AccountId && period.StartsOn >= auth.FirstPeriodStartsOn && (auth.EndsBefore == null || period.StartsOn < auth.EndsBefore) &&
                         (debt.AmountMinorUnits != auth.MonthlyMinorUnits || debt.Currency != auth.Currency || debt.AgreementId != auth.AgreementId ||
                          db.ObligationCredits.Any(x => x.ObligationId == debt.Id) || db.PaymentAllocations.Any(x => x.ObligationId == debt.Id) || db.RefundAllocations.Any(x => x.ObligationId == debt.Id) ||
                          db.PaymentAttemptItems.Any(x => x.ObligationId == debt.Id) || db.CollectionGuards.Any(x => x.ObligationId == debt.Id))
                       select debt.Id).AnyAsync(token);
    }
}
