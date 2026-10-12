namespace AllenBuilding.Domain.Tenancy;

public sealed class Building
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string TimeZoneId { get; set; } = "America/New_York";
}

public sealed class Suite
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BuildingId { get; set; }
    public string Name { get; set; } = "";
}
