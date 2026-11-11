using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Infrastructure.Persistence;
using AllenBuilding.Web.Security;

namespace AllenBuilding.Web.Hosting;

public static class BillingRegistration
{
    public static void AddOfficeBilling(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        var configuration = builder.Configuration;
        var testing = builder.Environment.IsEnvironment("Testing");
        services.AddScoped<IBillingStore, BillingStore>();
        services.AddScoped<BillingActions>();
        // Admission is wired but remains unavailable until provider dispatch and reconciliation are verified.
        var isolatedPayments = testing && configuration.GetValue<bool>("Billing:CollectionReviewEnabled");
        services.AddSingleton(isolatedPayments
            ? new CollectionPolicy(true, configuration["Stripe:ProviderAccount"] ?? "", false,
                new CheckoutProfile(configuration["Stripe:PublicOrigin"] ?? "",
                    configuration["Stripe:PaymentMethodConfiguration"] ?? ""))
            : new CollectionPolicy(false, "", false));
        services.AddScoped<ICollectionStore, CollectionStore>();
        services.AddScoped<CollectionActions>();
        services.AddScoped<IPayerCollectionReader, PayerCollectionReader>();
        services.AddScoped<PayerCollections>();
        services.AddScoped<PaymentReviewTickets>();
        // Protected consent pages may be exercised by isolated fixtures; no provider dispatcher is registered yet.
        var isolatedAutopay = testing && configuration.GetValue<bool>("Billing:AutopayReviewEnabled");
        services.AddSingleton(new AutopayPolicy(
            isolatedAutopay, configuration["Stripe:ProviderAccount"] ?? "", false,
            isolatedAutopay
                ? new CheckoutProfile(configuration["Stripe:PublicOrigin"] ?? "",
                    configuration["Stripe:PaymentMethodConfiguration"] ?? "")
                : null));
        services.AddScoped<IAutopayStore, AutopayStore>();
        services.AddScoped<IAutopayMethodStore>(s => (AutopayStore)s.GetRequiredService<IAutopayStore>());
        services.AddScoped<AutopayMethodHandoffs>();
        services.AddScoped<AutopayPlanning>();
        services.AddScoped<AutopayReviewTickets>();
        services.AddSingleton(new FinancialRecoveryPolicy(false));
        services.AddScoped<IFinancialExceptionStore, FinancialExceptionStore>();
        services.AddScoped<FinancialExceptionActions>();
        // Only isolated browser fixtures can opt in before whole-payment recovery is finished; ordinary runtime stays disabled.
        services.AddSingleton(new RefundDispositionPolicy(
            testing && configuration.GetValue<bool>("Billing:RefundReviewEnabled")));
        services.AddScoped<IRefundDispositionStore, RefundDispositionStore>();
        services.AddScoped<RefundDispositionActions>();
        services.AddSingleton(new CashSourceRecoveryPolicy(
            testing && configuration.GetValue<bool>("Billing:CashSourceReviewEnabled")));
        services.AddScoped<ICashSourceRecoveryStore, CashSourceRecoveryStore>();
        services.AddScoped<CashSourceRecoveryActions>();
        // No hosted reader or Stripe credential is registered while full payment agreement is unfinished.
        services.AddScoped<IPaymentWebhookReceiver, DisabledPaymentWebhookReceiver>();
        services.AddScoped<BillingReviewTickets>();
    }
}
