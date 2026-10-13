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
        var current = await AutopaySetupAdmission.AllowsAsync(db, auth, now, token);
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
            var canActivate = await AutopaySetupAdmission.AllowsAsync(db, auth, now, token);
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
}
