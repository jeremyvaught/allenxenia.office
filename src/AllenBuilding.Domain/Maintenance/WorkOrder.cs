namespace AllenBuilding.Domain.Maintenance;

public enum WorkOrderStatus { Submitted, Triaged, Assigned, InProgress, Completed, Closed, Cancelled }
public enum Priority { Low, Normal, High, Urgent }
public enum Trade { General, Plumbing, Electrical, HeatingCooling }

public sealed class WorkOrder
{
    private WorkOrder() { }

    public Guid Id { get; private set; }
    public Guid SuiteId { get; private set; }
    public Guid RequesterMembershipId { get; private set; }
    public string RequesterUserId { get; private set; } = "";
    public string Title { get; private set; } = "";
    public string Description { get; private set; } = "";
    public string Location { get; private set; } = "";
    public WorkOrderStatus Status { get; private set; }
    public Priority? Priority { get; private set; }
    public Trade? Trade { get; private set; }
    public string? AssignedManagerId { get; private set; }
    public Guid? AssignedContractorId { get; private set; }
    public string? Resolution { get; private set; }
    public Guid Version { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static WorkOrder Submit(Guid suiteId, Guid membershipId, string requesterUserId,
        string title, string description, string location, DateTimeOffset now)
    {
        if (suiteId == Guid.Empty || membershipId == Guid.Empty)
            throw new ArgumentException("A suite and original membership are required.");
        return new WorkOrder
        {
            Id = Guid.NewGuid(),
            SuiteId = suiteId,
            RequesterMembershipId = membershipId,
            RequesterUserId = Required(requesterUserId, 450),
            Title = Required(title, 120),
            Description = Required(description, 4000),
            Location = Required(location, 160),
            Status = WorkOrderStatus.Submitted,
            Version = Guid.NewGuid(),
            CreatedAt = now.ToUniversalTime(),
            UpdatedAt = now.ToUniversalTime()
        };
    }

    public void Triage(Priority priority, Trade trade, DateTimeOffset now)
    {
        Require(WorkOrderStatus.Submitted);
        if (!Enum.IsDefined(priority) || !Enum.IsDefined(trade))
            throw new ArgumentException("Choose a supported priority and trade.");
        Priority = priority; Trade = trade;
        Status = WorkOrderStatus.Triaged;
        Changed(now);
    }

    public void Assign(string? managerId, Guid? contractorId, DateTimeOffset now)
    {
        if (Status is not (WorkOrderStatus.Triaged or WorkOrderStatus.Assigned or WorkOrderStatus.InProgress))
            throw new InvalidOperationException("Triage the request before assignment.");
        if ((managerId is null) == (contractorId is null) || contractorId == Guid.Empty)
            throw new ArgumentException("Choose exactly one manager or contractor.");
        var normalizedManager = managerId is null ? null : Required(managerId, 450);
        AssignedManagerId = normalizedManager;
        AssignedContractorId = contractorId;
        if (Status != WorkOrderStatus.InProgress) Status = WorkOrderStatus.Assigned;
        Changed(now);
    }

    public void Start(DateTimeOffset now)
    {
        Require(WorkOrderStatus.Assigned);
        if (AssignedManagerId is null && AssignedContractorId is null)
            throw new InvalidOperationException("An assignment is required.");
        Status = WorkOrderStatus.InProgress;
        Changed(now);
    }

    public void Complete(string resolution, DateTimeOffset now)
    {
        Require(WorkOrderStatus.InProgress);
        var validated = Required(resolution, 2000);
        Resolution = validated; Status = WorkOrderStatus.Completed;
        Changed(now);
    }

    public void Close(DateTimeOffset now)
    {
        Require(WorkOrderStatus.Completed);
        Status = WorkOrderStatus.Closed;
        Changed(now);
    }

    public void Cancel(string explanation, DateTimeOffset now)
    {
        if (Status is WorkOrderStatus.Closed or WorkOrderStatus.Cancelled)
            throw new InvalidOperationException("This request cannot be cancelled.");
        Required(explanation, 2000);
        Status = WorkOrderStatus.Cancelled;
        Changed(now);
    }

    public void Reopen(string explanation, DateTimeOffset now)
    {
        if (Status is not (WorkOrderStatus.Completed or WorkOrderStatus.Closed or WorkOrderStatus.Cancelled))
            throw new InvalidOperationException("Only finished requests can be reopened.");
        Required(explanation, 2000);
        Status = WorkOrderStatus.Submitted;
        Priority = null; Trade = null; AssignedManagerId = null; AssignedContractorId = null;
        Resolution = null;
        Changed(now);
    }

    public void RecordComment(string text, DateTimeOffset now)
    {
        Required(text, 2000);
        Changed(now);
    }

    private void Require(WorkOrderStatus required)
    {
        if (Status != required) throw new InvalidOperationException($"Request must be {required} for this action.");
    }

    private void Changed(DateTimeOffset now)
    {
        Version = Guid.NewGuid(); UpdatedAt = now.ToUniversalTime();
    }

    private static string Required(string value, int maximum)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > maximum)
            throw new ArgumentException($"Enter between 1 and {maximum} characters.");
        return trimmed;
    }
}
