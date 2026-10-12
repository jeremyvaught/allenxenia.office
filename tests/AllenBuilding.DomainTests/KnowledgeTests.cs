using AllenBuilding.Domain.Knowledge;

namespace AllenBuilding.DomainTests;

public sealed class KnowledgeTests
{
    [Fact]
    public void Unreviewed_edits_cannot_replace_the_approved_revision()
    {
        var approved = Guid.NewGuid(); var draft = Guid.NewGuid();
        var article = HelpArticle.Create(Guid.NewGuid(), Guid.NewGuid(), approved);
        article.Publish(approved); article.SaveDraft(draft);
        Assert.Equal(approved, article.PublishedRevisionId);
        Assert.Equal(PublicationState.Published, article.State);
        var version = article.Version;
        Assert.Throws<InvalidOperationException>(() => article.Publish(approved));
        Assert.Equal(version, article.Version);
        article.Publish(draft); Assert.Equal(draft, article.PublishedRevisionId);
        article.Withdraw(); Assert.Equal(PublicationState.Withdrawn, article.State);
        Assert.Equal(draft, article.PublishedRevisionId);
    }

    [Fact]
    public void Revision_validation_preserves_exact_plain_text_and_requires_audience_editor_and_review_date()
    {
        var id = Guid.NewGuid(); var review = new DateOnly(2027, 1, 1); var now = DateTimeOffset.UtcNow;
        var revision = HelpRevision.Create(id, " Access ", " <script>untrusted</script> ", ArticleAudience.ActiveTenant, "editor", review, now);
        Assert.Equal("Access", revision.Title); Assert.Equal("<script>untrusted</script>", revision.Body);
        Assert.Throws<ArgumentException>(() => HelpRevision.Create(id, "Title", "Body", (ArticleAudience)99, "editor", review, now));
        Assert.Throws<ArgumentException>(() => HelpRevision.Create(id, "Title", "Body", ArticleAudience.Public, "", review, now));
        Assert.Throws<ArgumentException>(() => HelpRevision.Create(id, "Title", "Body", ArticleAudience.Public, "editor", default, now));
    }
}
