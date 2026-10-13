using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using Stripe;

namespace AllenBuilding.Infrastructure.Payments;

public sealed class StripeCustomerCredentials
{
    public string ApiKey { get; set; } = "";
    public string ProviderAccount { get; set; } = "";
    public bool LiveMode { get; set; }
    public void Validate()
    {
        var mode = LiveMode ? "live" : "test";
        if ((!ApiKey.StartsWith("rk_" + mode + "_", StringComparison.Ordinal) && !ApiKey.StartsWith("sk_" + mode + "_", StringComparison.Ordinal)) ||
            ApiKey.Length <= 8 || ApiKey.Any(char.IsWhiteSpace) || !PaymentReferences.Valid(ProviderAccount, "acct_"))
            throw new InvalidOperationException("A mode-matched credential and merchant scope are required.");
    }
}
public sealed class StripeCustomerProvisionGatewayFactory(StripeCustomerCredentials credentials, TimeProvider time) : ICustomerProvisionGatewayFactory
{
    public ICustomerProvisionGateway Open(CustomerProvision original)
    {
        if (original.ProviderAccount != credentials.ProviderAccount || original.LiveMode != credentials.LiveMode || original.ApiVersion != StripeConfiguration.ApiVersion)
            throw new InvalidOperationException("The original customer operation's scope or API is unavailable.");
        return new StripeCustomerProvisionGateway(credentials, time);
    }
}

// Prepared adapter, not activated. It never searches by email, reuses another tenancy, or copies consent.
public sealed class StripeCustomerProvisionGateway : ICustomerProvisionGateway, IDisposable
{
    private readonly StripeCustomerCredentials _credentials;
    private readonly StripeClient _client;
    private readonly TimeProvider _time;
    private readonly HttpClient? _ownedHttp;
    public StripeCustomerProvisionGateway(StripeCustomerCredentials credentials, TimeProvider time) : this(credentials, time, null) { }
    internal StripeCustomerProvisionGateway(StripeCustomerCredentials credentials, TimeProvider time, StripeClient? testClient)
    {
        credentials.Validate(); _credentials = new() { ApiKey = credentials.ApiKey, ProviderAccount = credentials.ProviderAccount, LiveMode = credentials.LiveMode }; _time = time;
        if (testClient is null) _ownedHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _client = testClient ?? new StripeClient(new StripeClientOptions
        { ApiKey = credentials.ApiKey, HttpClient = new SystemNetHttpClient(_ownedHttp, maxNetworkRetries: 0, enableTelemetry: false) });
    }
    public async Task<CustomerObservation> CreateAsync(CustomerProvision original, CancellationToken cancellationToken)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(20)); var token = deadline.Token;
            if (original.ProviderAccount != _credentials.ProviderAccount || original.LiveMode != _credentials.LiveMode || original.ApiVersion != StripeConfiguration.ApiVersion ||
                original.State != CustomerProvisionState.Leased || !original.Owns(original.LeaseToken, _time.GetUtcNow()) || !original.CanCreate(_time.GetUtcNow())) return new(CustomerProvisionOutcome.Invalid);
            if ((await new AccountService(_client).GetSelfAsync(cancellationToken: token)).Id != original.ProviderAccount) return new(CustomerProvisionOutcome.Invalid);
            var service = new CustomerService(_client);
            var created = await service.CreateAsync(new CustomerCreateOptions
            {
                Description = "Office rent account",
                Metadata = new() { ["office_account_id"] = original.AccountId.ToString("D"), ["office_customer_operation"] = original.Id.ToString("D") }
            }, new RequestOptions { IdempotencyKey = original.ProviderIdempotencyKey }, token);
            if (!Matches(created, original)) return new(CustomerProvisionOutcome.Invalid);
            // A POST acknowledgment alone does not bind. Re-read the exact returned resource.
            var canonical = await service.GetAsync(created.Id, cancellationToken: token);
            if (canonical.Id != created.Id || !Matches(canonical, original) || canonical.Balance != 0 || canonical.DefaultSourceId is not null || canonical.InvoiceSettings?.DefaultPaymentMethodId is not null)
                return new(CustomerProvisionOutcome.Invalid);
            return new(CustomerProvisionOutcome.Verified, original.ProviderAccount, original.LiveMode, original.AccountId, original.Id, canonical.Id);
        }
        catch (OperationCanceledException) { return new(CustomerProvisionOutcome.Unknown); }
        catch (HttpRequestException) { return new(CustomerProvisionOutcome.Unknown); }
        catch (StripeException) { return new(CustomerProvisionOutcome.Unknown); }
    }
    private static bool Matches(Customer customer, CustomerProvision original) => PaymentReferences.Valid(customer.Id, "cus_") && customer.Deleted != true && customer.Livemode == original.LiveMode &&
        customer.Metadata?.GetValueOrDefault("office_account_id") == original.AccountId.ToString("D") && customer.Metadata?.GetValueOrDefault("office_customer_operation") == original.Id.ToString("D");
    public void Dispose() => _ownedHttp?.Dispose();
}
