using System.Text.Json;
using AllenBuilding.Application.Access;
using AllenBuilding.Application.Maintenance;
using AllenBuilding.Domain.Maintenance;
using AllenBuilding.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Infrastructure.Persistence;

public sealed class MaintenanceStore(OfficeDbContext db, ICurrentActor current, AccountInvitations notifications, TimeProvider time) : IMaintenanceStore
{
    private IQueryable<WorkOrder> Scoped(Actor actor, bool management)
    {
        if (management)
            return from order in db.WorkOrders
                   join suite in db.Suites on order.SuiteId equals suite.Id
                   join manager in db.BuildingManagers on suite.BuildingId equals manager.BuildingId
                   where manager.UserId == actor.UserId && actor.ManagedBuildings.Contains(suite.BuildingId)
                   select order;
        var now = time.GetUtcNow();
        return from order in db.WorkOrders
               join membership in db.Memberships on order.RequesterMembershipId equals membership.Id
               join grant in db.ActiveGrants on membership.Id equals grant.MembershipId
               where order.RequesterUserId == actor.UserId && membership.UserId == actor.UserId &&
                   membership.RevokedAt == null && membership.StartsAt <= now && (membership.EndsAt == null || membership.EndsAt > now)
               select order;
    }

    public async Task<IReadOnlyList<RequestSummary>> ListAsync(Actor actor, bool management, CancellationToken cancellationToken)
        => await (from order in Scoped(actor, management).AsNoTracking()
                  join suite in db.Suites on order.SuiteId equals suite.Id
                  orderby order.CreatedAt descending, order.Id descending
                  select new RequestSummary(order.Id, suite.Name, order.Title, order.Status, order.CreatedAt))
            .Take(100).ToListAsync(cancellationToken);

