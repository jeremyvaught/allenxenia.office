namespace AllenBuilding.Domain.Billing;

public enum AutopayWorkStage { Setup, Activate, Stop }

public sealed class AutopaySlot
{
    public Guid AccountId { get; set; }
    public Guid AuthorizationId { get; set; }
}

// A durable original provider operation. Workers/adapters are a subsequent milestone; no runtime dispatcher is registered yet.
public sealed class AutopayWork
{
    private AutopayWork() { }
    public Guid Id { get; private set; }
    public Guid AuthorizationId { get; private set; }
    public Guid AccountId { get; private set; }
    public AutopayWorkStage Stage { get; private set; }
    public Guid OriginalOperationId { get; private set; }
    public Guid ReviewedAuthorizationVersion { get; private set; }
    public string ProviderOperationKey { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public CollectionWorkState State { get; private set; }
    public static AutopayWork Enqueue(AutopayAuthorization authorization, AutopayWorkStage stage, DateTimeOffset now)
    {
        if (authorization is null || !Enum.IsDefined(stage) || now < authorization.ConsentedAt || (stage switch
        { AutopayWorkStage.Setup => authorization.State != AutopayState.AwaitingMethod, AutopayWorkStage.Activate => authorization.State != AutopayState.AwaitingProvider, _ => authorization.State != AutopayState.StopPending || authorization.StopOperationId is null }))
            throw new ArgumentException("Provider work must use the original authorization and matching durable stage.");
        return new()
        {
            Id = Guid.NewGuid(),
            AuthorizationId = authorization.Id,
            AccountId = authorization.AccountId,
            Stage = stage,
            OriginalOperationId = stage == AutopayWorkStage.Stop ? authorization.StopOperationId!.Value : authorization.OperationId,
            ReviewedAuthorizationVersion = authorization.Version,
            ProviderOperationKey = stage switch { AutopayWorkStage.Setup => authorization.SetupOperationKey, AutopayWorkStage.Activate => authorization.SubscriptionOperationKey, _ => authorization.StopOperationKey! },
            CreatedAt = now.ToUniversalTime(),
            State = CollectionWorkState.Queued
        };
    }
}
