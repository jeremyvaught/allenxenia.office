using AllenBuilding.Domain.Maintenance;

namespace AllenBuilding.DomainTests;

public sealed class AssistantReviewTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    private static AssistantMaintenanceReview Review() => AssistantMaintenanceReview.Prepare(Guid.NewGuid(), "owner", Guid.NewGuid(), Guid.NewGuid(), "stamp",
        AssistantReviewKind.Request, " Dripping sink ", " Water underneath ", " Kitchen ", "", null, null, "[]", Now);
    [Fact]
    public void Editing_changes_the_operation_and_review_version_and_cannot_reuse_an_old_confirmation()
    {
        var review = Review(); review.BindHash(new('A', 64)); var version = review.Version; var operation = review.OperationId;
        review.Edit("Revised issue", "Revised details", "Hall", "", Now.AddMinutes(1));
        Assert.NotEqual(version, review.Version); Assert.NotEqual(operation, review.OperationId); Assert.Empty(review.PayloadHash);
        review.BindHash(new('B', 64)); Assert.Equal("Revised issue", review.Title); Assert.Equal(Now.AddMinutes(11), review.ExpiresAt);
    }
    [Fact]
    public void Completion_keeps_exact_replay_identity_and_is_terminal()
    {
        var review = Review(); var version = review.Version; var operation = review.OperationId;
        review.Complete(Guid.NewGuid(), Guid.NewGuid(), WorkOrderStatus.Submitted, Now.AddMinutes(1));
        Assert.Equal(AssistantReviewState.Completed, review.State); Assert.Equal(version, review.Version); Assert.Equal(operation, review.OperationId);
        Assert.Throws<InvalidOperationException>(() => review.Cancel());
        Assert.Throws<InvalidOperationException>(() => review.Edit("Changed", "Details", "Hall", "", Now));
        review.Revoke(); Assert.Equal(AssistantReviewState.Completed, review.State);
    }
    [Fact]
    public void Cancelled_revoked_and_expired_reviews_never_execute()
    {
        foreach (var state in new[] { "cancel", "revoke", "expire" })
        {
            var review = Review(); if (state == "cancel") review.Cancel(); if (state == "revoke") review.Revoke();
            var at = state == "expire" ? Now.AddMinutes(10) : Now;
            Assert.False(review.IsReady(at)); Assert.Throws<InvalidOperationException>(() => review.Complete(Guid.NewGuid(), Guid.NewGuid(), WorkOrderStatus.Submitted, at));
        }
    }
    [Fact]
    public void Drafts_cannot_mix_request_fields_with_comment_targets_or_omit_required_fields()
    {
        Assert.Throws<ArgumentException>(() => AssistantMaintenanceReview.Prepare(Guid.NewGuid(), "owner", Guid.NewGuid(), Guid.NewGuid(), "stamp",
            AssistantReviewKind.Comment, "", "", "", "comment", null, null, "[]", Now));
        Assert.Throws<ArgumentException>(() => AssistantMaintenanceReview.Prepare(Guid.NewGuid(), "owner", Guid.NewGuid(), Guid.NewGuid(), "stamp",
            AssistantReviewKind.Request, "title", "", "Hall", "", null, null, "[]", Now));
        Assert.Throws<ArgumentException>(() => Review().BindHash("unverified"));
        Assert.Equal("Dripping sink", Review().Title);
    }
}
