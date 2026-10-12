using AllenBuilding.Domain.Maintenance;

namespace AllenBuilding.Application.Maintenance;

public enum QueueSort { Oldest, Newest, RecentlyUpdated }
public sealed record MaintenanceQueueQuery(WorkOrderStatus? Status = null, Priority? Priority = null, int? MinAgeDays = null,
    string? Assignee = null, bool IncludeFinished = false, QueueSort Sort = QueueSort.Oldest, int Page = 1)
{
    public const int PageSize = 50;
    public bool IsValid => (Status is null || Enum.IsDefined(Status.Value)) && (Priority is null || Enum.IsDefined(Priority.Value)) &&
        (MinAgeDays is null || MinAgeDays is >= 0 and <= 36500) && Enum.IsDefined(Sort) && Page is >= 1 and <= 1000 && (Assignee is null || Assignee.Length <= 500 &&
        (Assignee is "" or "mine" or "unassigned" || (Assignee.StartsWith("manager:", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(Assignee[8..]) && Assignee[8..].Length <= 450) ||
         (Assignee.StartsWith("contractor:", StringComparison.Ordinal) && Guid.TryParse(Assignee[11..], out var id) && id != Guid.Empty)));
}
public sealed record MaintenanceQueuePage(IReadOnlyList<RequestSummary> Items, MaintenanceQueueQuery Query,
    bool HasNext, DateTimeOffset ObservedAt, IReadOnlyList<AssigneeChoice> Assignees);
