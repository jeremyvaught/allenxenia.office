namespace AllenBuilding.Domain.Tenancy;

public sealed class BuildingManager
{
    public Guid BuildingId { get; set; }
    public string UserId { get; set; } = "";
}

public sealed class Invitation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SuiteId { get; set; }
    public string UserId { get; set; } = "";
    public string IssuedBy { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
}
