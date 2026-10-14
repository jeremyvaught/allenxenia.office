using AllenBuilding.Application.Access;
using AllenBuilding.Application.Notifications;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Infrastructure.Notifications;

public sealed class NotificationInbox(OfficeDbContext db, TimeProvider time)
{
    public async Task<NotificationInboxPage> ReadAsync(Actor actor, int reviewPage, CancellationToken cancellationToken = default)
    {
        if (reviewPage is < 1 or > 1000000) throw new ArgumentOutOfRangeException(nameof(reviewPage));
        var now = time.GetUtcNow();
        // Reset delivery is account-wide. Only failed-delivery metadata for current tenants is visible to their managers.
        var currentTenants = from grant in db.ActiveGrants
                             join member in db.Memberships on grant.MembershipId equals member.Id
                             join suite in db.Suites on member.SuiteId equals suite.Id
                             where grant.UserId == member.UserId && grant.SuiteId == member.SuiteId &&
                                 actor.ManagedBuildings.Contains(suite.BuildingId) && member.RevokedAt == null &&
                                 member.StartsAt <= now && (member.EndsAt == null || member.EndsAt > now)
                             select member.UserId;
        var visible = db.AccountMessages.AsNoTracking().Where(x => x.UserId == actor.UserId ||
            (x.Kind == "PasswordReset" && x.State == MessageState.NeedsReview && currentTenants.Contains(x.UserId)) ||
            (x.Kind != "PasswordReset" && x.BuildingId != null && actor.ManagedBuildings.Contains(x.BuildingId.Value)));
        IQueryable<NotificationRow> Project(IQueryable<AccountMessage> messages) =>
            from message in messages
            join user in db.Users on message.UserId equals user.Id
            select new NotificationRow(message.Id, message.LeaseToken, message.Kind, message.State,
                message.Attempts, message.LastError, message.CreatedAt, message.Kind == "PasswordReset" ? user.Email : null);
        const int pageSize = 25;
        var exceptions = await Project(visible.Where(x => x.State == MessageState.NeedsReview).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .Skip((reviewPage - 1) * pageSize).Take(pageSize + 1)).ToListAsync(cancellationToken);
        var recent = await Project(visible.Where(x => x.State != MessageState.NeedsReview).OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Take(100)).ToListAsync(cancellationToken);
        return new(exceptions.Take(pageSize).ToArray(), recent, reviewPage, exceptions.Count > pageSize);
    }
}
