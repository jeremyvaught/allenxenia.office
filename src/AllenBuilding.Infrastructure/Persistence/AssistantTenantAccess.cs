using AllenBuilding.Application.Access;
using AllenBuilding.Application.Assistant;
using AllenBuilding.Application.Knowledge;
using AllenBuilding.Application.Maintenance;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Infrastructure.Persistence;

public sealed class AssistantTenantAccess(OfficeDbContext db, ICurrentActor current, KnowledgeActions knowledge, TimeProvider time) : IAssistantTenantAccess
{
    public async Task<AssistantTenantScope?> CaptureAsync(Guid membershipId, CancellationToken cancellationToken)
    {
        var actor = await current.GetAsync(cancellationToken);
        if (!MaintenanceActions.CanUse(actor, false) || membershipId == Guid.Empty) return null;
        var now = time.GetUtcNow();
        return await (from member in db.Memberships.AsNoTracking()
                      join grant in db.ActiveGrants on member.Id equals grant.MembershipId
                      join suite in db.Suites on member.SuiteId equals suite.Id
                      join user in db.Users on member.UserId equals user.Id
                      where member.Id == membershipId && member.UserId == actor!.UserId && grant.UserId == actor.UserId &&
                          grant.SuiteId == member.SuiteId && member.RevokedAt == null && member.StartsAt <= now && (member.EndsAt == null || member.EndsAt > now)
                      select new AssistantTenantScope(member.UserId, member.Id, member.Version, user.SecurityStamp!, suite.Name)).SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<bool> ReleaseAsync(AssistantTenantScope scope, IReadOnlyList<AssistantSource> contextualSources,
        Func<CancellationToken, Task> emit, CancellationToken cancellationToken)
    {
        if (contextualSources.Count > 5) return false;
        await using var transaction = await AuthorityLocks.BeginAsync(db, cancellationToken);
        await AuthorityLocks.UserAsync(db, scope.UserId, cancellationToken);
        await AuthorityLocks.MembershipAsync(db, scope.MembershipId, cancellationToken);
        if (!await ValidateLockedAsync(scope, contextualSources, cancellationToken)) return false;
        // No provider/model call inside this transaction. The already-rendered bounded answer is admitted once.
        await emit(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    // Shared by transport admission and durable review writes after user/membership locks are held.
    public async Task<bool> ValidateLockedAsync(AssistantTenantScope scope, IReadOnlyList<AssistantSource> contextualSources, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null || contextualSources.Count > 5) return false;
        var fresh = await CaptureAsync(scope.MembershipId, cancellationToken);
        if (fresh is null || fresh.UserId != scope.UserId || fresh.Version != scope.Version || fresh.SecurityStamp != scope.SecurityStamp) return false;
        foreach (var source in contextualSources.OrderBy(x => x.ArticleId))
        {
            if (db is MySqlOfficeDbContext)
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM help_articles WHERE id = {source.ArticleId.ToString("D")} FOR SHARE", cancellationToken);
            var published = await knowledge.FindForMembershipAsync(scope.MembershipId, source.ArticleId, cancellationToken);
            if (published.Count != 1 || published[0].RevisionId != source.RevisionId) return false;
        }
        return true;
    }
}
