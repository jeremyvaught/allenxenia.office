using System.ComponentModel.DataAnnotations;
using AllenBuilding.Application.Maintenance;
using AllenBuilding.Domain.Maintenance;

namespace AllenBuilding.Web.Models;

public sealed class NewRequestForm
{
    public Guid OperationId { get; set; } = Guid.NewGuid();
    public Guid MembershipId { get; set; }
    [Required, StringLength(120)] public string Title { get; set; } = "";
    [Required, StringLength(4000)] public string Description { get; set; } = "";
    [Required, StringLength(160)] public string Location { get; set; } = "";
}

public sealed class ChangeRequestForm
{
    public Guid OperationId { get; set; }
    public Guid WorkOrderId { get; set; }
    public Guid Version { get; set; }
    [EnumDataType(typeof(MaintenanceAction))] public MaintenanceAction Action { get; set; }
    [EnumDataType(typeof(Priority))] public Priority? Priority { get; set; }
    [EnumDataType(typeof(Trade))] public Trade? Trade { get; set; }
    [StringLength(2000)] public string? Note { get; set; }
    public bool Internal { get; set; }
    [StringLength(500)] public string? Assignee { get; set; }
    public ChangeRequest Command()
    {
        var manager = Assignee?.StartsWith("manager:", StringComparison.Ordinal) == true ? Assignee[8..] : null;
        Guid? contractor = Assignee?.StartsWith("contractor:", StringComparison.Ordinal) == true && Guid.TryParse(Assignee[11..], out var id) ? id : null;
        return new(OperationId, WorkOrderId, Version, Action, Priority, Trade, Note, Internal, manager, contractor);
    }
}

public sealed record MembershipChoice(Guid Id, string Suite);
public sealed record AccessRow(Guid Id, Guid Version, string Suite, string Email);
public sealed class RevokeAccessForm
{
    public Guid MembershipId { get; set; }
    public Guid Version { get; set; }
    [Required, StringLength(2000)] public string Reason { get; set; } = "";
}
