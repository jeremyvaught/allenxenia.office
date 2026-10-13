using AllenBuilding.Application.Access;
using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Infrastructure.Persistence;

public sealed class FinancialExceptionStore(OfficeDbContext db, ICurrentActor current, TimeProvider time, FinancialRecoveryPolicy recovery) : IFinancialExceptionStore
{
    public async Task<IReadOnlyList<FinancialCaseView>> ReadAsync(Actor actor, bool includeResolved, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var timeout = db.Database.GetCommandTimeout();
        try { db.Database.SetCommandTimeout(5); return await ReadCore(actor, includeResolved, deadline.Token); }
        finally { db.Database.SetCommandTimeout(timeout); }
    }
    private async Task<IReadOnlyList<FinancialCaseView>> ReadCore(Actor actor, bool includeResolved, CancellationToken cancellationToken)
    {
        var rows = await (from issue in db.FinancialExceptions.AsNoTracking()
                          join attempt in db.PaymentAttempts on issue.AttemptId equals attempt.Id
                          join account in db.RentAccounts on issue.AccountId equals account.Id
                          join membership in db.Memberships on account.MembershipId equals membership.Id
                          join suite in db.Suites on membership.SuiteId equals suite.Id
                          join payment in db.SettlementPayments on attempt.Id equals payment.AttemptId into payments
                          from payment in payments.DefaultIfEmpty()
                          join work in db.CollectionWork on attempt.Id equals work.AttemptId into jobs
                          from work in jobs.DefaultIfEmpty()
                          where actor.ManagedBuildings.Contains(suite.BuildingId) && db.BuildingManagers.Any(m => m.UserId == actor.UserId && m.BuildingId == suite.BuildingId) &&
                              (includeResolved || issue.State != FinancialExceptionState.Resolved)
                          orderby issue.FirstSeenAt, issue.Id
                          select new { Issue = issue, Attempt = attempt, Work = work, suite.BuildingId, Suite = suite.Name, LastCashAt = payment == null ? null : (DateTimeOffset?)payment.RecordedAt })
            .Take(50).ToListAsync(cancellationToken);
        var result = new List<FinancialCaseView>();
        var ownersByBuilding = new Dictionary<Guid, IReadOnlyList<FinancialCaseOwner>>();
        foreach (var row in rows)
        {
            if (!ownersByBuilding.TryGetValue(row.BuildingId, out var owners))
            {
                owners = await (from manager in db.BuildingManagers
                                join user in db.Users on manager.UserId equals user.Id
                                where manager.BuildingId == row.BuildingId && user.TwoFactorEnabled
                                orderby user.Email, user.Id
                                select new FinancialCaseOwner(user.Id, user.Email ?? user.UserName ?? "Manager")).Take(100).ToListAsync(cancellationToken);
                ownersByBuilding.Add(row.BuildingId, owners);
            }
            var items = await db.PaymentAttemptItems.AsNoTracking().Where(x => x.AttemptId == row.Attempt.Id).OrderBy(x => x.ObligationId)
                .Take(25).Select(x => new FinancialCaseItem(x.ObligationId, x.ReviewedVersion, x.AmountMinorUnits, row.Attempt.Currency)).ToListAsync(cancellationToken);
            var history = await db.FinancialExceptionHistory.AsNoTracking().Where(x => x.ExceptionId == row.Issue.Id).OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id)
                .Take(20).Select(x => new FinancialCaseHistory(x.Action, x.ActorUserId, x.Reason, x.OccurredAt)).ToListAsync(cancellationToken);
            var cash = await (from movement in db.CashMovements.AsNoTracking()
                              join review in db.CashMovementReviews on movement.Id equals review.MovementId into reviews
                              from review in reviews.DefaultIfEmpty()
                              where movement.AccountId == row.Issue.AccountId && movement.AttemptId == row.Attempt.Id
                              orderby movement.RecordedAt descending, movement.Id descending
                              select new FinancialCashFact(movement.Id, movement.Kind, movement.SignedMinorUnits, movement.Currency, movement.SourceId,
                                  movement.TransactionId, movement.ProviderAt, review == null ? null : (CashMovementReviewState?)review.State, review == null ? null : (Guid?)review.Version))
                .Take(50).ToListAsync(cancellationToken);
            var sources = await db.CashSourceWork.AsNoTracking().Where(x => x.AttemptId == row.Attempt.Id && x.AccountId == row.Issue.AccountId)
                .OrderBy(x => x.State == CollectionWorkState.Completed).ThenByDescending(x => x.CreatedAt).ThenBy(x => x.Id)
                .Take(25).Select(x => new FinancialSourceCheck(x.Id, x.SourceId, x.State, x.Attempts, x.Failures, x.NextAttemptAt, x.LastError)).ToListAsync(cancellationToken);
            result.Add(new(row.Issue.Id, row.Issue.Version, row.BuildingId, row.Issue.AccountId, row.Attempt.Id, row.Suite, row.Issue.State, row.Issue.Failure,
                row.Issue.OwnerUserId, row.Attempt.ProviderAccount, row.Attempt.LiveMode, row.Attempt.CustomerId, row.Attempt.SessionId, row.Attempt.PaymentId,
                row.Attempt.OperationId, row.Attempt.State, row.Issue.LastVerifiedState ?? (row.LastCashAt is null ? null : "Paid"), row.Issue.LastVerifiedAt ?? row.LastCashAt,
                row.Issue.FirstSeenAt, items, owners, history, row.Work?.Attempts, row.Work?.Failures, row.Work?.State, row.Work?.NextAttemptAt, CashFacts: cash, SourceChecks: sources));
        }
        // Discard protected rows when either the ticket or the current management grant ended during the read.
        var fresh = await current.GetAsync(cancellationToken);
        if (fresh is not { ChallengedMfa: true } || fresh.UserId != actor.UserId) return [];
        var permitted = await db.BuildingManagers.AsNoTracking().Where(x => x.UserId == actor.UserId && fresh.ManagedBuildings.Contains(x.BuildingId))
            .Select(x => x.BuildingId).ToListAsync(cancellationToken);
        return result.Where(x => permitted.Contains(x.BuildingId)).ToArray();
    }

    public async Task<BillingResult> ChangeAsync(Actor actor, FinancialCaseCommand command, string hash, CancellationToken cancellationToken)
    {
        if (command.Action == FinancialCaseAction.Reconcile && !recovery.Enabled) return new(BillingCode.Unavailable);
        await using var transaction = await AuthorityLocks.BeginAsync(db, cancellationToken);
        await AuthorityLocks.UserAsync(db, actor.UserId, cancellationToken);
        var scope = await (from issue in db.FinancialExceptions.AsNoTracking()
                           join account in db.RentAccounts on issue.AccountId equals account.Id
                           join membership in db.Memberships on account.MembershipId equals membership.Id
                           join suite in db.Suites on membership.SuiteId equals suite.Id
                           where issue.Id == command.Id
                           select new { issue.AccountId, issue.AttemptId, suite.BuildingId }).SingleOrDefaultAsync(cancellationToken);
        if (scope is null) return new(BillingCode.Unavailable);
        await AuthorityLocks.AssignmentManagersAsync(db, actor.UserId, command.OwnerUserId, scope.BuildingId, cancellationToken);
        if (db is MySqlOfficeDbContext)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM rent_accounts WHERE id = {scope.AccountId.ToString("D")} FOR UPDATE", cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM payment_attempts WHERE id = {scope.AttemptId.ToString("D")} FOR UPDATE", cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM financial_exceptions WHERE id = {command.Id.ToString("D")} FOR UPDATE", cancellationToken);
        }
        var fresh = await current.GetAsync(cancellationToken);
        if (fresh is not { ChallengedMfa: true, RecentMfa: true } || fresh.UserId != actor.UserId || !fresh.ManagedBuildings.Contains(scope.BuildingId) ||
            !await db.BuildingManagers.AnyAsync(x => x.UserId == actor.UserId && x.BuildingId == scope.BuildingId, cancellationToken)) return new(BillingCode.Unavailable);
        var replay = await db.BillingReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.ActorId == actor.UserId && x.OperationId == command.OperationId, cancellationToken);
        if (replay is not null) return replay.PayloadHash == hash ? new(BillingCode.Success, replay.AccountId, replay.EntityId, replay.AccountVersion) : new(BillingCode.Conflict);
        var issueToChange = await db.FinancialExceptions.SingleAsync(x => x.Id == command.Id, cancellationToken); await db.Entry(issueToChange).ReloadAsync(cancellationToken);
        if (issueToChange.Version != command.Version || issueToChange.State == FinancialExceptionState.Resolved) return new(BillingCode.Conflict);
        if (command.Action == FinancialCaseAction.Assign)
        {
            if (!await (from manager in db.BuildingManagers
                        join user in db.Users on manager.UserId equals user.Id
                        where manager.UserId == command.OwnerUserId && manager.BuildingId == scope.BuildingId && user.TwoFactorEnabled
                        select manager).AnyAsync(cancellationToken)) return new(BillingCode.Unavailable);
            issueToChange.Assign(command.OwnerUserId!);
        }
        else if (command.Action == FinancialCaseAction.Escalate) issueToChange.Escalate();
        else
        {
            if (db is MySqlOfficeDbContext) await db.Database.ExecuteSqlInterpolatedAsync($"SELECT attempt_id FROM collection_work WHERE attempt_id = {scope.AttemptId.ToString("D")} FOR UPDATE", cancellationToken);
            var original = await db.PaymentAttempts.SingleAsync(x => x.Id == scope.AttemptId, cancellationToken); await db.Entry(original).ReloadAsync(cancellationToken);
            var work = await db.CollectionWork.SingleOrDefaultAsync(x => x.AttemptId == original.Id, cancellationToken);
            if (work is null || original.SessionId is null) return new(BillingCode.Unavailable);
            await db.Entry(work).ReloadAsync(cancellationToken);
            try { work.RequestReconciliation(original, time.GetUtcNow()); }
            catch (InvalidOperationException) { return new(BillingCode.Conflict); }
            issueToChange.ReconciliationRequested(time.GetUtcNow());
        }
        var now = time.GetUtcNow();
        db.FinancialExceptionHistory.Add(FinancialExceptionHistory.Record(issueToChange.Id, command.Action switch { FinancialCaseAction.Assign => "Assigned", FinancialCaseAction.Escalate => "Escalated", _ => "ReconcileRequested" }, actor.UserId, command.Reason, now));
        db.BillingReceipts.Add(new BillingReceipt
        {
            ActorId = actor.UserId,
            OperationId = command.OperationId,
            PayloadHash = hash,
            AccountId = scope.AccountId,
            EntityId = issueToChange.Id,
            AccountVersion = issueToChange.Version,
            OccurredAt = now
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
            return new(BillingCode.Success, scope.AccountId, issueToChange.Id, issueToChange.Version);
        }
        catch (DbUpdateConcurrencyException) { return new(BillingCode.Conflict); }
        catch (DbUpdateException e) when (e.InnerException is MySql.Data.MySqlClient.MySqlException { Number: 1062 } or Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 })
        { return new(BillingCode.Conflict); }
    }
}
