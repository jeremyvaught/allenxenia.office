namespace AllenBuilding.Application.Access;

public sealed record Actor(string UserId, IReadOnlySet<Guid> ManagedBuildings, bool ChallengedMfa, bool RecentMfa);

// The adapter reads the protected ticket and current database state, never form/tool arguments.
public interface ICurrentActor
{
    Task<Actor?> GetAsync(CancellationToken cancellationToken = default);
}
