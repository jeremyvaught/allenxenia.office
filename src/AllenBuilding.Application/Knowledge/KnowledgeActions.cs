using AllenBuilding.Application.Access;
using AllenBuilding.Domain.Knowledge;

namespace AllenBuilding.Application.Knowledge;

public enum ContentCode { Success, ValidationFailed, Unavailable, Conflict }
public enum KnowledgeScope { Public, Tenant }
public sealed record HelpSource(Guid ArticleId, Guid RevisionId, string Building, string Title, string Body, ArticleAudience Audience, DateOnly ReviewOn, DateTimeOffset EditedAt);
public sealed record ArticleEditor(Guid Id, Guid BuildingId, Guid Version, PublicationState State, Guid? PublishedRevisionId,
    HelpRevision Draft, IReadOnlyList<RevisionHistory> History);
public sealed record RevisionHistory(HelpRevision Revision, string EditorName);
public sealed record DraftArticle(Guid Id, Guid BuildingId, Guid? Version, string Title, string Body, ArticleAudience Audience, DateOnly ReviewOn);
public sealed record PublishArticle(Guid Id, Guid Version, Guid ReviewedRevisionId, bool Withdraw = false);

public interface IKnowledgeStore
{
    Task<IReadOnlyList<HelpSource>> FindAsync(Actor? actor, KnowledgeScope scope, string search, Guid? id, CancellationToken cancellationToken, Guid? membershipId = null);
    Task<IReadOnlyList<ArticleEditor>> ManageAsync(Actor actor, Guid? id, CancellationToken cancellationToken);
    Task<ContentCode> SaveAsync(Actor actor, DraftArticle command, CancellationToken cancellationToken);
    Task<ContentCode> PublishAsync(Actor actor, PublishArticle command, CancellationToken cancellationToken);
}

// Pages and future assistant retrieval share this publication and audience boundary.
public sealed class KnowledgeActions(ICurrentActor current, IKnowledgeStore store)
{
    public async Task<IReadOnlyList<HelpSource>> FindAsync(KnowledgeScope scope, string? search = null, Guid? id = null, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(scope) || search?.Length > 120) return [];
        var actor = scope == KnowledgeScope.Tenant ? await current.GetAsync(cancellationToken) : null;
        if (scope == KnowledgeScope.Tenant && (actor is null || (actor.ManagedBuildings.Count > 0 && !actor.ChallengedMfa))) return [];
        return await store.FindAsync(actor, scope, search?.Trim() ?? "", id, cancellationToken);
    }

    public async Task<IReadOnlyList<HelpSource>> FindForMembershipAsync(Guid membershipId, Guid? id = null, CancellationToken cancellationToken = default)
    {
        var actor = await current.GetAsync(cancellationToken);
        if (membershipId == Guid.Empty || actor is null || (actor.ManagedBuildings.Count > 0 && !actor.ChallengedMfa)) return [];
        return await store.FindAsync(actor, KnowledgeScope.Tenant, "", id, cancellationToken, membershipId);
    }

    public async Task<IReadOnlyList<ArticleEditor>> ManageAsync(Guid? id = null, CancellationToken cancellationToken = default)
    {
        var actor = await current.GetAsync(cancellationToken);
        return CanManage(actor) ? await store.ManageAsync(actor!, id, cancellationToken) : [];
    }

    public async Task<ContentCode> SaveAsync(DraftArticle command, CancellationToken cancellationToken = default)
    {
        var actor = await current.GetAsync(cancellationToken);
        if (!CanManage(actor) || !actor!.ManagedBuildings.Contains(command.BuildingId)) return ContentCode.Unavailable;
        if (command.Id == Guid.Empty || command.Version == Guid.Empty) return ContentCode.ValidationFailed;
        try { HelpRevision.Create(command.Id, command.Title, command.Body, command.Audience, actor.UserId, command.ReviewOn, DateTimeOffset.UtcNow); }
        catch (ArgumentException) { return ContentCode.ValidationFailed; }
        return await store.SaveAsync(actor, command, cancellationToken);
    }

    public async Task<ContentCode> PublishAsync(PublishArticle command, CancellationToken cancellationToken = default)
    {
        var actor = await current.GetAsync(cancellationToken);
        if (!CanManage(actor)) return ContentCode.Unavailable;
        if (command.Id == Guid.Empty || command.Version == Guid.Empty || command.ReviewedRevisionId == Guid.Empty) return ContentCode.ValidationFailed;
        return await store.PublishAsync(actor!, command, cancellationToken);
    }

    public static bool CanManage(Actor? actor) => actor is { ChallengedMfa: true } && actor.ManagedBuildings.Count > 0;
}
