using System.Security.Cryptography;
using System.Text.Json;
using AllenBuilding.Application.Access;
using AllenBuilding.Domain.Billing;

namespace AllenBuilding.Application.Billing;

public enum FinancialCaseAction { Assign, Escalate }
public sealed record FinancialCaseCommand(Guid OperationId, Guid Id, Guid Version, FinancialCaseAction Action, string Reason, string? OwnerUserId = null);
public sealed record FinancialCaseItem(Guid ObligationId, Guid ReviewedVersion, long AmountMinorUnits, string Currency);
public sealed record FinancialCaseOwner(string UserId, string Label);
public sealed record FinancialCaseHistory(string Action, string? ActorUserId, string? Reason, DateTimeOffset OccurredAt);
public sealed record FinancialCaseView(Guid Id, Guid Version, Guid BuildingId, Guid AccountId, Guid AttemptId, string Suite,
    FinancialExceptionState State, FinancialFailure Failure, string? OwnerUserId, string ProviderAccount, bool LiveMode, string CustomerId,
    string? SessionId, string? PaymentId, Guid OriginalOperationId, PaymentAttemptState AttemptState, string? LastVerifiedState,
    DateTimeOffset? LastVerifiedAt, DateTimeOffset FirstSeenAt, IReadOnlyList<FinancialCaseItem> Items,
    IReadOnlyList<FinancialCaseOwner> Owners, IReadOnlyList<FinancialCaseHistory> History);
public interface IFinancialExceptionStore
{
    Task<IReadOnlyList<FinancialCaseView>> ReadAsync(Actor actor, bool includeResolved, CancellationToken cancellationToken);
    Task<BillingResult> ChangeAsync(Actor actor, FinancialCaseCommand command, string hash, CancellationToken cancellationToken);
}
public sealed class FinancialExceptionActions(ICurrentActor current, IFinancialExceptionStore store)
{
    public async Task<IReadOnlyList<FinancialCaseView>> ReadAsync(bool includeResolved = false, CancellationToken cancellationToken = default)
    {
        var actor = await current.GetAsync(cancellationToken);
        return actor is { ChallengedMfa: true } && actor.ManagedBuildings.Count > 0 ? await store.ReadAsync(actor, includeResolved, cancellationToken) : [];
    }
    public async Task<BillingResult> ChangeAsync(FinancialCaseCommand command, CancellationToken cancellationToken = default)
    {
        var actor = await current.GetAsync(cancellationToken);
        if (actor is not { ChallengedMfa: true, RecentMfa: true } || actor.ManagedBuildings.Count == 0) return new(BillingCode.Unavailable);
        if (command.Id == Guid.Empty || command.Version == Guid.Empty || command.OperationId == Guid.Empty || !Enum.IsDefined(command.Action) ||
            string.IsNullOrWhiteSpace(command.Reason) || command.Reason.Trim().Length > 2000 ||
            (command.Action == FinancialCaseAction.Assign && (string.IsNullOrWhiteSpace(command.OwnerUserId) || command.OwnerUserId.Length > 450)) ||
            (command.Action == FinancialCaseAction.Escalate && command.OwnerUserId is not null)) return new(BillingCode.ValidationFailed);
        command = command with { Reason = command.Reason.Trim() };
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Type = "FinancialCaseRecoveryV1", command })));
        return await store.ChangeAsync(actor, command, hash, cancellationToken);
    }
}
