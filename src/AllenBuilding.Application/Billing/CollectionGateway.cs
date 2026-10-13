using AllenBuilding.Domain.Billing;

namespace AllenBuilding.Application.Billing;

public enum ProviderCollectionState { Unknown, Invalid, Open, Processing, Paid, Uncollected }
public sealed record ProviderCollectionObservation(ProviderCollectionState State, string? SessionId = null, Uri? Handoff = null,
    DateTimeOffset? ExpiresAt = null, CanonicalSettlement? Settlement = null, CashInventoryObservation? CashInventory = null);

// Canonical provider reads and dispatch are outside local transactions. No redirect is a receipt.
public interface ICollectionGateway
{
    Task<ProviderCollectionObservation> CreateAsync(PaymentAttempt original, CancellationToken cancellationToken = default);
    Task<ProviderCollectionObservation> ReadAsync(PaymentAttempt original, string sessionId, CancellationToken cancellationToken = default);
    Task<ProviderCollectionObservation> ExpireAsync(PaymentAttempt original, string sessionId, CancellationToken cancellationToken = default);
}
