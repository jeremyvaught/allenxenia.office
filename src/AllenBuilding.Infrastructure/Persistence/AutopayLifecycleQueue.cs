using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Infrastructure.Persistence;

// Prepared lifecycle coordinator, not a registered dispatcher. Known-subscription stops require
// the upcoming canonical invoice mapping before their reservation can be released.
public sealed class AutopayLifecycleQueue(OfficeDbContext db, TimeProvider time) : IAutopayLifecycleQueue
{
    public async Task<AutopayLifecycleLease?> ClaimAsync(CancellationToken token)
    {
        var now = time.GetUtcNow(); await using var tx = await AuthorityLocks.BeginAsync(db, token);
        var candidate = await db.AutopayWork.AsNoTracking().Where(x => x.Stage != AutopayWorkStage.Setup &&
            (x.State == CollectionWorkState.Queued && x.NextAttemptAt <= now || x.State == CollectionWorkState.Leased && x.LeaseUntil <= now))
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).FirstOrDefaultAsync(token);
        if (candidate is null) return null;
        var snapshot = await db.AutopayAuthorizations.AsNoTracking().SingleAsync(x => x.Id == candidate.AuthorizationId, token); await Lock(snapshot, candidate.Id, token);
        var work = await db.AutopayWork.SingleAsync(x => x.Id == candidate.Id, token); await db.Entry(work).ReloadAsync(token);
        var auth = await db.AutopayAuthorizations.SingleAsync(x => x.Id == work.AuthorizationId, token); await db.Entry(auth).ReloadAsync(token);
        now = time.GetUtcNow(); if (!work.Eligible(now)) return null;
        var current = work.Stage == AutopayWorkStage.Activate && await AutopaySetupAdmission.AllowsAsync(db, auth, now, token, AutopayState.AwaitingProvider);
        if (work.Stage == AutopayWorkStage.Activate && !current && work.DispatchedAt is null)
        {
            work.Suppress(now); Review(auth); await db.SaveChangesAsync(token); await tx.CommitAsync(token); return null;
        }
        if (work.Stage == AutopayWorkStage.Activate && !current && work.DispatchedAt is not null) QueueCleanup(auth, work, now);
        if (work.Stage == AutopayWorkStage.Stop && auth.State != AutopayState.StopPending || work.Profile is null)
        {
            if (work.Stage == AutopayWorkStage.Activate && work.DispatchedAt is not null) QueueCleanup(auth, work, now);
            work.Review(work.Profile is null ? "OriginalProfileMissing" : "AdmissionChanged"); Review(auth); await db.SaveChangesAsync(token); await tx.CommitAsync(token); return null;
        }
        if (!work.Claim(now))
        {
            if (work.Stage == AutopayWorkStage.Activate && work.DispatchedAt is not null) QueueCleanup(auth, work, now);
            Review(auth); await db.SaveChangesAsync(token); await tx.CommitAsync(token); return null;
        }
        if (work.DispatchedAt is null) work.BeginDispatch(now);
        var readOnly = work.Stage == AutopayWorkStage.Activate && (!current || auth.SubscriptionId is not null) || !work.WithinOriginalWriteWindow(now);
        await db.SaveChangesAsync(token); await tx.CommitAsync(token); return new(work, auth, work.LeaseToken, readOnly);
    }

    public async Task<bool> CompleteAsync(AutopayLifecycleLease lease, AutopayLifecycleObservation observation, CancellationToken token)
    {
        await using var tx = await AuthorityLocks.BeginAsync(db, token); await Lock(lease.Authorization, lease.Original.Id, token);
        var work = await db.AutopayWork.SingleOrDefaultAsync(x => x.Id == lease.Original.Id, token); if (work is null) return false;
        await db.Entry(work).ReloadAsync(token); var now = time.GetUtcNow();
        if (!work.Owns(lease.Token, now) || work.Stage == AutopayWorkStage.Setup || work.Stage != lease.Original.Stage || work.AuthorizationId != lease.Authorization.Id) return false;
        var until = work.LeaseUntil!.Value; var auth = await db.AutopayAuthorizations.SingleAsync(x => x.Id == work.AuthorizationId, token); await db.Entry(auth).ReloadAsync(token);
        var canActivate = work.Stage == AutopayWorkStage.Activate && await AutopaySetupAdmission.AllowsAsync(db, auth, now, token, AutopayState.AwaitingProvider);
        if (work.Stage == AutopayWorkStage.Activate && !canActivate) QueueCleanup(auth, work, now);
        var result = CollectionWorkOutcome.Retry;
        if (!Enum.IsDefined(observation.Outcome) || observation.Outcome == AutopayLifecycleOutcome.Invalid) result = CollectionWorkOutcome.Review;
        else if (observation.Outcome is AutopayLifecycleOutcome.Verified or AutopayLifecycleOutcome.Pending)
        {
            try
            {
                if (work.Stage == AutopayWorkStage.Activate)
                {
                    if (observation is not { Outcome: AutopayLifecycleOutcome.Verified, Subscription: { } subscription, Stop: null } ||
                        subscription.CheckedAt > now || subscription.CheckedAt < work.DispatchedAt) throw new InvalidOperationException();
                    if (canActivate) auth.ApplyVerifiedSubscription(subscription);
                    else auth.ObserveDispatchedSubscriptionForCleanup(subscription, work.DispatchedAt!.Value);
                    result = CollectionWorkOutcome.Finished;
                    Audit(auth, canActivate ? "AutopaySubscriptionVerified" : "AutopayOriginalSubscriptionFoundForStop", now);
                }
                else
                {
                    if (observation.Subscription is { } subscription)
                    {
                        if (subscription.CheckedAt > now || subscription.CheckedAt < work.DispatchedAt) throw new InvalidOperationException();
                        auth.ObserveOriginalSubscriptionForStop(subscription);
                    }
                    if (observation.Outcome == AutopayLifecycleOutcome.Pending && observation.Stop is null) result = CollectionWorkOutcome.Waiting;
                    else
                    {
                        // A no-subscription proof is unsafe after an uncertain original creation. A known
                        // subscription cannot release the local slot before all its invoice instructions map.
                        if (observation.Stop is not { } stopped || stopped.CheckedAt > now || stopped.CheckedAt < work.DispatchedAt || auth.SubscriptionId is not null || stopped.SubscriptionId is not null ||
                            await db.AutopayWork.AnyAsync(x => x.AuthorizationId == auth.Id && x.Stage == AutopayWorkStage.Activate && x.DispatchedAt != null, token)) throw new InvalidOperationException();
                        var originalCreate = await db.AutopayWork.SingleOrDefaultAsync(x => x.AuthorizationId == auth.Id && x.Stage == AutopayWorkStage.Activate, token);
                        if (originalCreate is not null && originalCreate.State != CollectionWorkState.Suppressed)
                        {
                            if (!originalCreate.Eligible(now)) throw new InvalidOperationException();
                            originalCreate.Suppress(now);
                        }
                        auth.VerifyStop(stopped); result = CollectionWorkOutcome.Finished; Audit(auth, "AutopayNoSubscriptionStopVerified", now);
                    }
                }
            }
            catch (InvalidOperationException) { result = CollectionWorkOutcome.Review; }
        }
        if (!work.Complete(lease.Token, result, time.GetUtcNow())) { await tx.RollbackAsync(token); db.ChangeTracker.Clear(); return false; }
        if (work.State == CollectionWorkState.NeedsReview)
        {
            if (work.Stage == AutopayWorkStage.Activate && work.DispatchedAt is not null) QueueCleanup(auth, work, time.GetUtcNow());
            Review(auth);
        }
        if (time.GetUtcNow() >= until) { await tx.RollbackAsync(token); db.ChangeTracker.Clear(); return false; }
        try
        {
            await db.SaveChangesAsync(token);
            if (time.GetUtcNow() >= until) { await tx.RollbackAsync(token); db.ChangeTracker.Clear(); return false; }
            await tx.CommitAsync(token); return true;
        }
        catch (DbUpdateException) { await tx.RollbackAsync(token); db.ChangeTracker.Clear(); return false; }
    }
    private void QueueCleanup(AutopayAuthorization auth, AutopayWork original, DateTimeOffset now)
    {
        if (auth.State == AutopayState.Stopped) throw new InvalidOperationException("An uncertain creation cannot follow a verified stop.");
        if (auth.StopOperationId is null)
        {
            auth.RequestCleanupStop(original.Id, now);
            db.AutopayWork.Add(AutopayWork.Enqueue(auth, AutopayWorkStage.Stop, now, original.Profile));
            Audit(auth, "AutopayCleanupRequired", now);
        }
    }
    private void Audit(AutopayAuthorization auth, string action, DateTimeOffset now) => db.AccountAudits.Add(new AccountAudit
    { ActorId = "system", Action = action, SubjectId = auth.Id.ToString("D"), Reason = "Original rent account " + auth.AccountId.ToString("D"), OccurredAt = now });
    private static void Review(AutopayAuthorization auth) { if (auth.State is not (AutopayState.StopPending or AutopayState.Stopped or AutopayState.NeedsReview)) auth.RequireReview(); }
    private async Task Lock(AutopayAuthorization auth, Guid workId, CancellationToken token)
    {
        await AuthorityLocks.UserAsync(db, auth.ActorId, token);
        var membership = await db.RentAccounts.AsNoTracking().Where(x => x.Id == auth.AccountId).Select(x => x.MembershipId).SingleAsync(token);
        await AuthorityLocks.MembershipAsync(db, membership, token); await CustomerProvisionQueue.LockAccount(db, auth.AccountId, token);
        if (db is MySqlOfficeDbContext) await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM autopay_work WHERE id = {workId.ToString("D")} FOR UPDATE", token);
    }
}
