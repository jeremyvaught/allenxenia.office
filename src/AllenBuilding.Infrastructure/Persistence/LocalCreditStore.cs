using AllenBuilding.Application.Access;
using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Infrastructure.Persistence;

public sealed class LocalCreditStore(OfficeDbContext db, ICurrentActor current, LocalCreditPolicy policy, AccountInvitations audit, TimeProvider time) : ILocalCreditStore
{
    public async Task<BillingResult> ApplyAsync(Actor actor, CreditLocalDebt command, string hash, CancellationToken token)
    {
        if (!policy.Enabled) return new(BillingCode.Unavailable);
        await using var tx = await AuthorityLocks.BeginAsync(db, token); await AuthorityLocks.UserAsync(db, actor.UserId, token);
        var building = await (from account in db.RentAccounts.AsNoTracking()
                              join membership in db.Memberships on account.MembershipId equals membership.Id
                              join suite in db.Suites on membership.SuiteId equals suite.Id
                              where account.Id == command.AccountId
                              select (Guid?)suite.BuildingId).SingleOrDefaultAsync(token);
        if (building is null) return new(BillingCode.Unavailable);
        await AuthorityLocks.ManagerAsync(db, actor.UserId, building.Value, token); await CustomerProvisionQueue.LockAccount(db, command.AccountId, token);
        var fresh = await current.GetAsync(token);
        if (fresh is not { ChallengedMfa: true, RecentMfa: true } || fresh.UserId != actor.UserId || !fresh.ManagedBuildings.Contains(building.Value) ||
            !await db.BuildingManagers.AnyAsync(x => x.UserId == actor.UserId && x.BuildingId == building, token)) return new(BillingCode.Unavailable);
        var receipt = await db.BillingReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.ActorId == actor.UserId && x.OperationId == command.OperationId, token);
        if (receipt is not null) return receipt.PayloadHash == hash && receipt.AccountId == command.AccountId ? new(BillingCode.Success, receipt.AccountId, receipt.EntityId, receipt.AccountVersion) : new(BillingCode.Conflict);
        if (db is MySqlOfficeDbContext) await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM obligations WHERE id = {command.ObligationId.ToString("D")} FOR UPDATE", token);
        var debt = await db.Obligations.SingleOrDefaultAsync(x => x.Id == command.ObligationId && x.AccountId == command.AccountId, token);
        if (debt is null) return new(BillingCode.Unavailable); await db.Entry(debt).ReloadAsync(token);
        if (debt.Version != command.ReviewedVersion) return new(BillingCode.Conflict);
        // Absence of a guard is insufficient. Any prior binding, dispatched creation or payment history prevents a local-only reduction.
        if (await db.BillingCustomers.AnyAsync(x => x.AccountId == command.AccountId, token) ||
            await db.CustomerProvisions.AnyAsync(x => x.AccountId == command.AccountId && x.DispatchedAt != null, token) ||
            await db.PaymentAttempts.AnyAsync(x => x.AccountId == command.AccountId, token) ||
            await db.CollectionGuards.AnyAsync(x => x.AccountId == command.AccountId, token) ||
            await db.ProviderRentInvoices.AnyAsync(x => x.AccountId == command.AccountId, token) ||
            await db.FinancialExceptions.AnyAsync(x => x.AccountId == command.AccountId && x.State != FinancialExceptionState.Resolved, token)) return new(BillingCode.Conflict);
        var credited = await db.ObligationCredits.Where(x => x.ObligationId == debt.Id).SumAsync(x => (long?)x.AmountMinorUnits, token) ?? 0;
        var paid = await db.PaymentAllocations.Where(x => x.ObligationId == debt.Id).SumAsync(x => (long?)x.AmountMinorUnits, token) ?? 0;
        ObligationCredit credit;
        try { credit = ObligationCredit.RecordLocal(command.OperationId, actor.UserId, new(debt.Id, debt.AccountId, debt.Version, debt.AmountMinorUnits - credited - paid, debt.Currency), command.AmountMinorUnits, command.Kind, command.Reason, time.GetUtcNow()); }
        catch (ArgumentException) { return new(BillingCode.ValidationFailed); }
        db.ObligationCredits.Add(credit); debt.FinancialStateChanged();
        var currentAccount = await db.RentAccounts.AsNoTracking().SingleAsync(x => x.Id == command.AccountId, token);
        db.BillingReceipts.Add(new() { ActorId = actor.UserId, OperationId = command.OperationId, PayloadHash = hash, AccountId = currentAccount.Id, AccountVersion = currentAccount.Version, EntityId = credit.Id, OccurredAt = time.GetUtcNow() });
        audit.Audit(actor.UserId, command.Kind == ObligationCreditKind.WriteOff ? "LocalDebtWrittenOff" : "LocalDebtCredited", debt.Id.ToString("D"), command.Reason);
        await db.SaveChangesAsync(token); await tx.CommitAsync(token); return new(BillingCode.Success, currentAccount.Id, credit.Id, currentAccount.Version);
    }
}
