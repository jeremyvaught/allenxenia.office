using AllenBuilding.Application.Assistant;
using AllenBuilding.Web.Hosting;

namespace AllenBuilding.IntegrationTests;

public sealed class AssistantConversationTests
{
    [Fact]
    public void Guest_transfer_serializes_claims_and_uncertain_preparation_cannot_be_retargeted_or_edited()
    {
        var clock = new TestClock(); using var store = new AssistantConversations(clock);
        using var turn = store.Begin(Guid.Empty, 0, "browser:guest", null, null)!;
        Assert.True(turn.Complete("Question", new(AssistantModelCode.Success, "Draft ready", GuestDraft: new(AllenBuilding.Domain.Maintenance.AssistantReviewKind.Request, "Sink", "Dripping", "Kitchen"))));
        Assert.True(turn.Closed); Assert.Empty(store.ReadContext(turn.Id, "browser:guest")!.History);
        Assert.Null(store.ReadGuest(turn.Id, "another-browser")); var draft = store.ReadGuest(turn.Id, "browser")!;
        var scope = new AssistantTenantScope("user", Guid.NewGuid(), Guid.NewGuid(), "stamp", "Suite");
        Assert.Null(store.BeginTransfer(turn.Id, Guid.NewGuid(), "browser", scope));
        using (var first = store.BeginTransfer(turn.Id, draft.Version, "browser", scope))
        {
            Assert.NotNull(first); Assert.Null(store.BeginTransfer(turn.Id, draft.Version, "browser", scope));
            Assert.False(store.EditGuest(turn.Id, draft.Version, "browser", draft.Draft, true));
        }
        Assert.Null(store.BeginTransfer(turn.Id, draft.Version, "browser", scope with { UserId = "other" }));
        Assert.Null(store.BeginTransfer(turn.Id, draft.Version, "browser", scope with { MembershipId = Guid.NewGuid() }));
        Assert.Null(store.BeginTransfer(turn.Id, draft.Version, "browser", scope with { Version = Guid.NewGuid() }));
        Assert.False(store.EditGuest(turn.Id, draft.Version, "browser", draft.Draft, false));
        using var retry = store.BeginTransfer(turn.Id, draft.Version, "browser", scope); Assert.NotNull(retry);
        var review = Guid.NewGuid(); Assert.True(retry.Complete(review)); Assert.Equal(review, store.ReadGuest(turn.Id, "browser")!.ReviewId);
        clock.Offset = TimeSpan.FromMinutes(11); Assert.Null(store.ReadGuest(turn.Id, "browser"));
    }
    [Fact]
    public void Leases_serialize_turns_and_reject_wrong_owners_sequences_scopes_and_targets()
    {
        using var store = new AssistantConversations(new TestClock()); var member = Guid.NewGuid(); var target = Guid.NewGuid();
        using var first = store.Begin(Guid.Empty, 0, "owner", member, target); Assert.NotNull(first);
        Assert.Null(store.Begin(first.Id, 0 + 1, "owner", member, target));
        Assert.True(first.Complete("Leaking sink", new(AssistantModelCode.Success, "Where is it?")));
        Assert.Null(store.Begin(first.Id, 0, "owner", member, target));
        Assert.Null(store.Begin(first.Id, 1, "other", member, target));
        Assert.Null(store.Begin(first.Id, 1, "owner", Guid.NewGuid(), target));
        Assert.Null(store.Begin(first.Id, 1, "owner", member, null));
        Assert.Null(store.Begin(first.Id, 2, "owner", member, target));
        using var next = store.Begin(first.Id, 1, "owner", member, target); Assert.NotNull(next);
        Assert.Equal("Leaking sink", Assert.Single(next.Previous!.History).Question);
        Assert.Null(store.Begin(first.Id, 1, "owner", member, target));
    }
    [Fact]
    public void Failed_turns_release_the_lease_without_advancing_history_and_forgetting_prevents_resume()
    {
        using var store = new AssistantConversations(new TestClock()); Guid id;
        using (var first = store.Begin(Guid.Empty, 0, "owner", null, null)!)
        { id = first.Id; Assert.True(first.Complete("Question", new(AssistantModelCode.Success, "Answer"))); }
        using (var failed = store.Begin(id, 1, "owner", null, null)) Assert.NotNull(failed);
        using var retry = store.Begin(id, 1, "owner", null, null); Assert.NotNull(retry); Assert.Single(retry.Previous!.History);
        retry.Forget(); Assert.Null(store.Begin(id, 1, "owner", null, null));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Idle_and_absolute_expiry_discard_context(bool absolute)
    {
        var clock = new TestClock(); using var store = new AssistantConversations(clock);
        using var first = store.Begin(Guid.Empty, 0, "owner", null, null)!; first.Complete("Question", new(AssistantModelCode.Success, "Answer"));
        if (absolute)
        {
            for (var turn = 1; turn <= 4; turn++)
            {
                clock.Offset = TimeSpan.FromMinutes(turn * 25);
                using var next = store.Begin(first.Id, turn, "owner", null, null)!; Assert.NotNull(next); next.Complete("More", new(AssistantModelCode.Success, "Answer"));
            }
            clock.Offset = TimeSpan.FromMinutes(121);
        }
        else clock.Offset = TimeSpan.FromMinutes(31);
        Assert.Null(store.Begin(first.Id, absolute ? 5 : 1, "owner", null, null));
    }
    [Fact]
    public void Turn_limits_and_bounded_partition_capacity_prevent_unbounded_transcripts()
    {
        using var store = new AssistantConversations(new TestClock()); Guid id = Guid.Empty;
        for (var turn = 0; turn < AssistantConversations.MaxTurns; turn++)
        {
            using var lease = store.Begin(id, turn, "owner", null, null)!; Assert.NotNull(lease); id = lease.Id;
            Assert.True(lease.Complete("Question", new(AssistantModelCode.Success, "Answer")));
        }
        Assert.Null(store.Begin(id, AssistantConversations.MaxTurns, "owner", null, null));
        for (var index = 1; index < AssistantConversations.Capacity; index++)
        {
            using var lease = store.Begin(Guid.Empty, 0, "owner" + index, null, null)!; Assert.NotNull(lease);
            lease.Complete("Question", new(AssistantModelCode.Success, "Answer"));
        }
        Assert.Null(store.Begin(Guid.Empty, 0, "overflow", null, null));
    }
}
