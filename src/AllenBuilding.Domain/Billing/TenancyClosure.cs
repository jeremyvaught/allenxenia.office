namespace AllenBuilding.Domain.Billing;

public enum BillingClosureState { Pending, NeedsReview, Verified }

// Ending access is local and immediate. This durable case cannot assert a provider cancellation.
public sealed class TenancyClosure
{
    private TenancyClosure() { }
    public Guid AccountId { get; private set; }
    public Guid OperationId { get; private set; }
    public Guid MembershipId { get; private set; }
    public Guid ReviewedMembershipVersion { get; private set; }
    public Guid ReviewedAccountVersion { get; private set; }
    public string ActorId { get; private set; } = "";
    public string Reason { get; private set; } = "";
    public DateTimeOffset RequestedAt { get; private set; }
    public BillingClosureState State { get; private set; }
    public Guid Version { get; private set; }
    public static bool ValidReason(string? reason) => reason?.Trim() is { Length: >= 1 and <= 2000 } value && !value.Any(x => char.IsControl(x) && x is not ('\n' or '\r' or '\t'));
    public static TenancyClosure Request(Guid operationId, RentAccount account, Guid membershipVersion, string actorId, string reason, DateTimeOffset now)
    {
        reason = reason?.Trim() ?? "";
        if (operationId == Guid.Empty || account is null || account.EndedAt is not null || membershipVersion == Guid.Empty ||
            string.IsNullOrWhiteSpace(actorId) || actorId.Length > 450 || !ValidReason(reason) || now == default)
            throw new ArgumentException("Ending a tenancy requires its exact account/membership versions, actor and reason.");
        return new()
        {
            AccountId = account.Id,
            OperationId = operationId,
            MembershipId = account.MembershipId,
            ReviewedMembershipVersion = membershipVersion,
            ReviewedAccountVersion = account.Version,
            ActorId = actorId,
            Reason = reason,
            RequestedAt = now.ToUniversalTime(),
            State = BillingClosureState.Pending,
            Version = Guid.NewGuid()
        };
    }
}
