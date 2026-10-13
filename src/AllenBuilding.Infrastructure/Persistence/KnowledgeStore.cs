using AllenBuilding.Application.Access;
using AllenBuilding.Application.Knowledge;
using AllenBuilding.Domain.Knowledge;
using AllenBuilding.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Infrastructure.Persistence;

public sealed class KnowledgeStore(OfficeDbContext db, ICurrentActor current, AccountInvitations audit, TimeProvider time) : IKnowledgeStore
{
    public async Task<IReadOnlyList<HelpSource>> FindAsync(Actor? actor, KnowledgeScope scope, string search, Guid? id, CancellationToken cancellationToken, Guid? membershipId = null)
    {
        var now = time.GetUtcNow();
        var userId = actor?.UserId ?? "";
        var allowPrivate = scope == KnowledgeScope.Tenant && actor is not null;
        // Apply publication and audience restrictions before text search/projection.
        var sources = from article in db.HelpArticles.AsNoTracking()
                      join revision in db.HelpRevisions on article.PublishedRevisionId equals (Guid?)revision.Id
                      join building in db.Buildings on article.BuildingId equals building.Id
                      where article.State == PublicationState.Published && revision.ArticleId == article.Id &&
                          (revision.Audience == ArticleAudience.Public || (allowPrivate &&
                              (from membership in db.Memberships
                               join grant in db.ActiveGrants on membership.Id equals grant.MembershipId
                               join suite in db.Suites on membership.SuiteId equals suite.Id
                               where membership.UserId == userId && suite.BuildingId == article.BuildingId && membership.RevokedAt == null &&
                                   (membershipId == null || membership.Id == membershipId) &&
                                   membership.StartsAt <= now && (membership.EndsAt == null || membership.EndsAt > now)
                               select membership.Id).Any()))
                      select new { article.Id, Revision = revision, Building = building.Name };
        if (id is { } articleId) sources = sources.Where(x => x.Id == articleId);
        if (search != "")
        {
            var normalized = search.ToLowerInvariant();
            sources = sources.Where(x => x.Revision.Title.ToLower().Contains(normalized) || x.Revision.Body.ToLower().Contains(normalized));
        }
        return await sources.OrderBy(x => x.Revision.Title).ThenBy(x => x.Id).Take(100)
            .Select(x => new HelpSource(x.Id, x.Revision.Id, x.Building, x.Revision.Title, x.Revision.Body,
                x.Revision.Audience, x.Revision.ReviewOn, x.Revision.EditedAt)).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ArticleEditor>> ManageAsync(Actor actor, Guid? id, CancellationToken cancellationToken)
    {
        var query = from article in db.HelpArticles.AsNoTracking()
                    join manager in db.BuildingManagers on article.BuildingId equals manager.BuildingId
                    join revision in db.HelpRevisions on article.DraftRevisionId equals revision.Id
                    where manager.UserId == actor.UserId && actor.ManagedBuildings.Contains(article.BuildingId) && revision.ArticleId == article.Id
                    select new { Article = article, Draft = revision };
        if (id is { } articleId) query = query.Where(x => x.Article.Id == articleId);
        var rows = await query.OrderBy(x => x.Draft.Title).ThenBy(x => x.Article.Id).Take(100).ToListAsync(cancellationToken);
        var result = new List<ArticleEditor>();
        foreach (var row in rows)
        {
            IReadOnlyList<RevisionHistory> history = id is null ? [] : await (from revision in db.HelpRevisions.AsNoTracking()
                                                                              join user in db.Users on revision.EditorId equals user.Id
                                                                              where revision.ArticleId == row.Article.Id
                                                                              orderby revision.EditedAt descending, revision.Id descending
                                                                              select new RevisionHistory(revision, user.DisplayName == "" ? user.UserName! : user.DisplayName)).Take(50).ToListAsync(cancellationToken);
            result.Add(new(row.Article.Id, row.Article.BuildingId, row.Article.Version, row.Article.State, row.Article.PublishedRevisionId, row.Draft, history));
        }
        return result;
    }

    public async Task<ContentCode> SaveAsync(Actor actor, DraftArticle command, CancellationToken cancellationToken)
    {
        await using var transaction = await AuthorityLocks.BeginAsync(db, cancellationToken);
        if (!await Authorize(actor, command.BuildingId, command.Id, cancellationToken)) return ContentCode.Unavailable;
        var article = await db.HelpArticles.SingleOrDefaultAsync(x => x.Id == command.Id, cancellationToken);
        if (article is not null && article.BuildingId != command.BuildingId) return ContentCode.Unavailable;
        if (command.Version is null ? article is not null : article is null || article.Version != command.Version) return ContentCode.Conflict;
        var revision = HelpRevision.Create(command.Id, command.Title, command.Body, command.Audience, actor.UserId, command.ReviewOn, time.GetUtcNow());
        if (article is null) { article = HelpArticle.Create(command.Id, command.BuildingId, revision.Id); db.HelpArticles.Add(article); }
        else article.SaveDraft(revision.Id);
        db.HelpRevisions.Add(revision);
        Record(actor, "HelpDraftSaved", article.Id, revision.Id);
        return await Commit(transaction, cancellationToken);
    }

    public async Task<ContentCode> PublishAsync(Actor actor, PublishArticle command, CancellationToken cancellationToken)
    {
        await using var transaction = await AuthorityLocks.BeginAsync(db, cancellationToken);
        var building = await db.HelpArticles.Where(x => x.Id == command.Id).Select(x => (Guid?)x.BuildingId).SingleOrDefaultAsync(cancellationToken);
        if (building is null || !await Authorize(actor, building.Value, command.Id, cancellationToken)) return ContentCode.Unavailable;
        var article = await db.HelpArticles.SingleAsync(x => x.Id == command.Id, cancellationToken);
        if (article.Version != command.Version || article.DraftRevisionId != command.ReviewedRevisionId) return ContentCode.Conflict;
        if (!await db.HelpRevisions.AnyAsync(x => x.Id == command.ReviewedRevisionId && x.ArticleId == article.Id, cancellationToken)) return ContentCode.Conflict;
        try { if (command.Withdraw) article.Withdraw(); else article.Publish(command.ReviewedRevisionId); }
        catch (InvalidOperationException) { return ContentCode.Conflict; }
        Record(actor, command.Withdraw ? "HelpWithdrawn" : "HelpPublished", article.Id, command.ReviewedRevisionId);
        return await Commit(transaction, cancellationToken);
    }

    private async Task<bool> Authorize(Actor actor, Guid buildingId, Guid articleId, CancellationToken cancellationToken)
    {
        await AuthorityLocks.UserAsync(db, actor.UserId, cancellationToken);
        await AuthorityLocks.ManagerAsync(db, actor.UserId, buildingId, cancellationToken);
        if (db is MySqlOfficeDbContext)
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM help_articles WHERE id = {articleId.ToString("D")} FOR UPDATE", cancellationToken);
        var fresh = await current.GetAsync(cancellationToken);
        return KnowledgeActions.CanManage(fresh) && fresh!.ManagedBuildings.Contains(buildingId) &&
            await db.BuildingManagers.AnyAsync(x => x.BuildingId == buildingId && x.UserId == actor.UserId, cancellationToken);
    }

    private void Record(Actor actor, string action, Guid articleId, Guid revisionId)
    {
        audit.Audit(actor.UserId, action, articleId.ToString("D"), "Revision " + revisionId.ToString("D"));
    }

    private async Task<ContentCode> Commit(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction, CancellationToken cancellationToken)
    {
        try { await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); return ContentCode.Success; }
        catch (DbUpdateConcurrencyException) { return ContentCode.Conflict; }
        catch (DbUpdateException exception) when (exception.InnerException is MySql.Data.MySqlClient.MySqlException { Number: 1062 } or Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 })
        { return ContentCode.Conflict; }
    }
}
