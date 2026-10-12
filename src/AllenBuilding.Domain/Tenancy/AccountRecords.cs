namespace AllenBuilding.Domain.Tenancy;

public sealed class AccountAudit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ActorId { get; set; } = "";
    public string Action { get; set; } = "";
    public string SubjectId { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class AccountMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string ProtectedPayload { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
