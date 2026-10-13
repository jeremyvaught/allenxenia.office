using System.Text.Json;
using AllenBuilding.Application.Access;
using AllenBuilding.Application.Notifications;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Infrastructure.Notifications;

public sealed class NotificationQueue(OfficeDbContext db, IDataProtectionProvider protection, TimeProvider time, Uri publicOrigin) : INotificationQueue
{
    private readonly IDataProtector _messages = protection.CreateProtector("AllenOffice.AccountMessage.v1");

    public async Task<MessageLease?> ClaimAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await using var transaction = await AuthorityLocks.BeginAsync(db, cancellationToken);
        var message = await db.AccountMessages.Where(x => (x.State == MessageState.Queued && x.NextAttemptAt <= now) ||
            (x.State == MessageState.Sending && x.LeaseUntil <= now)).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).FirstOrDefaultAsync(cancellationToken);
        if (message is null) return null;
        await Lock(message.Id, cancellationToken);
        await db.Entry(message).ReloadAsync(cancellationToken);
        if (!Eligible(message, now)) return null;
        if (message.CycleAttempts >= 5)
        {
            message.State = MessageState.NeedsReview; message.LastError = "LeaseExpired"; message.LeaseUntil = null;
            await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
            return null;
        }
        message.State = MessageState.Sending; message.LeaseToken = Guid.NewGuid(); message.LeaseUntil = now.AddMinutes(2);
        message.Attempts = checked(message.Attempts + 1); message.CycleAttempts = checked(message.CycleAttempts + 1);
        await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
        return new(message.Id, message.LeaseToken, message.OriginOperationId, message.OriginTraceId, message.OriginSpanId);
    }

    public async Task<EmailEnvelope?> ComposeAsync(MessageLease lease, CancellationToken cancellationToken)
    {
        var message = await db.AccountMessages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == lease.Id && x.LeaseToken == lease.Token &&
            x.State == MessageState.Sending && x.LeaseUntil > time.GetUtcNow(), cancellationToken);
        if (message is null) return null;
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == message.UserId, cancellationToken);
        if (string.IsNullOrWhiteSpace(user?.Email)) return null;
        var payload = _messages.Unprotect(message.ProtectedPayload);
        if (message.Kind == "SecurityNotice") return user.EmailConfirmed ? new(message.Id, user.Email, "Account security update",
            "A security change was recorded on your Allen Xenia Office account. Sign in to review your account. If you did not request it, contact building management.") : null;
        using var json = JsonDocument.Parse(payload);
        var data = json.RootElement;
        if (message.Kind == "Invitation")
        {
            var invitationId = data.GetProperty("invitationId").GetGuid();
            Guid? tokenVersion = data.TryGetProperty("version", out var encodedVersion) && encodedVersion.ValueKind == JsonValueKind.String ? encodedVersion.GetGuid() : null;
            var valid = await db.Invitations.AnyAsync(x => x.TokenVersion == tokenVersion && x.RevokedAt == null && x.Id == invitationId && x.UserId == user.Id && x.AcceptedAt == null && x.ExpiresAt > time.GetUtcNow(), cancellationToken);
            return valid ? new(message.Id, user.Email, "Your Allen Xenia Office invitation", "Activate your invitation:\n" +
                Link($"Invitation/Accept/{invitationId:D}", data.GetProperty("token").GetString()!)) : null;
        }
        if (message.Kind == "PasswordReset") return user.EmailConfirmed && message.CreatedAt.AddHours(2) > time.GetUtcNow() && data.GetProperty("userId").GetString() == user.Id
            ? new(message.Id, user.Email, "Reset your Allen Xenia Office password", "Reset your password:\n" + Link($"Invitation/Reset/{Uri.EscapeDataString(user.Id)}", data.GetProperty("token").GetString()!)) : null;
        if (message.Kind != "MaintenanceUpdate" || !user.EmailConfirmed) return null;
        var orderId = data.GetProperty("workOrderId").GetGuid(); var membershipId = data.GetProperty("membershipId").GetGuid();
        if (!data.TryGetProperty("historyId", out var historyId)) return null;
        var history = await db.WorkOrderHistory.AsNoTracking().Where(x => x.Id == historyId.GetGuid() && x.WorkOrderId == orderId)
            .Select(x => new { x.Visibility, x.Action }).SingleOrDefaultAsync(cancellationToken);
        if (history is null || history.Action == "Comment" || history.Visibility == Domain.Maintenance.HistoryVisibility.Internal) return null;
        var identity = await db.WorkOrders.AsNoTracking().Where(x => x.Id == orderId && x.RequesterMembershipId == membershipId)
            .Select(x => new { x.SuiteId, x.RequesterUserId }).SingleOrDefaultAsync(cancellationToken);
        if (identity is null) return null;
        var now = time.GetUtcNow();
        var tenant = identity.RequesterUserId == user.Id && await db.Memberships.AnyAsync(x => x.Id == membershipId && x.UserId == user.Id &&
            x.RevokedAt == null && x.StartsAt <= now && (x.EndsAt == null || x.EndsAt > now), cancellationToken) &&
            await db.ActiveGrants.AnyAsync(x => x.MembershipId == membershipId, cancellationToken);
        var manager = await (from suite in db.Suites
                             join grant in db.BuildingManagers on suite.BuildingId equals grant.BuildingId
                             where suite.Id == identity.SuiteId && grant.UserId == user.Id
                             select grant.UserId).AnyAsync(cancellationToken);
        return tenant || (manager && history.Action is "Submitted" or "SubmittedOnBehalf") ? new(message.Id, user.Email, "Maintenance request update",
            "A maintenance request has an update. Sign in to view it:\n" + new Uri(publicOrigin, "Portal")) : null;
    }

    public async Task<bool> CompleteAsync(MessageLease lease, DeliveryResult result, CancellationToken cancellationToken)
    {
        await using var transaction = await AuthorityLocks.BeginAsync(db, cancellationToken);
        await Lock(lease.Id, cancellationToken);
        var message = await db.AccountMessages.SingleOrDefaultAsync(x => x.Id == lease.Id, cancellationToken);
        if (message is null) return false;
        await db.Entry(message).ReloadAsync(cancellationToken);
        if (message.State != MessageState.Sending || message.LeaseToken != lease.Token || message.LeaseUntil is not { } until || until <= time.GetUtcNow()) return false;
        message.LeaseUntil = null;
        message.LastError = result.Outcome switch
        {
            DeliveryOutcome.Delivered => null,
            DeliveryOutcome.Retryable => "DeliveryRetryable",
            DeliveryOutcome.Suppressed => "NoLongerEligible",
            DeliveryOutcome.PermanentFailure => "DeliveryRejected",
            _ => "DeliveryUnknown"
        };
        message.State = result.Outcome switch
        {
            DeliveryOutcome.Delivered => MessageState.Delivered,
            DeliveryOutcome.Suppressed => MessageState.Suppressed,
            DeliveryOutcome.Retryable when message.CycleAttempts < 5 => MessageState.Queued,
            _ => MessageState.NeedsReview
        };
        if (message.State == MessageState.Delivered) message.DeliveredAt = time.GetUtcNow();
        if (message.State == MessageState.Queued) message.NextAttemptAt = time.GetUtcNow().AddSeconds(30 * (1 << Math.Min(message.CycleAttempts - 1, 4)));
        await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RetryAsync(ICurrentActor current, Guid id, Guid version, string reason, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 2000) return false;
        var initial = await current.GetAsync(cancellationToken); if (initial is null) return false;
        await using var transaction = await AuthorityLocks.BeginAsync(db, cancellationToken);
        await AuthorityLocks.UserAsync(db, initial.UserId, cancellationToken);
        await Lock(id, cancellationToken);
        var message = await db.AccountMessages.SingleOrDefaultAsync(x => x.Id == id, cancellationToken); if (message is null) return false;
        if (message.BuildingId is { } building) await AuthorityLocks.ManagerAsync(db, initial.UserId, building, cancellationToken);
        var actor = await current.GetAsync(cancellationToken);
        if (actor is not { RecentMfa: true } || actor.ManagedBuildings.Count == 0 ||
            !(message.UserId == actor.UserId || (message.BuildingId is { } scope && actor.ManagedBuildings.Contains(scope))) ||
            message.State != MessageState.NeedsReview || message.LeaseToken != version) return false;
        message.State = MessageState.Queued; message.CycleAttempts = 0; message.NextAttemptAt = time.GetUtcNow(); message.LeaseToken = Guid.NewGuid();
        db.AccountAudits.Add(new AccountAudit { ActorId = actor.UserId, Action = "NotificationRetryApproved", SubjectId = id.ToString(), Reason = reason.Trim(), OccurredAt = time.GetUtcNow() });
        await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); return true;
    }

    private Task<int> Lock(Guid id, CancellationToken cancellationToken) => db is MySqlOfficeDbContext
        ? db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM account_messages WHERE id = {id.ToString("D")} FOR UPDATE", cancellationToken) : Task.FromResult(0);
    private string Link(string path, string token) => new Uri(publicOrigin, path) + "#token=" + Uri.EscapeDataString(token);
    private static bool Eligible(AccountMessage message, DateTimeOffset now) => (message.State == MessageState.Queued && message.NextAttemptAt <= now) ||
        (message.State == MessageState.Sending && message.LeaseUntil <= now);
}
