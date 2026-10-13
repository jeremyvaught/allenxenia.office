using System.Security.Cryptography;
using System.Text.Json;
using AllenBuilding.Application.Access;
using AllenBuilding.Domain.Billing;

namespace AllenBuilding.Application.Billing;

public sealed record RefundDispositionPolicy(bool Enabled);
public sealed record ReviewedRefundItem(Guid ObligationId, Guid Version, long AmountMinorUnits);
public sealed record ApplyRefundDisposition(Guid OperationId, Guid AccountId, Guid MovementId, Guid ReviewVersion,
    RefundDisposition Disposition, IReadOnlyList<ReviewedRefundItem> Items, string Reason);
public interface IRefundDispositionStore
{
    Task<BillingResult> ApplyAsync(Actor actor, ApplyRefundDisposition command, string hash, CancellationToken cancellationToken);
}
// Only the separately confirmed server command calls this use case. It never dispatches a provider write or releases a collection hold.
public sealed class RefundDispositionActions(ICurrentActor current, IRefundDispositionStore store, RefundDispositionPolicy policy)
{
    public async Task<BillingResult> ApplyAsync(ApplyRefundDisposition command, CancellationToken cancellationToken = default)
    {
        if (!policy.Enabled) return new(BillingCode.Unavailable);
        var actor = await current.GetAsync(cancellationToken);
        if (actor is not { ChallengedMfa: true, RecentMfa: true } || actor.ManagedBuildings.Count == 0) return new(BillingCode.Unavailable);
        if (command.OperationId == Guid.Empty || command.AccountId == Guid.Empty || command.MovementId == Guid.Empty || command.ReviewVersion == Guid.Empty ||
            !Enum.IsDefined(command.Disposition) || command.Items is not { Count: >= 1 and <= 25 } || command.Items.Any(x => x is null ||
                x.ObligationId == Guid.Empty || x.Version == Guid.Empty || x.AmountMinorUnits is < 1 or > 99_999_999) ||
            command.Items.Select(x => x.ObligationId).Distinct().Count() != command.Items.Count || string.IsNullOrWhiteSpace(command.Reason) || command.Reason.Trim().Length > 2000)
            return new(BillingCode.ValidationFailed);
        var normalized = command with { Reason = command.Reason.Trim(), Items = command.Items.OrderBy(x => x.ObligationId.ToString("D"), StringComparer.Ordinal).ToArray() };
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Type = "RefundDispositionV1", Command = normalized })));
        return await store.ApplyAsync(actor, normalized, hash, cancellationToken);
    }
}
