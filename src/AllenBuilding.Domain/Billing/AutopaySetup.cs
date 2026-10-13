namespace AllenBuilding.Domain.Billing;

public enum AutopaySetupState { Waiting, Ready, Expired }
public sealed record VerifiedAutopaySetup(string ProviderAccount, bool LiveMode, Guid AccountId, string CustomerId, string ConsentHash,
    string SessionId, DateTimeOffset ExpiresAt, AutopaySetupState State, string? PaymentMethodId, bool SetupOnly, bool OffSessionUsage, DateTimeOffset CheckedAt);
