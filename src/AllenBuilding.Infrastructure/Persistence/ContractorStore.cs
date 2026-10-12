using AllenBuilding.Application.Access;
using AllenBuilding.Application.Maintenance;
using AllenBuilding.Domain.Maintenance;
using AllenBuilding.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Infrastructure.Persistence;

public sealed class ContractorStore(OfficeDbContext db, ICurrentActor current, AccountInvitations audit) : IContractorStore
{
    public async Task<IReadOnlyList<ContractorDetails>> ListAsync(Actor actor, CancellationToken cancellationToken)
        => await (from contact in db.Contractors.AsNoTracking()
                  join manager in db.BuildingManagers on contact.BuildingId equals manager.BuildingId
                  where manager.UserId == actor.UserId && actor.ManagedBuildings.Contains(contact.BuildingId)
                  orderby contact.Active descending, contact.Name, contact.Id
                  select new ContractorDetails(contact.Id, contact.BuildingId, contact.Version, contact.Name, contact.Trade, contact.Email, contact.Phone, contact.Active))
            .Take(100).ToListAsync(cancellationToken);

    public async Task<MaintenanceCode> SaveAsync(Actor actor, SaveContractor command, CancellationToken cancellationToken)
    {
        await using var transaction = await AuthorityLocks.BeginAsync(db, cancellationToken);
        await AuthorityLocks.UserAsync(db, actor.UserId, cancellationToken);
        await AuthorityLocks.ManagerAsync(db, actor.UserId, command.BuildingId, cancellationToken);
        await AuthorityLocks.ContractorAsync(db, command.Id, cancellationToken);
        var fresh = await current.GetAsync(cancellationToken);
        if (!MaintenanceActions.CanUse(fresh, true) || !fresh!.ManagedBuildings.Contains(command.BuildingId) ||
            !await db.BuildingManagers.AnyAsync(x => x.BuildingId == command.BuildingId && x.UserId == actor.UserId, cancellationToken))
            return MaintenanceCode.Unavailable;
        var contact = await db.Contractors.SingleOrDefaultAsync(x => x.Id == command.Id, cancellationToken);
        if (command.Version is null)
        {
            if (contact is not null) return MaintenanceCode.Conflict;
            contact = Contractor.Create(command.Id, command.BuildingId, command.Name, command.Trade, command.Email, command.Phone);
            db.Contractors.Add(contact);
        }
        else
        {
            if (contact is null || contact.BuildingId != command.BuildingId) return MaintenanceCode.Unavailable;
            if (contact.Version != command.Version) return MaintenanceCode.Conflict;
            contact.Update(command.Name, command.Trade, command.Email, command.Phone, command.Active);
        }
        audit.Audit(actor.UserId, command.Version is null ? "ContractorCreated" : contact.Active ? "ContractorUpdated" : "ContractorDeactivated", contact.Id.ToString("D"));
        try { await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); return MaintenanceCode.Success; }
        catch (DbUpdateConcurrencyException) { return MaintenanceCode.Conflict; }
        catch (DbUpdateException exception) when (exception.InnerException is MySql.Data.MySqlClient.MySqlException { Number: 1062 } or Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 })
        { return MaintenanceCode.Conflict; }
    }
}
