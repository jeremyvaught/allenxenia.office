namespace AllenBuilding.Domain.Maintenance;

public enum AssistantReviewKind { Request, Comment }
public enum AssistantReviewState { Ready, Completed, Cancelled, Revoked }

// One independent assistant turn has one durable review. There is no model execution state.
public sealed class AssistantMaintenanceReview
{
    private AssistantMaintenanceReview() { }
    public Guid Id { get; private set; }
    public Guid ConversationId { get; private set; }
    public string OwnerId { get; private set; } = "";
    public Guid MembershipId { get; private set; }
    public Guid MembershipVersion { get; private set; }
    public string SecurityStamp { get; private set; } = "";
    public AssistantReviewKind Kind { get; private set; }
    public Guid? WorkOrderId { get; private set; }
    public Guid? WorkOrderVersion { get; private set; }
    public string Title { get; private set; } = "";
    public string Description { get; private set; } = "";
    public string Location { get; private set; } = "";
    public string Comment { get; private set; } = "";
    public string SourcesJson { get; private set; } = "[]";
    public Guid OperationId { get; private set; }
    public Guid Version { get; private set; }
    public string PayloadHash { get; private set; } = "";
    public AssistantReviewState State { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public Guid? ResultId { get; private set; }
    public Guid? ResultVersion { get; private set; }
    public WorkOrderStatus? ResultStatus { get; private set; }

    public static AssistantMaintenanceReview Prepare(Guid conversationId, string owner, Guid membership, Guid membershipVersion,
        string stamp, AssistantReviewKind kind, string title, string description, string location, string comment,
        Guid? orderId, Guid? orderVersion, string sourcesJson, DateTimeOffset now)
    {
        if (conversationId == Guid.Empty || string.IsNullOrWhiteSpace(owner) || owner.Length > 450 || membership == Guid.Empty ||
            membershipVersion == Guid.Empty || string.IsNullOrWhiteSpace(stamp) || stamp.Length > 256 || !Enum.IsDefined(kind) || sourcesJson.Length > 4096)
            throw new ArgumentException("An owned original membership and bounded review are required.");
        var review = new AssistantMaintenanceReview
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            OwnerId = owner,
            MembershipId = membership,
            MembershipVersion = membershipVersion,
            SecurityStamp = stamp,
            Kind = kind,
            WorkOrderId = orderId,
            WorkOrderVersion = orderVersion,
            SourcesJson = sourcesJson,
            OperationId = Guid.NewGuid(),
            Version = Guid.NewGuid(),
            CreatedAt = now.ToUniversalTime(),
            ExpiresAt = now.ToUniversalTime().AddMinutes(10)
        };
        review.Fields(title, description, location, comment); return review;
    }

    public void BindHash(string hash)
    {
        if (PayloadHash.Length != 0 || hash.Length != 64 || hash.Any(x => !Uri.IsHexDigit(x))) throw new ArgumentException("A canonical command hash is required.");
        PayloadHash = hash;
    }
    public bool IsReady(DateTimeOffset now) => State == AssistantReviewState.Ready && now < ExpiresAt;
    public void Edit(string title, string description, string location, string comment, DateTimeOffset now)
    {
        if (!IsReady(now)) throw new InvalidOperationException("This review cannot be edited.");
        Fields(title, description, location, comment); Version = Guid.NewGuid(); OperationId = Guid.NewGuid(); PayloadHash = "";
        ExpiresAt = now.ToUniversalTime().AddMinutes(10);
    }
    public void Cancel()
    {
        if (State != AssistantReviewState.Ready) throw new InvalidOperationException("This review cannot be cancelled.");
        State = AssistantReviewState.Cancelled; Version = Guid.NewGuid();
    }
    public void Revoke() { if (State == AssistantReviewState.Ready) { State = AssistantReviewState.Revoked; Version = Guid.NewGuid(); } }
    public void Complete(Guid resultId, Guid resultVersion, WorkOrderStatus status, DateTimeOffset now)
    {
        if (!IsReady(now) || resultId == Guid.Empty || resultVersion == Guid.Empty || !Enum.IsDefined(status)) throw new InvalidOperationException("A valid ready review and committed result are required.");
        State = AssistantReviewState.Completed; ResultId = resultId; ResultVersion = resultVersion; ResultStatus = status; CompletedAt = now.ToUniversalTime();
        // Keep the reviewed version/operation for an exact retry after a lost response.
    }
    private void Fields(string title, string description, string location, string comment)
    {
        if (Kind == AssistantReviewKind.Request)
        {
            if (WorkOrderId is not null || WorkOrderVersion is not null || !string.IsNullOrEmpty(comment)) throw new ArgumentException("A new request cannot target an existing request.");
            var validated = WorkOrder.Submit(Guid.NewGuid(), MembershipId, OwnerId, title, description, location, CreatedAt);
            Title = validated.Title; Description = validated.Description; Location = validated.Location; Comment = "";
        }
        else
        {
            if (WorkOrderId is null || WorkOrderId == Guid.Empty || WorkOrderVersion is null || WorkOrderVersion == Guid.Empty ||
                !string.IsNullOrEmpty(title) || !string.IsNullOrEmpty(description) || !string.IsNullOrEmpty(location) ||
                string.IsNullOrWhiteSpace(comment) || comment.Trim().Length > 2000) throw new ArgumentException("A comment requires an exact request version and text.");
            Comment = comment.Trim(); Title = ""; Description = ""; Location = "";
        }
    }
}
