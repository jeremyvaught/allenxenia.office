namespace AllenBuilding.Domain.Knowledge;

public enum ArticleAudience { Public, ActiveTenant }
public enum PublicationState { Draft, Published, Withdrawn }

public sealed class HelpArticle
{
    private HelpArticle() { }
    public Guid Id { get; private set; }
    public Guid BuildingId { get; private set; }
    public Guid Version { get; private set; }
    public Guid DraftRevisionId { get; private set; }
    public Guid? PublishedRevisionId { get; private set; }
    public PublicationState State { get; private set; }

    public static HelpArticle Create(Guid id, Guid buildingId, Guid revisionId)
    {
        if (id == Guid.Empty || buildingId == Guid.Empty) throw new ArgumentException("An article and building are required.");
        var article = new HelpArticle { Id = id, BuildingId = buildingId };
        article.SaveDraft(revisionId); return article;
    }

    public void SaveDraft(Guid revisionId)
    {
        if (revisionId == Guid.Empty) throw new ArgumentException("A revision is required.");
        DraftRevisionId = revisionId; Version = Guid.NewGuid();
        // Editing creates a draft while the previously approved revision stays published.
    }

    public void Publish(Guid reviewedRevisionId)
    {
        if (reviewedRevisionId != DraftRevisionId) throw new InvalidOperationException("Review the latest revision before publishing.");
        PublishedRevisionId = DraftRevisionId; State = PublicationState.Published; Version = Guid.NewGuid();
    }

    public void Withdraw()
    {
        if (State != PublicationState.Published) throw new InvalidOperationException("Only published articles can be withdrawn.");
        State = PublicationState.Withdrawn; Version = Guid.NewGuid();
    }
}

public sealed class HelpRevision
{
    private HelpRevision() { }
    public Guid Id { get; private set; }
    public Guid ArticleId { get; private set; }
    public string Title { get; private set; } = "";
    public string Body { get; private set; } = "";
    public ArticleAudience Audience { get; private set; }
    public string EditorId { get; private set; } = "";
    public DateOnly ReviewOn { get; private set; }
    public DateTimeOffset EditedAt { get; private set; }

    public static HelpRevision Create(Guid articleId, string title, string body, ArticleAudience audience,
        string editorId, DateOnly reviewOn, DateTimeOffset now)
    {
        title = title?.Trim() ?? ""; body = body?.Trim() ?? "";
        if (articleId == Guid.Empty || title.Length is < 1 or > 160 || body.Length is < 1 or > 10000 ||
            !Enum.IsDefined(audience) || string.IsNullOrWhiteSpace(editorId) || editorId.Length > 450 || reviewOn == default)
            throw new ArgumentException("Invalid help revision.");
        return new HelpRevision
        {
            Id = Guid.NewGuid(),
            ArticleId = articleId,
            Title = title,
            Body = body,
            Audience = audience,
            EditorId = editorId,
            ReviewOn = reviewOn,
            EditedAt = now.ToUniversalTime()
        };
    }
}
