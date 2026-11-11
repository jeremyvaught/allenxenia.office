using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Persistence;

namespace AllenBuilding.Infrastructure.Identity;

// Adds an audit row to the caller's unit of work; the caller saves it with the change it records.
public sealed class AuditTrail(OfficeDbContext db, TimeProvider time)
{
    public void Record(string actor, string action, string subject, string? reason = null) => db.AccountAudits.Add(new AccountAudit
    { ActorId = actor, Action = action, SubjectId = subject, Reason = reason, OccurredAt = time.GetUtcNow() });
}
