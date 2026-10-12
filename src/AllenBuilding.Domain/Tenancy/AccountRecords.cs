namespace AllenBuilding.Domain.Tenancy;

public sealed class AccountAudit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ActorId { get; set; } = "";
    public string Action { get; set; } = "";
    public string SubjectId { get; set; } = "";
    public string? Reason { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class AccountMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = "";
    public Guid? BuildingId { get; set; }
    public string Kind { get; set; } = "";
    public string ProtectedPayload { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public MessageState State { get; set; }
    public int Attempts { get; set; }
    public int CycleAttempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public Guid LeaseToken { get; set; } = Guid.NewGuid();
    public DateTimeOffset? LeaseUntil { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public string? LastError { get; set; }
}

public enum MessageState { Queued, Sending, Delivered, Suppressed, NeedsReview }
