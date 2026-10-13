using AllenBuilding.Application.Access;
using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Infrastructure.Persistence;

public sealed class RefundDispositionStore(OfficeDbContext db, ICurrentActor current, RefundDispositionPolicy policy, AccountInvitations audit, TimeProvider time) : IRefundDispositionStore
{
    public async Task<RefundDispositionReview?> ReadAsync(Actor actor, Guid movementId, CancellationToken token)
    {
        if (!policy.Enabled) return null;
        await using var tx = await AuthorityLocks.BeginAsync(db, token); await AuthorityLocks.UserAsync(db, actor.UserId, token);
        var row = await (from movement in db.CashMovements.AsNoTracking()
                         join account in db.RentAccounts on movement.AccountId equals account.Id
                         join member in db.Memberships on account.MembershipId equals member.Id
                         join suite in db.Suites on member.SuiteId equals suite.Id
                         where movement.Id == movementId && movement.Kind == CashMovementKind.Refund && movement.SignedMinorUnits < 0 &&
                             actor.ManagedBuildings.Contains(suite.BuildingId) && db.BuildingManagers.Any(x => x.UserId == actor.UserId && x.BuildingId == suite.BuildingId)
                         select new { Movement = movement, Suite = suite.Name, suite.BuildingId }).SingleOrDefaultAsync(token);
        if (row is null) return null;
        await AuthorityLocks.ManagerAsync(db, actor.UserId, row.BuildingId, token); await CustomerProvisionQueue.LockAccount(db, row.Movement.AccountId, token);
        if (!await Authorized(actor, row.BuildingId, token)) return null;
        var review = await db.CashMovementReviews.AsNoTracking().SingleOrDefaultAsync(x => x.MovementId == movementId, token);
        if (review is null || review.State != CashMovementReviewState.Pending) return null;
        var originals = await db.PaymentAllocations.AsNoTracking().Where(x => x.PaymentId == row.Movement.CaptureId && x.AccountId == row.Movement.AccountId).OrderBy(x => x.ObligationId).Take(26).ToListAsync(token);
        if (originals.Count is < 1 or > 25) return null;
        var items = new List<RefundReviewItem>();
        foreach (var original in originals)
        {
            var debt = await db.Obligations.AsNoTracking().SingleAsync(x => x.Id == original.ObligationId && x.AccountId == row.Movement.AccountId, token);
            var returned = await db.RefundAllocations.Where(x => x.CaptureId == row.Movement.CaptureId && x.ObligationId == original.ObligationId).SumAsync(x => (long?)x.SignedMinorUnits, token) ?? 0;
            var credited = await db.ObligationCredits.Where(x => x.ObligationId == original.ObligationId).SumAsync(x => (long?)x.AmountMinorUnits, token) ?? 0;
            var available = original.AmountMinorUnits + returned;
            if (available > 0) items.Add(new(debt.Id, debt.Version, debt.Description, available, debt.AmountMinorUnits - credited));
        }
        if (items.Sum(x => x.AvailableCapturedMinorUnits) < -row.Movement.SignedMinorUnits || !await Authorized(actor, row.BuildingId, token)) return null;
        return new(row.Movement.AccountId, movementId, review.Version, row.BuildingId, row.Suite, row.Movement.SourceId, -row.Movement.SignedMinorUnits, row.Movement.Currency, items);
    }
    public async Task<BillingResult> ApplyAsync(Actor actor, ApplyRefundDisposition command, string hash, CancellationToken token)
    {
        if (!policy.Enabled) return new(BillingCode.Unavailable);
        await using var tx = await AuthorityLocks.BeginAsync(db, token); await AuthorityLocks.UserAsync(db, actor.UserId, token);
        var building = await (from account in db.RentAccounts.AsNoTracking()
                              join member in db.Memberships on account.MembershipId equals member.Id
                              join suite in db.Suites on member.SuiteId equals suite.Id
                              where account.Id == command.AccountId
                              select (Guid?)suite.BuildingId).SingleOrDefaultAsync(token);
        if (building is null) return new(BillingCode.Unavailable);
        await AuthorityLocks.ManagerAsync(db, actor.UserId, building.Value, token); await CustomerProvisionQueue.LockAccount(db, command.AccountId, token);
        if (!await Authorized(actor, building.Value, token)) return new(BillingCode.Unavailable);
        var receipt = await db.BillingReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.ActorId == actor.UserId && x.OperationId == command.OperationId, token);
        if (receipt is not null) return receipt.PayloadHash == hash && receipt.AccountId == command.AccountId
            ? new(BillingCode.Success, receipt.AccountId, receipt.EntityId, receipt.AccountVersion) : new(BillingCode.Conflict);
        var movement = await db.CashMovements.AsNoTracking().SingleOrDefaultAsync(x => x.Id == command.MovementId && x.AccountId == command.AccountId, token);
        if (movement is null) return new(BillingCode.Unavailable);
        var review = await db.CashMovementReviews.SingleOrDefaultAsync(x => x.MovementId == movement.Id, token);
        if (review is null) return new(BillingCode.Conflict); await db.Entry(review).ReloadAsync(token);
        if (review.Version != command.ReviewVersion || review.State != CashMovementReviewState.Pending || movement.Kind != CashMovementKind.Refund || movement.SignedMinorUnits >= 0)
            return new(BillingCode.Conflict);
        if (command.Items.Sum(x => x.AmountMinorUnits) != -movement.SignedMinorUnits) return new(BillingCode.ValidationFailed);
        var allocations = new List<RefundAllocation>(); var credits = new List<ObligationCredit>(); var debts = new List<Obligation>();
        foreach (var item in command.Items)
        {
            if (db is MySqlOfficeDbContext) await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM obligations WHERE id = {item.ObligationId.ToString("D")} FOR UPDATE", token);
            var debt = await db.Obligations.SingleOrDefaultAsync(x => x.Id == item.ObligationId && x.AccountId == command.AccountId, token);
            if (debt is null) return new(BillingCode.Unavailable); await db.Entry(debt).ReloadAsync(token);
            if (debt.Version != item.Version) return new(BillingCode.Conflict);
            var original = await db.PaymentAllocations.AsNoTracking().SingleOrDefaultAsync(x => x.PaymentId == movement.CaptureId && x.ObligationId == item.ObligationId && x.AccountId == command.AccountId, token);
            if (original is null) return new(BillingCode.Conflict);
            var returned = await db.RefundAllocations.Where(x => x.CaptureId == movement.CaptureId && x.ObligationId == item.ObligationId).SumAsync(x => (long?)x.SignedMinorUnits, token) ?? 0;
            var credited = await db.ObligationCredits.Where(x => x.ObligationId == item.ObligationId).SumAsync(x => (long?)x.AmountMinorUnits, token) ?? 0;
            try
            {
                var allocation = RefundAllocation.Record(movement, original, item.Version, item.AmountMinorUnits, original.AmountMinorUnits + returned);
                allocations.Add(allocation);
                if (command.Disposition == RefundDisposition.CreditDebt)
                    credits.Add(ObligationCredit.RecordRefundCredit(command.OperationId, actor.UserId, allocation, debt.AmountMinorUnits - credited, command.Reason, time.GetUtcNow()));
                debts.Add(debt);
            }
            catch (ArgumentException) { return new(BillingCode.ValidationFailed); }
        }
        // Complete batch validation before attaching economic rows. The account lock serializes competing dispositions/imports.
        if (!await Authorized(actor, building.Value, token)) return new(BillingCode.Unavailable);
        review.ApplyRefund(movement, command.OperationId, actor.UserId, command.Disposition, command.Reason, time.GetUtcNow());
        db.RefundAllocations.AddRange(allocations); db.ObligationCredits.AddRange(credits); foreach (var debt in debts) debt.FinancialStateChanged();
        var issue = await db.FinancialExceptions.SingleOrDefaultAsync(x => x.AttemptId == movement.AttemptId, token);
        if (issue is null)
        {
            var attempt = await db.PaymentAttempts.AsNoTracking().SingleAsync(x => x.Id == movement.AttemptId, token);
            issue = FinancialException.Open(attempt, FinancialFailure.ExternalCashMovement, null, time.GetUtcNow()); db.FinancialExceptions.Add(issue);
        }
        else { await db.Entry(issue).ReloadAsync(token); issue.Observe(FinancialFailure.ExternalCashMovement, null, time.GetUtcNow()); }
        db.FinancialExceptionHistory.Add(FinancialExceptionHistory.Record(issue.Id, "RefundDispositionApplied", actor.UserId, command.Reason, time.GetUtcNow()));
        var accountVersion = await db.RentAccounts.Where(x => x.Id == command.AccountId).Select(x => x.Version).SingleAsync(token);
        db.BillingReceipts.Add(new() { ActorId = actor.UserId, OperationId = command.OperationId, PayloadHash = hash, AccountId = command.AccountId, EntityId = movement.Id, AccountVersion = accountVersion, OccurredAt = time.GetUtcNow() });
        audit.Audit(actor.UserId, command.Disposition == RefundDisposition.CreditDebt ? "RefundDebtCredited" : "RefundDebtRetained", movement.Id.ToString("D"), command.Reason);
        try { await db.SaveChangesAsync(token); await tx.CommitAsync(token); return new(BillingCode.Success, command.AccountId, movement.Id, accountVersion); }
        catch (DbUpdateException) { await tx.RollbackAsync(token); db.ChangeTracker.Clear(); return new(BillingCode.Conflict); }
    }
    private async Task<bool> Authorized(Actor original, Guid building, CancellationToken token)
    {
        var fresh = await current.GetAsync(token);
        return fresh is { ChallengedMfa: true, RecentMfa: true } && fresh.UserId == original.UserId && fresh.ManagedBuildings.Contains(building) &&
            await db.BuildingManagers.AnyAsync(x => x.UserId == original.UserId && x.BuildingId == building, token);
    }
}
