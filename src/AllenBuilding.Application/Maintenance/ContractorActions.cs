using AllenBuilding.Application.Access;
using AllenBuilding.Domain.Maintenance;

namespace AllenBuilding.Application.Maintenance;

public sealed record ContractorDetails(Guid Id, Guid BuildingId, Guid Version, string Name, Trade Trade, string Email, string Phone, bool Active);
public sealed record SaveContractor(Guid Id, Guid BuildingId, Guid? Version, string Name, Trade Trade, string Email, string Phone, bool Active = true);
public interface IContractorStore
{
    Task<IReadOnlyList<ContractorDetails>> ListAsync(Actor actor, CancellationToken cancellationToken);
    Task<MaintenanceCode> SaveAsync(Actor actor, SaveContractor command, CancellationToken cancellationToken);
}

public sealed class ContractorActions(ICurrentActor current, IContractorStore store)
{
    public async Task<IReadOnlyList<ContractorDetails>> ListAsync(CancellationToken cancellationToken = default)
    {
        var actor = await current.GetAsync(cancellationToken);
        return MaintenanceActions.CanUse(actor, true) ? await store.ListAsync(actor!, cancellationToken) : [];
    }

    public async Task<MaintenanceCode> SaveAsync(SaveContractor command, CancellationToken cancellationToken = default)
    {
        var actor = await current.GetAsync(cancellationToken);
        if (actor is null) return MaintenanceCode.AuthenticationRequired;
        if (!MaintenanceActions.CanUse(actor, true) || !actor.ManagedBuildings.Contains(command.BuildingId)) return MaintenanceCode.Unavailable;
        if (command.Version == Guid.Empty || (command.Version is null && !command.Active)) return MaintenanceCode.ValidationFailed;
        try { Contractor.Create(command.Id, command.BuildingId, command.Name, command.Trade, command.Email, command.Phone); }
        catch (ArgumentException) { return MaintenanceCode.ValidationFailed; }
        return await store.SaveAsync(actor, command, cancellationToken);
    }
}