    public async Task<RequestDetails?> ReadAsync(Actor actor, Guid id, bool management, CancellationToken cancellationToken)
    {
        var order = await Scoped(actor, management).AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (order is null) return null;
        var suite = await db.Suites.AsNoTracking().Where(x => x.Id == order.SuiteId).Select(x => x.Name).SingleAsync(cancellationToken);
        var history = await db.WorkOrderHistory.AsNoTracking().Where(x => x.WorkOrderId == order.Id &&
                (management || x.Visibility == HistoryVisibility.TenantVisible))
            .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id).Take(100)
            .Select(x => new HistoryEntry(x.Action, x.Status, x.Note, x.Visibility == HistoryVisibility.Internal, x.OccurredAt))
            .ToListAsync(cancellationToken);
        return new(order.Id, order.Version, suite, order.Title, order.Description, order.Location, order.Status,
            order.Priority, order.Trade, order.Resolution, history);
    }

    public async Task<MaintenanceResult> SubmitAsync(Actor actor, SubmitRequest command, string hash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await AuthorityLocks.BeginAsync(db, cancellationToken);
        await AuthorityLocks.UserAsync(db, actor.UserId, cancellationToken);
        await AuthorityLocks.MembershipAsync(db, command.MembershipId, cancellationToken);
        var fresh = await current.GetAsync(cancellationToken);
        if (!MaintenanceActions.CanUse(fresh, false)) return new(MaintenanceCode.Unavailable);
        var membership = await db.Memberships.AsNoTracking().SingleOrDefaultAsync(x => x.Id == command.MembershipId && x.UserId == actor.UserId, cancellationToken);
        if (membership is null || !membership.IsActive(time.GetUtcNow()) || !await db.ActiveGrants.AnyAsync(x => x.MembershipId == membership.Id, cancellationToken))
            return new(MaintenanceCode.Unavailable);
        var replay = await Replay(actor.UserId, command.OperationId, hash, cancellationToken);
        if (replay is not null) return replay;
        var order = WorkOrder.Submit(membership.SuiteId, membership.Id, actor.UserId, command.Title, command.Description, command.Location, now);
        db.WorkOrders.Add(order);
        await Record(actor.UserId, command.OperationId, hash, order, "Submitted", null, false, now, cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
            return Result(order);
        }
        catch (DbUpdateException exception) when (ReceiptCollision(exception)) { return new(MaintenanceCode.Conflict); }
    }

    public async Task<MaintenanceResult> ChangeAsync(Actor actor, ChangeRequest command, string hash, DateTimeOffset now,
        bool management, Action<WorkOrder> mutation, CancellationToken cancellationToken)
    {
        await using var transaction = await AuthorityLocks.BeginAsync(db, cancellationToken);
        await AuthorityLocks.UserAsync(db, actor.UserId, cancellationToken);
        // Find only identifiers needed for locking; no private text is read before the scope check.
        var identity = await db.WorkOrders.AsNoTracking().Where(x => x.Id == command.WorkOrderId)
            .Select(x => new { x.SuiteId, x.RequesterMembershipId }).SingleOrDefaultAsync(cancellationToken);
        if (identity is null) return new(MaintenanceCode.Unavailable);
        if (management)
        {
            var building = await db.Suites.Where(x => x.Id == identity.SuiteId).Select(x => x.BuildingId).SingleAsync(cancellationToken);
            await AuthorityLocks.ManagerAsync(db, actor.UserId, building, cancellationToken);
        }
        else await AuthorityLocks.MembershipAsync(db, identity.RequesterMembershipId, cancellationToken);
        await AuthorityLocks.WorkOrderAsync(db, command.WorkOrderId, cancellationToken);
        var fresh = await current.GetAsync(cancellationToken);
        if (!MaintenanceActions.CanUse(fresh, management)) return new(MaintenanceCode.Unavailable);
        var order = await Scoped(fresh!, management).SingleOrDefaultAsync(x => x.Id == command.WorkOrderId, cancellationToken);
        if (order is null) return new(MaintenanceCode.Unavailable);
        var replay = await Replay(actor.UserId, command.OperationId, hash, cancellationToken);
        if (replay is not null) return replay;
        if (order.Version != command.Version) return new(MaintenanceCode.Conflict);
        try { mutation(order); }
        catch (ArgumentException) { return new(MaintenanceCode.ValidationFailed); }
        catch (InvalidOperationException) { return new(MaintenanceCode.Conflict); }
        await Record(actor.UserId, command.OperationId, hash, order, command.Action.ToString(), command.Note?.Trim(), command.Internal, now, cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
            return Result(order);
        }
        catch (DbUpdateConcurrencyException) { return new(MaintenanceCode.Conflict); }
        catch (DbUpdateException exception) when (ReceiptCollision(exception)) { return new(MaintenanceCode.Conflict); }
    }

    private async Task<MaintenanceResult?> Replay(string actor, Guid operation, string hash, CancellationToken cancellationToken)
    {
        var receipt = await db.MaintenanceReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.ActorId == actor && x.OperationId == operation, cancellationToken);
        return receipt is null ? null : receipt.PayloadHash == hash
            ? new(MaintenanceCode.Success, receipt.WorkOrderId, receipt.ResultVersion, receipt.ResultStatus) : new(MaintenanceCode.Conflict);
    }

    private async Task Record(string actor, Guid operation, string hash, WorkOrder order, string action, string? note, bool internalNote,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var entry = new WorkOrderHistory
        {
            WorkOrderId = order.Id,
            ActorId = actor,
            Action = action,
            Note = note,
            Visibility = internalNote ? HistoryVisibility.Internal : HistoryVisibility.TenantVisible,
            Status = order.Status,
            OccurredAt = now
        };
        db.WorkOrderHistory.Add(entry);
        db.MaintenanceReceipts.Add(new MaintenanceReceipt
        {
            ActorId = actor,
            OperationId = operation,
            PayloadHash = hash,
            WorkOrderId = order.Id,
            ResultVersion = order.Version,
            ResultStatus = order.Status,
            OccurredAt = now
        });
        var building = await db.Suites.Where(x => x.Id == order.SuiteId).Select(x => x.BuildingId).SingleAsync(cancellationToken);
        var recipients = (await db.BuildingManagers.Where(x => x.BuildingId == building).Select(x => x.UserId).ToListAsync(cancellationToken)).ToHashSet();
        if (!internalNote && await db.Memberships.AnyAsync(x => x.Id == order.RequesterMembershipId && x.RevokedAt == null &&
            x.StartsAt <= now && (x.EndsAt == null || x.EndsAt > now), cancellationToken) &&
            await db.ActiveGrants.AnyAsync(x => x.MembershipId == order.RequesterMembershipId, cancellationToken)) recipients.Add(order.RequesterUserId);
        foreach (var recipient in recipients)
            notifications.Queue(recipient, "MaintenanceUpdate", JsonSerializer.Serialize(new { workOrderId = order.Id, historyId = entry.Id, membershipId = order.RequesterMembershipId }), building);
    }
    private static MaintenanceResult Result(WorkOrder order) => new(MaintenanceCode.Success, order.Id, order.Version, order.Status);
    private static bool ReceiptCollision(DbUpdateException exception) => exception.InnerException is
        MySql.Data.MySqlClient.MySqlException { Number: 1062 } or Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 };
}
