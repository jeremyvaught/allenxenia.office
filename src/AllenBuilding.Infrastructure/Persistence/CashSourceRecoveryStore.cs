using System.Diagnostics;
using AllenBuilding.Application.Access;
using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Infrastructure.Persistence;

// Admission only retrieves evidence from Stripe. It never issues a refund or accepts replacement original payment IDs.
public sealed class CashSourceRecoveryStore(OfficeDbContext db, ICurrentActor current, CashSourceRecoveryPolicy policy, AccountInvitations audit, TimeProvider time) : ICashSourceRecoveryStore
{
    public async Task<BillingResult> RequestAsync(Actor actor, InvestigateCashSource command, string hash, CancellationToken token)
    {
        if (!policy.Enabled) return new(BillingCode.Unavailable);
        await using var tx = await AuthorityLocks.BeginAsync(db, token); await AuthorityLocks.UserAsync(db, actor.UserId, token);
        var scope = await (from issue in db.FinancialExceptions.AsNoTracking()
                           join account in db.RentAccounts on issue.AccountId equals account.Id
                           join member in db.Memberships on account.MembershipId equals member.Id
                           join suite in db.Suites on member.SuiteId equals suite.Id
                           where issue.Id == command.CaseId
                           select new { issue.AccountId, issue.AttemptId, suite.BuildingId }).SingleOrDefaultAsync(token);
        if (scope is null) return new(BillingCode.Unavailable);
        await AuthorityLocks.ManagerAsync(db, actor.UserId, scope.BuildingId, token); await CustomerProvisionQueue.LockAccount(db, scope.AccountId, token);
        if (db is MySqlOfficeDbContext)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM payment_attempts WHERE id = {scope.AttemptId.ToString("D")} FOR UPDATE", token);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM financial_exceptions WHERE id = {command.CaseId.ToString("D")} FOR UPDATE", token);
        }
        if (!await Authorized(actor, scope.BuildingId, token)) return new(BillingCode.Unavailable);
        var receipt = await db.BillingReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.ActorId == actor.UserId && x.OperationId == command.OperationId, token);
        if (receipt is not null) return receipt.PayloadHash == hash && receipt.AccountId == scope.AccountId
            ? new(BillingCode.Success, receipt.AccountId, receipt.EntityId, receipt.AccountVersion) : new(BillingCode.Conflict);
        var issueToChange = await db.FinancialExceptions.SingleAsync(x => x.Id == command.CaseId, token); await db.Entry(issueToChange).ReloadAsync(token);
        if (issueToChange.Version != command.CaseVersion) return new(BillingCode.Conflict);
        var original = await db.PaymentAttempts.SingleAsync(x => x.Id == scope.AttemptId, token); await db.Entry(original).ReloadAsync(token);
        if (original.State != PaymentAttemptState.Settled || !await db.SettlementPayments.AnyAsync(x => x.AttemptId == original.Id, token)) return new(BillingCode.Unavailable);
        var work = await db.CashSourceWork.SingleOrDefaultAsync(x => x.AttemptId == original.Id && x.SourceId == command.SourceId, token);
        var now = time.GetUtcNow();
        if (work is null)
        {
            if (await db.CashSourceWork.CountAsync(x => x.AccountId == scope.AccountId && x.State != CollectionWorkState.Completed, token) >= 25) return new(BillingCode.Conflict);
            var activity = Activity.Current;
            work = CashSourceWork.Enqueue(original, command.SourceId, command.OperationId, actor.UserId, now, activity?.TraceId.ToHexString(), activity?.SpanId.ToHexString());
            db.CashSourceWork.Add(work);
        }
        else
        {
            await db.Entry(work).ReloadAsync(token);
            try { work.RequestAgain(now); } catch (InvalidOperationException) { return new(BillingCode.Conflict); }
        }
        if (!await Authorized(actor, scope.BuildingId, token)) { await tx.RollbackAsync(token); db.ChangeTracker.Clear(); return new(BillingCode.Unavailable); }
        // A resolved case is reopened without altering its original capture; work also holds collection independently of case state.
        issueToChange.Observe(FinancialFailure.ExternalCashMovement, null, now);
        db.FinancialExceptionHistory.Add(FinancialExceptionHistory.Record(issueToChange.Id, "CashReconcileRequested", actor.UserId, command.Reason, now));
        db.BillingReceipts.Add(new() { ActorId = actor.UserId, OperationId = command.OperationId, PayloadHash = hash, AccountId = scope.AccountId, EntityId = work.Id, AccountVersion = issueToChange.Version, OccurredAt = now });
        audit.Audit(actor.UserId, "CashSourceReconciliationRequested", issueToChange.Id.ToString("D"), command.Reason);
        try { await db.SaveChangesAsync(token); await tx.CommitAsync(token); return new(BillingCode.Success, scope.AccountId, work.Id, issueToChange.Version); }
        catch (DbUpdateException) { await tx.RollbackAsync(token); db.ChangeTracker.Clear(); return new(BillingCode.Conflict); }
    }
    private async Task<bool> Authorized(Actor actor, Guid building, CancellationToken token)
    {
        var fresh = await current.GetAsync(token);
        return fresh is { ChallengedMfa: true, RecentMfa: true } && fresh.UserId == actor.UserId && fresh.ManagedBuildings.Contains(building) &&
            await db.BuildingManagers.AnyAsync(x => x.UserId == actor.UserId && x.BuildingId == building, token);
    }
}
