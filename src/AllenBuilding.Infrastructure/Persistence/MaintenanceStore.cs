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
        string? assignment = null;
        if (order.AssignedContractorId is { } contactId)
            assignment = await db.Contractors.Where(x => x.Id == contactId).Select(x => x.Name).SingleAsync(cancellationToken);
        if (order.AssignedManagerId is { } managerId)
            assignment = await db.Users.Where(x => x.Id == managerId).Select(x => x.DisplayName == "" ? "Building manager" : x.DisplayName).SingleAsync(cancellationToken);
        IReadOnlyList<AssigneeChoice>? choices = null;
        if (management)
        {
            var building = await db.Suites.Where(x => x.Id == order.SuiteId).Select(x => x.BuildingId).SingleAsync(cancellationToken);
            var managers = await (from grant in db.BuildingManagers
                                  join user in db.Users on grant.UserId equals user.Id
                                  where grant.BuildingId == building
                                  orderby user.DisplayName, user.Id
                                  select new AssigneeChoice("manager:" + user.Id, user.DisplayName == "" ? user.UserName! : user.DisplayName)).Take(100).ToListAsync(cancellationToken);
            var contractors = await db.Contractors.Where(x => x.BuildingId == building && x.Active).OrderBy(x => x.Name).ThenBy(x => x.Id)
                .Take(100).ToListAsync(cancellationToken);
            choices = managers.Concat(contractors.Select(x => new AssigneeChoice("contractor:" + x.Id.ToString("D"), x.Name + " (contractor)"))).ToList();
        }
        return new(order.Id, order.Version, suite, order.Title, order.Description, order.Location, order.Status,
            order.Priority, order.Trade, order.Resolution, history, assignment, choices);
    }

    public async Task<IReadOnlyList<RequestMemberChoice>> MembersAsync(Actor actor, bool management, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        return await (from membership in db.Memberships.AsNoTracking()
                      join grant in db.ActiveGrants on membership.Id equals grant.MembershipId
                      join suite in db.Suites on membership.SuiteId equals suite.Id
                      join user in db.Users on membership.UserId equals user.Id
                      where membership.RevokedAt == null && membership.StartsAt <= now && (membership.EndsAt == null || membership.EndsAt > now) &&
                        (management ? actor.ManagedBuildings.Contains(suite.BuildingId) && db.BuildingManagers.Any(x => x.BuildingId == suite.BuildingId && x.UserId == actor.UserId) : membership.UserId == actor.UserId)
                      orderby suite.Name, membership.Id
                      select new RequestMemberChoice(membership.Id, suite.Name, management ? user.Email : null)).Take(100).ToListAsync(cancellationToken);
    }

    public async Task<MaintenanceResult> SubmitAsync(Actor actor, SubmitRequest command, string hash, DateTimeOffset now, bool management, CancellationToken cancellationToken)
    {
        await using var transaction = await AuthorityLocks.BeginAsync(db, cancellationToken);
        await AuthorityLocks.UserAsync(db, actor.UserId, cancellationToken);
        Guid? building = null;
        if (management)
        {
            building = await (from targetMembership in db.Memberships
                              join suite in db.Suites on targetMembership.SuiteId equals suite.Id
                              where targetMembership.Id == command.MembershipId
                              select (Guid?)suite.BuildingId).SingleOrDefaultAsync(cancellationToken);
            if (building is null) return new(MaintenanceCode.Unavailable);
            await AuthorityLocks.ManagerAsync(db, actor.UserId, building.Value, cancellationToken);
        }
        await AuthorityLocks.MembershipAsync(db, command.MembershipId, cancellationToken);
        var fresh = await current.GetAsync(cancellationToken);
        if (!MaintenanceActions.CanUse(fresh, management) || (management && (!fresh!.ManagedBuildings.Contains(building!.Value) ||
            !await db.BuildingManagers.AnyAsync(x => x.BuildingId == building && x.UserId == fresh.UserId, cancellationToken)))) return new(MaintenanceCode.Unavailable);
        var membership = await db.Memberships.AsNoTracking().SingleOrDefaultAsync(x => x.Id == command.MembershipId && (management || x.UserId == actor.UserId), cancellationToken);
        if (membership is null) return new(MaintenanceCode.Unavailable);
        if (management)
        {
            var replay = await Replay(actor.UserId, command.OperationId, hash, cancellationToken);
            if (replay is not null) return replay;
        }
        if (!membership.IsActive(time.GetUtcNow()) || !await db.ActiveGrants.AnyAsync(x => x.MembershipId == membership.Id, cancellationToken)) return new(MaintenanceCode.Unavailable);
        if (!management)
        {
            var replay = await Replay(actor.UserId, command.OperationId, hash, cancellationToken);
            if (replay is not null) return replay;
        }
        var order = WorkOrder.Submit(membership.SuiteId, membership.Id, membership.UserId, command.Title, command.Description, command.Location, now);
        db.WorkOrders.Add(order);
        await Record(actor.UserId, command.OperationId, hash, order, management ? "SubmittedOnBehalf" : "Submitted", null, false, now, cancellationToken);
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
            if (command.Action == MaintenanceAction.Assign)
                await AuthorityLocks.AssignmentManagersAsync(db, actor.UserId, command.ManagerId, building, cancellationToken);
            else await AuthorityLocks.ManagerAsync(db, actor.UserId, building, cancellationToken);
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
        string? assignmentNote = null;
        if (command.Action is MaintenanceAction.Assign or MaintenanceAction.AssignSelf)
        {
            var building = await db.Suites.Where(x => x.Id == order.SuiteId).Select(x => x.BuildingId).SingleAsync(cancellationToken);
            if (command.Action == MaintenanceAction.Assign && command.ContractorId is { } contractorId)
            {
                await AuthorityLocks.ContractorAsync(db, contractorId, cancellationToken);
                var contact = await db.Contractors.AsNoTracking().SingleOrDefaultAsync(x => x.Id == contractorId && x.BuildingId == building && x.Active, cancellationToken);
                if (contact is null) return new(MaintenanceCode.Unavailable);
                assignmentNote = "Assigned to " + contact.Name + " (contractor).";
            }
            else
            {
                var target = command.Action == MaintenanceAction.AssignSelf ? actor.UserId : command.ManagerId;
                var name = await (from grant in db.BuildingManagers
                                  join user in db.Users on grant.UserId equals user.Id
                                  where grant.BuildingId == building && grant.UserId == target
                                  select user.DisplayName == "" ? "Building manager" : user.DisplayName).SingleOrDefaultAsync(cancellationToken);
                if (name is null) return new(MaintenanceCode.Unavailable);
                assignmentNote = "Assigned to " + name + ".";
            }
        }
        try { mutation(order); }
        catch (ArgumentException) { return new(MaintenanceCode.ValidationFailed); }
        catch (InvalidOperationException) { return new(MaintenanceCode.Conflict); }
        await Record(actor.UserId, command.OperationId, hash, order, command.Action.ToString(), assignmentNote ?? command.Note?.Trim(), command.Internal, now, cancellationToken);
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
