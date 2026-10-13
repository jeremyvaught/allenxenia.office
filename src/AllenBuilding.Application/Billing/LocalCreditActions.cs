using System.Security.Cryptography;
using System.Text.Json;
using AllenBuilding.Application.Access;
using AllenBuilding.Domain.Billing;

namespace AllenBuilding.Application.Billing;

public sealed record LocalCreditPolicy(bool Enabled);
public sealed record CreditLocalDebt(Guid OperationId, Guid AccountId, Guid ObligationId, Guid ReviewedVersion, long AmountMinorUnits, ObligationCreditKind Kind, string Reason);
public interface ILocalCreditStore
{
    Task<BillingResult> ApplyAsync(Actor actor, CreditLocalDebt command, string hash, CancellationToken cancellationToken);
}
// This narrow path is for never-provider-bound local debt; provider credits/refunds require the manual intent workflow.
public sealed class LocalCreditActions(ICurrentActor current, ILocalCreditStore store, LocalCreditPolicy policy, TimeProvider time)
{
    public async Task<BillingResult> ApplyAsync(CreditLocalDebt command, CancellationToken cancellationToken = default)
    {
        if (!policy.Enabled) return new(BillingCode.Unavailable);
        var actor = await current.GetAsync(cancellationToken);
        if (actor is not { ChallengedMfa: true, RecentMfa: true } || actor.ManagedBuildings.Count == 0) return new(BillingCode.Unavailable);
        try
        {
            ObligationCredit.RecordLocal(command.OperationId, actor.UserId, new(command.ObligationId, command.AccountId, command.ReviewedVersion, command.AmountMinorUnits),
                command.AmountMinorUnits, command.Kind, command.Reason, time.GetUtcNow());
        }
        catch (ArgumentException) { return new(BillingCode.ValidationFailed); }
        var normalized = command with { Reason = command.Reason.Trim() };
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(normalized)));
        return await store.ApplyAsync(actor, normalized, hash, cancellationToken);
    }
}
