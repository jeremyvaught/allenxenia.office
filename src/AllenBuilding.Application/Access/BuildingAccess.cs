using AllenBuilding.Domain.Tenancy;

namespace AllenBuilding.Application.Access;

public sealed record AccessRow(Guid Id, Guid Version, string Suite, string Email);
public sealed record InvitationRow(Guid Id, Guid Version, string Suite, string Email, DateTimeOffset ExpiresAt, string Status);
public sealed record BuildingAccessOverview(
    IReadOnlyList<Building> Buildings, IReadOnlyList<Suite> Suites, IReadOnlyList<AccessRow> Access, IReadOnlyList<InvitationRow> Invitations);

public enum SuiteCode { Success, ValidationFailed, Forbidden, Conflict }
public sealed record SuiteResult(SuiteCode Code, string? Name = null);

public interface IBuildingAccessStore
{
    Task<BuildingAccessOverview> ReadOverviewAsync(Actor actor, DateTimeOffset now, CancellationToken cancellationToken);
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

    public Task<SuiteResult> AddSuiteAsync(Guid buildingId, string name, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(name)
            ? Task.FromResult(new SuiteResult(SuiteCode.ValidationFailed))
            : store.AddSuiteAsync(buildingId, name.Trim(), cancellationToken);
}
