using System.Security.Cryptography;
using System.Text.Json;
using AllenBuilding.Application.Access;
using AllenBuilding.Domain.Billing;

namespace AllenBuilding.Application.Billing;

public sealed record AutopayPolicy(bool Enabled, string ProviderAccount, bool LiveMode);
public sealed record AutopayConsentReview(Guid OperationId, Guid AccountId, Guid AccountVersion, AutopaySchedule Schedule, string Suite);
public sealed record ConfirmAutopay(Guid OperationId, Guid AccountId, Guid AccountVersion, AutopaySchedule Schedule, bool ExplicitlyAccepted);
public sealed record StopAutopay(Guid OperationId, Guid AuthorizationId, Guid Version, AutopayStopTiming Timing);
public sealed record AutopayResult(BillingCode Code, Guid? AuthorizationId = null);
public sealed record AutopayView(Guid Id, Guid AccountId, Guid Version, string Suite, AutopayState State, long MonthlyMinorUnits,
    int DueDay, DateOnly FirstPeriodStartsOn, DateOnly FirstDueOn, DateOnly? EndsBefore, DateTimeOffset? StopEffectiveAt);
public interface IAutopayStore
{
    Task<AutopayConsentReview?> ReviewAsync(Actor actor, Guid accountId, Guid agreementId, CancellationToken cancellationToken);
    Task<IReadOnlyList<AutopayView>> ReadAsync(Actor actor, Guid? accountId, CancellationToken cancellationToken);
    Task<AutopayResult> ConfirmAsync(Actor actor, ConfirmAutopay command, string hash, CancellationToken cancellationToken);
    Task<AutopayResult> StopAsync(Actor actor, StopAutopay command, string hash, CancellationToken cancellationToken);
}
public sealed class AutopayPlanning(ICurrentActor current, IAutopayStore store, AutopayPolicy policy)
{
    public async Task<AutopayConsentReview?> ReviewAsync(Guid accountId, Guid agreementId, CancellationToken cancellationToken = default)
    {
        var actor = await CurrentActorAsync(cancellationToken);
        if (actor is null || accountId == Guid.Empty || agreementId == Guid.Empty) return null;
        return await store.ReviewAsync(actor, accountId, agreementId, cancellationToken);
    }
    public async Task<IReadOnlyList<AutopayView>> ReadAsync(Guid? accountId = null, CancellationToken cancellationToken = default)
    {
        var actor = await CurrentActorAsync(cancellationToken);
        return actor is null || accountId == Guid.Empty ? [] : await store.ReadAsync(actor, accountId, cancellationToken);
    }
    public async Task<AutopayResult> ConfirmAsync(ConfirmAutopay command, CancellationToken cancellationToken = default)
    {
        var actor = await CurrentActorAsync(cancellationToken); if (actor is null) return new(BillingCode.Unavailable);
        if (command.OperationId == Guid.Empty || command.AccountId == Guid.Empty || command.AccountVersion == Guid.Empty || command.Schedule is null || !command.ExplicitlyAccepted)
            return new(BillingCode.ValidationFailed);
        return await store.ConfirmAsync(actor, command, Hash(command), cancellationToken);
    }
    public async Task<AutopayResult> StopAsync(StopAutopay command, CancellationToken cancellationToken = default)
    {
        var actor = await CurrentActorAsync(cancellationToken); if (actor is null) return new(BillingCode.Unavailable);
        if (command.OperationId == Guid.Empty || command.AuthorizationId == Guid.Empty || command.Version == Guid.Empty || !Enum.IsDefined(command.Timing)) return new(BillingCode.ValidationFailed);
        return await store.StopAsync(actor, command, Hash(command), cancellationToken);
    }
    private async Task<Actor?> CurrentActorAsync(CancellationToken token)
    {
        if (!policy.Enabled || !PaymentReferences.Valid(policy.ProviderAccount, "acct_")) return null;
        var actor = await current.GetAsync(token);
        return actor is null || actor.ManagedBuildings.Count > 0 && !actor.ChallengedMfa ? null : actor;
    }
    private static string Hash<T>(T command) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Type = typeof(T).Name, command })));
}
