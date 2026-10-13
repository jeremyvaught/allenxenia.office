using AllenBuilding.Application.Access;

namespace AllenBuilding.Application.Assistant;

// Provenance is server-owned and stays outside model input and browser form payloads.
public sealed record AssistantTenantScope(string UserId, Guid MembershipId, Guid Version, string SecurityStamp, string Suite);

public interface IAssistantTenantAccess
{
    Task<AssistantTenantScope?> CaptureAsync(Guid membershipId, CancellationToken cancellationToken);
    // Admission and bounded transport emission share the same locks as access revocation.
    Task<bool> ReleaseAsync(AssistantTenantScope scope, IReadOnlyList<AssistantSource> contextualSources,
        Func<CancellationToken, Task> emit, CancellationToken cancellationToken);
}
