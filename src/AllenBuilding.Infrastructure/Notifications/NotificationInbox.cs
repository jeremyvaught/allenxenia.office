using System.Text.Json;
using AllenBuilding.Application.Access;
using AllenBuilding.Application.Notifications;
using AllenBuilding.Domain.Maintenance;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Infrastructure.Notifications;

public sealed class NotificationInbox(OfficeDbContext db, IDataProtectionProvider protection, TimeProvider time)
{
    private readonly IDataProtector _messages = protection.CreateProtector("AllenOffice.AccountMessage.v1");

    // describe: also say which request each maintenance email was about. A caller that only counts rows can skip it.
    public async Task<NotificationInboxPage> ReadAsync(Actor actor, int reviewPage, CancellationToken cancellationToken = default,
        bool describe = true)
    {
        if (reviewPage is < 1 or > 1000000)
            throw new ArgumentOutOfRangeException(nameof(reviewPage));
        var now = time.GetUtcNow();
        // Reset delivery is account-wide. Only failed-delivery metadata for current tenants is visible to their managers.
        var currentTenants = from grant in db.ActiveGrants
                             join member in db.Memberships on grant.MembershipId equals member.Id
                             join suite in db.Suites on member.SuiteId equals suite.Id
                             where grant.UserId == member.UserId && grant.SuiteId == member.SuiteId &&
                                 actor.ManagedBuildings.Contains(suite.BuildingId) && member.RevokedAt == null &&
                                 member.StartsAt <= now && (member.EndsAt == null || member.EndsAt > now)
                             select member.UserId;
        var visible = db.AccountMessages.AsNoTracking().Where(x => x.UserId == actor.UserId ||
            (x.Kind == "PasswordReset" && x.State == MessageState.NeedsReview && currentTenants.Contains(x.UserId)) ||
            (x.Kind != "PasswordReset" && x.BuildingId != null && actor.ManagedBuildings.Contains(x.BuildingId.Value)));
        IQueryable<NotificationRow> Project(IQueryable<AccountMessage> messages) =>
            from message in messages
            join user in db.Users on message.UserId equals user.Id
            select new NotificationRow(message.Id, message.LeaseToken, message.Kind, message.State,
                message.Attempts, message.LastError, message.CreatedAt, message.Kind == "PasswordReset" ? user.Email : null);
        const int pageSize = 25;
        var exceptions = await Project(visible.Where(x => x.State == MessageState.NeedsReview).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .Skip((reviewPage - 1) * pageSize).Take(pageSize + 1)).ToListAsync(cancellationToken);
        var recent = await Project(visible.Where(x => x.State != MessageState.NeedsReview).OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Take(100)).ToListAsync(cancellationToken);
        var review = exceptions.Take(pageSize).ToArray();
        if (!describe)
            return new(review, recent, reviewPage, exceptions.Count > pageSize);
        var about = await DescribeAsync(actor, review.Concat(recent).Where(x => x.Kind == "MaintenanceUpdate").Select(x => x.Id).ToList(), cancellationToken);
        NotificationRow With(NotificationRow row) => about.TryGetValue(row.Id, out var subject) ? row with { About = subject } : row;
        return new(review.Select(With).ToArray(), recent.Select(With).ToArray(), reviewPage, exceptions.Count > pageSize);
    }

    // The payload names the request and the history entry that caused the email. Only requests in
    // buildings this manager manages are described, and never an internal note.
    private async Task<Dictionary<Guid, NotificationSubject>> DescribeAsync(Actor actor, List<Guid> ids, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, NotificationSubject>();
        if (ids.Count == 0)
            return result;
        var payloads = await db.AccountMessages.AsNoTracking().Where(x => ids.Contains(x.Id))
            .Select(x => new { x.Id, x.UserId, x.ProtectedPayload }).ToListAsync(cancellationToken);
        var references = new List<(Guid Message, string Recipient, Guid Order, Guid? History)>();
        foreach (var payload in payloads)
        {
            try
            {
                using var json = JsonDocument.Parse(_messages.Unprotect(payload.ProtectedPayload));
                Guid? history = json.RootElement.TryGetProperty("historyId", out var entry) ? entry.GetGuid() : null;
                references.Add((payload.Id, payload.UserId, json.RootElement.GetProperty("workOrderId").GetGuid(), history));
            }
            // An unreadable payload only loses its description; the row itself is still listed.
            catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or JsonException or
                KeyNotFoundException or InvalidOperationException or FormatException)
            { }
        }
        var orderIds = references.Select(x => x.Order).Distinct().ToList();
        var orders = await (from order in db.WorkOrders.AsNoTracking()
                            join suite in db.Suites on order.SuiteId equals suite.Id
                            where orderIds.Contains(order.Id) && actor.ManagedBuildings.Contains(suite.BuildingId)
                            select new { order.Id, order.Title, Suite = suite.Name, order.RequesterUserId })
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var historyIds = references.Where(x => x.History is not null).Select(x => x.History!.Value).Distinct().ToList();
        var events = await db.WorkOrderHistory.AsNoTracking()
            .Where(x => historyIds.Contains(x.Id) && x.Visibility == HistoryVisibility.TenantVisible)
            .Select(x => new { x.Id, x.WorkOrderId, x.Action }).ToDictionaryAsync(x => x.Id, cancellationToken);
        foreach (var reference in references)
            if (orders.TryGetValue(reference.Order, out var order))
                result[reference.Message] = new(order.Id, order.Title, order.Suite,
                    reference.History is { } id && events.TryGetValue(id, out var happened) && happened.WorkOrderId == order.Id ? happened.Action : null,
                    reference.Recipient == order.RequesterUserId);
        return result;
    }
}
