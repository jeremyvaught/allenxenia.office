namespace AllenBuilding.Domain.Tenancy;

public sealed class Membership
{
    private Membership() { }
    public Guid Id { get; private set; }
    public Guid SuiteId { get; private set; }
    public string UserId { get; private set; } = "";
    public DateTimeOffset StartsAt { get; private set; }
    public DateTimeOffset? EndsAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public string? RevokedBy { get; private set; }
    public string? RevocationReason { get; private set; }
    public Guid Version { get; private set; }

    public static Membership Grant(Guid suiteId, string userId, DateTimeOffset startsAt, DateTimeOffset? endsAt = null)
    {
        if (suiteId == Guid.Empty || string.IsNullOrWhiteSpace(userId) || userId.Length > 450)
            throw new ArgumentException("A suite and user are required.");
        if (endsAt <= startsAt) throw new ArgumentException("End must follow the start.");
        return new Membership
        {
            Id = Guid.NewGuid(),
            SuiteId = suiteId,
            UserId = userId,
            StartsAt = startsAt.ToUniversalTime(),
            EndsAt = endsAt?.ToUniversalTime(),
            Version = Guid.NewGuid()
        };
    }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && now >= StartsAt && (EndsAt is null || now < EndsAt);

    public void Revoke(string actorId, string reason, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(actorId) || string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 2000)
            throw new ArgumentException("Actor and a reason of at most 2000 characters are required.");
        if (RevokedAt is not null) return;
        RevokedAt = now.ToUniversalTime(); RevokedBy = actorId; RevocationReason = reason.Trim();
        Version = Guid.NewGuid();
    }
}
