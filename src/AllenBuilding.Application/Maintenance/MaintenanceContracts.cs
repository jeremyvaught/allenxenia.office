using AllenBuilding.Application.Access;
using AllenBuilding.Domain.Maintenance;

namespace AllenBuilding.Application.Maintenance;

public enum MaintenanceAction { Triage, AssignSelf, Start, Complete, Close, Cancel, Reopen, Comment, Assign }
public enum MaintenanceCode { Success, ValidationFailed, AuthenticationRequired, Unavailable, Conflict }
public sealed record SubmitRequest(Guid OperationId, Guid MembershipId, string Title, string Description, string Location);
public sealed record ChangeRequest(Guid OperationId, Guid WorkOrderId, Guid Version, MaintenanceAction Action,
    Priority? Priority = null, Trade? Trade = null, string? Note = null, bool Internal = false, string? ManagerId = null, Guid? ContractorId = null);
public sealed record MaintenanceResult(MaintenanceCode Code, Guid? WorkOrderId = null, Guid? Version = null, WorkOrderStatus? Status = null);
public sealed record RequestSummary(Guid Id, string Suite, string Title, WorkOrderStatus Status, DateTimeOffset CreatedAt);
public sealed record HistoryEntry(string Action, WorkOrderStatus Status, string? Note, bool Internal, DateTimeOffset At);
public sealed record RequestDetails(Guid Id, Guid Version, string Suite, string Title, string Description, string Location,
    WorkOrderStatus Status, Priority? Priority, Trade? Trade, string? Resolution, IReadOnlyList<HistoryEntry> History, string? Assignment = null, IReadOnlyList<AssigneeChoice>? Assignees = null);
public sealed record RequestMemberChoice(Guid Id, string Suite, string? RequesterEmail = null);
public sealed record AssigneeChoice(string Value, string Label);

// Feature persistence boundary. Atomic methods serialize current authorization, version,
// domain mutation, history, durable notifications and replay receipts together.
public interface IMaintenanceStore
{
    Task<IReadOnlyList<RequestSummary>> ListAsync(Actor actor, bool management, CancellationToken cancellationToken);
    Task<RequestDetails?> ReadAsync(Actor actor, Guid id, bool management, CancellationToken cancellationToken);
    Task<IReadOnlyList<RequestMemberChoice>> MembersAsync(Actor actor, bool management, CancellationToken cancellationToken);
    Task<MaintenanceResult> SubmitAsync(Actor actor, SubmitRequest command, string hash, DateTimeOffset now, bool management, CancellationToken cancellationToken);
    Task<MaintenanceResult> ChangeAsync(Actor actor, ChangeRequest command, string hash, DateTimeOffset now,
        bool management, Action<WorkOrder> mutation, CancellationToken cancellationToken);
}
