using System.ComponentModel.DataAnnotations;
using AllenBuilding.Application.Maintenance;
using AllenBuilding.Domain.Maintenance;

namespace AllenBuilding.Web.Models;

public sealed class ContractorForm
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BuildingId { get; set; }
    public Guid? Version { get; set; }
    [Required, StringLength(160)] public string Name { get; set; } = "";
    [EnumDataType(typeof(Trade))] public Trade Trade { get; set; }
    [EmailAddress, StringLength(254)] public string? Email { get; set; }
    [StringLength(80)] public string? Phone { get; set; }
    public bool Active { get; set; } = true;
    public SaveContractor Command() => new(Id, BuildingId, Version, Name, Trade, Email ?? "", Phone ?? "", Active);
}
