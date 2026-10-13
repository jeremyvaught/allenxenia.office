using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Infrastructure.Persistence;

// Each operation gets a fresh scope. External calls occur between claim and completion transactions.
public sealed class CustomerProvisionQueue(OfficeDbContext db, TimeProvider time) : ICustomerProvisionQueue
{
    public async Task<CustomerProvisionLease?> ClaimAsync(CancellationToken token)
    {
        var now = time.GetUtcNow(); await using var tx = await AuthorityLocks.BeginAsync(db, token);
        var candidate = await db.CustomerProvisions.AsNoTracking().Where(x => (x.State == CustomerProvisionState.Queued && x.NextAttemptAt <= now) ||
            (x.State == CustomerProvisionState.Leased && x.LeaseUntil <= now)).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).FirstOrDefaultAsync(token);
        if (candidate is null) return null;
        var membershipId = await db.RentAccounts.AsNoTracking().Where(x => x.Id == candidate.AccountId).Select(x => x.MembershipId).SingleAsync(token);
        await AuthorityLocks.UserAsync(db, candidate.ActorId, token); await AuthorityLocks.MembershipAsync(db, membershipId, token);
        await LockAccount(db, candidate.AccountId, token); await LockWork(candidate.Id, token);
        var original = await db.CustomerProvisions.SingleAsync(x => x.Id == candidate.Id, token); await db.Entry(original).ReloadAsync(token);
        if (!original.Eligible(now)) return null;
        if (original.DispatchedAt is null && (!await db.Memberships.AsNoTracking().AnyAsync(x => x.Id == membershipId && x.UserId == original.ActorId && x.RevokedAt == null &&
            x.StartsAt <= now && (x.EndsAt == null || x.EndsAt > now) && db.ActiveGrants.Any(g => g.MembershipId == x.Id), token) ||
            !await db.Users.AsNoTracking().AnyAsync(x => x.Id == original.ActorId && x.EmailConfirmed && (!x.LockoutEnabled || x.LockoutEnd == null || x.LockoutEnd <= now), token)))
        {
            original.Suppress(now); await db.SaveChangesAsync(token); await tx.CommitAsync(token); return null;
        }
        if (!original.Claim(now)) { await db.SaveChangesAsync(token); await tx.CommitAsync(token); return null; }
        if (original.DispatchedAt is null) original.BeginDispatch(now);
        await db.SaveChangesAsync(token); await tx.CommitAsync(token); return new(original, original.LeaseToken);
    }
    public async Task<bool> CompleteAsync(CustomerProvisionLease lease, CustomerObservation observation, CancellationToken token)
    {
        await using var tx = await AuthorityLocks.BeginAsync(db, token); await LockAccount(db, lease.Original.AccountId, token); await LockWork(lease.Original.Id, token);
        var original = await db.CustomerProvisions.SingleOrDefaultAsync(x => x.Id == lease.Original.Id, token); if (original is null) return false;
        await db.Entry(original).ReloadAsync(token); var now = time.GetUtcNow(); if (!original.Owns(lease.Token, now)) return false;
        var leaseUntil = original.LeaseUntil!.Value;
        var outcome = observation.Outcome;
        if (outcome == CustomerProvisionOutcome.Verified && (observation.AccountId != original.AccountId || observation.OperationId != original.Id ||
            observation.ProviderAccount != original.ProviderAccount || observation.LiveMode != original.LiveMode || !PaymentReferences.Valid(observation.CustomerId!, "cus_")))
            outcome = CustomerProvisionOutcome.Invalid;
        if (outcome == CustomerProvisionOutcome.Verified)
        {
            var binding = await db.BillingCustomers.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == original.AccountId && x.ProviderAccount == original.ProviderAccount && x.LiveMode == original.LiveMode, token);
            var otherOwner = await db.BillingCustomers.AnyAsync(x => x.ProviderAccount == original.ProviderAccount && x.LiveMode == original.LiveMode && x.CustomerId == observation.CustomerId && x.AccountId != original.AccountId, token);
            if (otherOwner || (binding is not null && binding.CustomerId != observation.CustomerId)) original.Review("BindingConflict");
            else
            {
                if (binding is null) db.BillingCustomers.Add(BillingCustomerBinding.Create(original.AccountId, original.ProviderAccount, original.LiveMode, observation.CustomerId!, now));
                if (!original.Complete(lease.Token, outcome, observation.CustomerId, time.GetUtcNow())) return false;
            }
        }
        else if (!original.Complete(lease.Token, outcome, null, time.GetUtcNow())) return false;
        // Binding and original completion commit together; stale fences cannot append a binding.
        if (time.GetUtcNow() >= leaseUntil) return false;
        try { await db.SaveChangesAsync(token); await tx.CommitAsync(token); return true; }
        catch (DbUpdateException)
        {
            await tx.RollbackAsync(token); db.ChangeTracker.Clear(); return false;
        }
    }
    internal static Task<int> LockAccount(OfficeDbContext db, Guid accountId, CancellationToken token) => db is MySqlOfficeDbContext
        ? db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM rent_accounts WHERE id = {accountId.ToString("D")} FOR UPDATE", token) : Task.FromResult(0);
    private Task<int> LockWork(Guid id, CancellationToken token) => db is MySqlOfficeDbContext
        ? db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM customer_provisions WHERE id = {id.ToString("D")} FOR UPDATE", token) : Task.FromResult(0);
}
