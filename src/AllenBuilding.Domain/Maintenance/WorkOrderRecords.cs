namespace AllenBuilding.Domain.Maintenance;

public enum HistoryVisibility { TenantVisible, Internal }

public sealed class WorkOrderHistory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkOrderId { get; set; }
    public string ActorId { get; set; } = "";
    public string Action { get; set; } = "";
    public WorkOrderStatus Status { get; set; }
    public string? Note { get; set; }
    public HistoryVisibility Visibility { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class MaintenanceReceipt
{
    public string ActorId { get; set; } = "";
    public Guid OperationId { get; set; }
    public string PayloadHash { get; set; } = "";
    public Guid WorkOrderId { get; set; }
    public Guid ResultVersion { get; set; }
    public WorkOrderStatus ResultStatus { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
