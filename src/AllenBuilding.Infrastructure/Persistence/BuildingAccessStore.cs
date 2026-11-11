using AllenBuilding.Application.Access;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Infrastructure.Persistence;

public sealed class BuildingAccessStore(OfficeDbContext db, ICurrentActor current, AuditTrail audit) : IBuildingAccessStore
{
    public async Task<BuildingAccessOverview> ReadOverviewAsync(Actor actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var buildings = await db.Buildings.AsNoTracking().Where(x => actor.ManagedBuildings.Contains(x.Id)).ToListAsync(cancellationToken);
        var access = await (from membership in db.Memberships.AsNoTracking()
                            join suite in db.Suites on membership.SuiteId equals suite.Id
                            join user in db.Users on membership.UserId equals user.Id
                            where actor.ManagedBuildings.Contains(suite.BuildingId) && membership.RevokedAt == null
                            orderby suite.Name, membership.Id
                            select new AccessRow(membership.Id, membership.Version, suite.Name, user.Email!))
            .Take(100).ToListAsync(cancellationToken);
        var invitations = await (from invitation in db.Invitations.AsNoTracking()
                                 join suite in db.Suites on invitation.SuiteId equals suite.Id
                                 join user in db.Users on invitation.UserId equals user.Id
                                 where actor.ManagedBuildings.Contains(suite.BuildingId) && invitation.AcceptedAt == null
                                 orderby invitation.ExpiresAt descending, invitation.Id
                                 select new InvitationRow(invitation.Id, invitation.Version, suite.Name, user.Email!, invitation.ExpiresAt,
                                     invitation.RevokedAt != null ? "Revoked" : invitation.ExpiresAt <= now ? "Expired" : "Pending"))
            .Take(100).ToListAsync(cancellationToken);
        var suites = await db.Suites.AsNoTracking().Where(x => actor.ManagedBuildings.Contains(x.BuildingId))
            .OrderBy(x => x.Name).ThenBy(x => x.Id).Take(100).ToListAsync(cancellationToken);
        return new(buildings, suites, access, invitations);
    }

    public async Task<SuiteResult> AddSuiteAsync(Guid buildingId, string name, CancellationToken cancellationToken)
    {
        var initial = await current.GetAsync(cancellationToken);
        if (initial is null)
            return new(SuiteCode.Forbidden);
        await using var transaction = await AuthorityLocks.BeginAsync(db, cancellationToken);
        await AuthorityLocks.UserAsync(db, initial.UserId, cancellationToken);
        await AuthorityLocks.ManagerAsync(db, initial.UserId, buildingId, cancellationToken);
        // Re-read after the locks so a just-removed manager or expired MFA cannot add a suite.
        var actor = await current.GetAsync(cancellationToken);
        if (actor is not { RecentMfa: true })
            return new(SuiteCode.Forbidden);
        if (!actor.ManagedBuildings.Contains(buildingId))
            return new(SuiteCode.ValidationFailed);
        if (await db.Suites.AnyAsync(x => x.BuildingId == buildingId && x.Name == name, cancellationToken))
            return new(SuiteCode.Conflict);
        var suite = new Suite { BuildingId = buildingId, Name = name };
        db.Suites.Add(suite);
        audit.Record(actor.UserId, "SuiteCreated", suite.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(SuiteCode.Success, suite.Name);
    }
}
