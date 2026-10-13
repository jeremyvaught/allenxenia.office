using System.Security.Cryptography;
using System.Text.Json;
using AllenBuilding.Application.Access;
using AllenBuilding.Domain.Billing;

namespace AllenBuilding.Application.Billing;

public sealed record CashSourceRecoveryPolicy(bool Enabled);
public sealed record InvestigateCashSource(Guid OperationId, Guid CaseId, Guid CaseVersion, string SourceId, string Reason);
public interface ICashSourceRecoveryStore
{
    Task<BillingResult> RequestAsync(Actor actor, InvestigateCashSource command, string hash, CancellationToken cancellationToken);
}
public sealed class CashSourceRecoveryActions(ICurrentActor current, ICashSourceRecoveryStore store, CashSourceRecoveryPolicy policy)
{
    public async Task<BillingResult> RequestAsync(InvestigateCashSource command, CancellationToken cancellationToken = default)
    {
        if (!policy.Enabled) return new(BillingCode.Unavailable);
        var actor = await current.GetAsync(cancellationToken);
        if (actor is not { ChallengedMfa: true, RecentMfa: true } || actor.ManagedBuildings.Count == 0) return new(BillingCode.Unavailable);
        if (command.OperationId == Guid.Empty || command.CaseId == Guid.Empty || command.CaseVersion == Guid.Empty ||
            (!PaymentReferences.Valid(command.SourceId, "re_") && !CashMovement.DisputeReference(command.SourceId)) ||
            string.IsNullOrWhiteSpace(command.Reason) || command.Reason.Trim().Length > 2000) return new(BillingCode.ValidationFailed);
        command = command with { Reason = command.Reason.Trim() };
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Type = "CashSourceRecoveryV1", command })));
        return await store.RequestAsync(actor, command, hash, cancellationToken);
    }
}
