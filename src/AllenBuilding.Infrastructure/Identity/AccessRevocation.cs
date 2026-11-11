using AllenBuilding.Application.Access;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Infrastructure.Identity;

public sealed class AccessRevocation(OfficeDbContext db, ICurrentActor current, AccountInvitations audit, TimeProvider time)
{
    public async Task<AccessCode> RevokeAsync(Guid membershipId, Guid version, string reason, CancellationToken cancellationToken = default)
    {
        if (membershipId == Guid.Empty || version == Guid.Empty || string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 2000)
            return AccessCode.ValidationFailed;
        var initial = await current.GetAsync(cancellationToken);
        // No loaded actor stays a 400 response, as it was before AccessCode.
        if (initial is null)
            return AccessCode.ValidationFailed;
        await using var transaction = await AuthorityLocks.BeginAsync(db, cancellationToken);
        await AuthorityLocks.UserAsync(db, initial.UserId, cancellationToken);
        var building = await (from membership in db.Memberships
                              join suite in db.Suites on membership.SuiteId equals suite.Id
                              where membership.Id == membershipId
                              select (Guid?)suite.BuildingId).SingleOrDefaultAsync(cancellationToken);
        if (building is null)
            return AccessCode.Unavailable;
        await AuthorityLocks.ManagerAsync(db, initial.UserId, building.Value, cancellationToken);
        await AuthorityLocks.MembershipAsync(db, membershipId, cancellationToken);
        var actor = await current.GetAsync(cancellationToken);
        if (actor is not { RecentMfa: true } || !actor.ManagedBuildings.Contains(building.Value))
            return AccessCode.Unavailable;
        var membershipToRevoke = await db.Memberships.SingleAsync(x => x.Id == membershipId, cancellationToken);
        if (membershipToRevoke.RevokedAt is not null)
            return AccessCode.Success;
        if (membershipToRevoke.Version != version)
            return AccessCode.Conflict;
        membershipToRevoke.Revoke(actor.UserId, reason, time.GetUtcNow());
        await db.AssistantMaintenanceReviews
            .Where(x => x.MembershipId == membershipId && x.State == AllenBuilding.Domain.Maintenance.AssistantReviewState.Ready)
            .ExecuteUpdateAsync(x => x.SetProperty(r => r.State, AllenBuilding.Domain.Maintenance.AssistantReviewState.Revoked), cancellationToken);
        var grant = await db.ActiveGrants.SingleOrDefaultAsync(x => x.MembershipId == membershipId, cancellationToken);
        if (grant is not null)
            db.ActiveGrants.Remove(grant);
        audit.Audit(actor.UserId, "AccessRevoked", membershipId.ToString());
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        // No provider calls. Access-only revocation does not cancel rent or end billing consent.
        return AccessCode.Success;
    }
}
