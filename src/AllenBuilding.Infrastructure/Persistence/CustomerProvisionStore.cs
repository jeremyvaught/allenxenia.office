using AllenBuilding.Application.Access;
using AllenBuilding.Application.Billing;
using AllenBuilding.Application.Diagnostics;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Infrastructure.Persistence;

public sealed class CustomerProvisionStore(OfficeDbContext db, ICurrentActor current, CustomerProvisionPolicy policy, AccountInvitations audit, TimeProvider time) : ICustomerProvisionStore
{
    public async Task<CustomerProvisionResult> EnsureAsync(Actor actor, Guid accountId, CancellationToken token)
    {
        if (!policy.Enabled || !PaymentReferences.Valid(policy.ProviderAccount, "acct_")) return new(CollectionCode.Unavailable);
        await using var tx = await AuthorityLocks.BeginAsync(db, token); await AuthorityLocks.UserAsync(db, actor.UserId, token);
        var membershipId = await db.RentAccounts.AsNoTracking().Where(x => x.Id == accountId).Select(x => (Guid?)x.MembershipId).SingleOrDefaultAsync(token);
        if (membershipId is null) return new(CollectionCode.Unavailable);
        await AuthorityLocks.MembershipAsync(db, membershipId.Value, token); await CustomerProvisionQueue.LockAccount(db, accountId, token);
        var fresh = await current.GetAsync(token); var now = time.GetUtcNow();
        if (fresh is null || fresh.UserId != actor.UserId || (fresh.ManagedBuildings.Count > 0 && !fresh.ChallengedMfa) ||
            !await db.Memberships.AsNoTracking().AnyAsync(x => x.Id == membershipId && x.UserId == actor.UserId && x.RevokedAt == null &&
                x.StartsAt <= now && (x.EndsAt == null || x.EndsAt > now) && db.ActiveGrants.Any(g => g.MembershipId == x.Id), token)) return new(CollectionCode.Unavailable);
        var original = await db.CustomerProvisions.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == accountId && x.ProviderAccount == policy.ProviderAccount && x.LiveMode == policy.LiveMode, token);
        if (original is not null) return new(CollectionCode.Success, original.Id, original.State);
        if (await db.BillingCustomers.AnyAsync(x => x.AccountId == accountId && x.ProviderAccount == policy.ProviderAccount && x.LiveMode == policy.LiveMode, token))
            return new(CollectionCode.Success, State: CustomerProvisionState.Bound);
        var origin = OfficeTelemetry.Capture(); var work = CustomerProvision.Enqueue(accountId, actor.UserId, policy.ProviderAccount, policy.LiveMode, now, origin?.TraceId, origin?.SpanId);
        db.CustomerProvisions.Add(work); audit.Audit(actor.UserId, "CustomerProvisionRequested", accountId.ToString("D"), "Original " + work.Id.ToString("D"));
        await db.SaveChangesAsync(token); await tx.CommitAsync(token); return new(CollectionCode.Success, work.Id, work.State);
    }
}
