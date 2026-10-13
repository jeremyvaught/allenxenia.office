using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;

namespace AllenBuilding.Infrastructure.Payments;

// Runtime credentials can rotate. The original request's public settings cannot.
public sealed class StripeCollectionGatewayFactory(StripeCollectionOptions credentials, TimeProvider time) : ICollectionGatewayFactory
{
    public ICollectionGateway Open(PaymentAttempt original, CheckoutProfile profile)
    {
        if (original.ProviderAccount != credentials.ProviderAccount || original.LiveMode != credentials.LiveMode || profile.ApiVersion != Stripe.StripeConfiguration.ApiVersion)
            throw new InvalidOperationException("The original provider scope or API contract is unavailable.");
        return new StripeCollectionGateway(new StripeCollectionOptions
        {
            ApiKey = credentials.ApiKey,
            ProviderAccount = original.ProviderAccount,
            LiveMode = original.LiveMode,
            PublicOrigin = profile.PublicOrigin,
            PaymentMethodConfiguration = profile.PaymentMethodConfiguration
        }, time);
    }
}
