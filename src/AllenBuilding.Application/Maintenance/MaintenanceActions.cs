using System.Security.Cryptography;
using System.Text.Json;
using AllenBuilding.Application.Access;
using AllenBuilding.Domain.Maintenance;

namespace AllenBuilding.Application.Maintenance;

public sealed class MaintenanceActions(ICurrentActor current, IMaintenanceStore store, TimeProvider time)
{
    public async Task<IReadOnlyList<RequestSummary>> ListAsync(bool management, CancellationToken cancellationToken = default)
    {
        var actor = await current.GetAsync(cancellationToken);
        return CanUse(actor, management) ? await store.ListAsync(actor!, management, cancellationToken) : [];
    }

    public async Task<RequestDetails?> ReadAsync(Guid id, bool management, CancellationToken cancellationToken = default)
    {
        var actor = await current.GetAsync(cancellationToken);
        return CanUse(actor, management) ? await store.ReadAsync(actor!, id, management, cancellationToken) : null;
    }

    public async Task<MaintenanceResult> SubmitAsync(SubmitRequest command, CancellationToken cancellationToken = default)
    {
        var actor = await current.GetAsync(cancellationToken);
        if (actor is null) return new(MaintenanceCode.AuthenticationRequired);
        if (!CanUse(actor, false)) return new(MaintenanceCode.Unavailable);
        if (command.OperationId == Guid.Empty || command.MembershipId == Guid.Empty) return new(MaintenanceCode.ValidationFailed);
        // Validate using the same domain constructor before opening the persistence transaction.
        try { WorkOrder.Submit(Guid.NewGuid(), command.MembershipId, actor.UserId, command.Title, command.Description, command.Location, time.GetUtcNow()); }
        catch (ArgumentException) { return new(MaintenanceCode.ValidationFailed); }
        return await store.SubmitAsync(actor, command, Hash(command), time.GetUtcNow(), cancellationToken);
    }

    public async Task<MaintenanceResult> ChangeAsync(ChangeRequest command, bool management, CancellationToken cancellationToken = default)
    {
        var actor = await current.GetAsync(cancellationToken);
        if (actor is null) return new(MaintenanceCode.AuthenticationRequired);
        if (!CanUse(actor, management) || (!management && (command.Action != MaintenanceAction.Comment || command.Internal)))
            return new(MaintenanceCode.Unavailable);
        if (command.OperationId == Guid.Empty || command.WorkOrderId == Guid.Empty || command.Version == Guid.Empty ||
            !Enum.IsDefined(command.Action) || (command.Internal && command.Action != MaintenanceAction.Comment) ||
            (command.Priority is { } priority && !Enum.IsDefined(priority)) || (command.Trade is { } trade && !Enum.IsDefined(trade)) ||
            (command.Note?.Length > 2000)) return new(MaintenanceCode.ValidationFailed);
        var now = time.GetUtcNow();
        void Mutate(WorkOrder order)
        {
            switch (command.Action)
            {
                case MaintenanceAction.Triage: order.Triage(command.Priority ?? throw new ArgumentException(), command.Trade ?? throw new ArgumentException(), now); break;
                case MaintenanceAction.AssignSelf: order.Assign(actor.UserId, null, now); break;
                case MaintenanceAction.Start: order.Start(now); break;
                case MaintenanceAction.Complete: order.Complete(command.Note ?? "", now); break;
                case MaintenanceAction.Close: order.Close(now); break;
                case MaintenanceAction.Cancel: order.Cancel(command.Note ?? "", now); break;
                case MaintenanceAction.Reopen: order.Reopen(command.Note ?? "", now); break;
                case MaintenanceAction.Comment: order.RecordComment(command.Note ?? "", now); break;
            }
        }
        return await store.ChangeAsync(actor, command, Hash(new { command, management }), now, management, Mutate, cancellationToken);
    }

    public static bool CanUse(Actor? actor, bool management) => actor is not null && (management
        ? actor.ChallengedMfa && actor.ManagedBuildings.Count > 0
        : actor.ManagedBuildings.Count == 0 || actor.ChallengedMfa);

    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
