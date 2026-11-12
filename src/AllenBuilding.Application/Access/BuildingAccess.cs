using AllenBuilding.Domain.Tenancy;

namespace AllenBuilding.Application.Access;

public sealed record AccessRow(Guid Id, Guid Version, string Suite, string Email, string? Name = null);
public sealed record InvitationRow(Guid Id, Guid Version, string Suite, string Email, DateTimeOffset ExpiresAt, string Status);
public sealed record BuildingAccessOverview(
    IReadOnlyList<Building> Buildings, IReadOnlyList<Suite> Suites, IReadOnlyList<AccessRow> Access, IReadOnlyList<InvitationRow> Invitations);

public sealed record BuildingOption(Guid Id, string Name);
// What a manager should look at first: open work by stage and invitations still waiting.
public sealed record ManagerSummary(int AwaitingTriage, int InProgress, int AwaitingVerification, int PendingInvitations);

public enum SuiteCode { Success, ValidationFailed, Forbidden, Conflict }
public sealed record SuiteResult(SuiteCode Code, string? Name = null);

public interface IBuildingAccessStore
{
    Task<BuildingAccessOverview> ReadOverviewAsync(Actor actor, DateTimeOffset now, CancellationToken cancellationToken);
    Task<IReadOnlyList<BuildingOption>> ReadManagedBuildingsAsync(Actor actor, CancellationToken cancellationToken);
    Task<IReadOnlyList<Membership>> ReadActiveMembershipsAsync(string userId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<ManagerSummary> ReadManagerSummaryAsync(Actor actor, DateTimeOffset now, CancellationToken cancellationToken);
    // Takes the authority locks, re-reads the actor and records the audit in one transaction.
    Task<SuiteResult> AddSuiteAsync(Guid buildingId, string name, CancellationToken cancellationToken);
}

public sealed class BuildingAccessActions(ICurrentActor current, IBuildingAccessStore store, TimeProvider time)
{
    public async Task<BuildingAccessOverview?> OverviewAsync(CancellationToken cancellationToken)
    {
        var actor = await current.GetAsync(cancellationToken);
        return actor is null ? null : await store.ReadOverviewAsync(actor, time.GetUtcNow(), cancellationToken);
    }

    public async Task<IReadOnlyList<BuildingOption>> ManagedBuildingsAsync(CancellationToken cancellationToken)
    {
        var actor = await current.GetAsync(cancellationToken);
        return actor is null ? [] : await store.ReadManagedBuildingsAsync(actor, cancellationToken);
    }

    // Memberships that are in force now and still hold their active grant.
    public Task<IReadOnlyList<Membership>> ActiveMembershipsAsync(Actor actor, CancellationToken cancellationToken) =>
        store.ReadActiveMembershipsAsync(actor.UserId, time.GetUtcNow(), cancellationToken);

    public Task<ManagerSummary> ManagerSummaryAsync(Actor actor, CancellationToken cancellationToken) =>
        store.ReadManagerSummaryAsync(actor, time.GetUtcNow(), cancellationToken);

    public Task<SuiteResult> AddSuiteAsync(Guid buildingId, string name, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(name)
            ? Task.FromResult(new SuiteResult(SuiteCode.ValidationFailed))
            : store.AddSuiteAsync(buildingId, name.Trim(), cancellationToken);
}
