using AllenBuilding.Application.Access;
using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;

namespace AllenBuilding.IntegrationTests;

public sealed class PayerHandoffTests
{
    [Theory]
    [InlineData("valid", true)]
    [InlineData("revoked", false)]
    [InlineData("changed", false)]
    [InlineData("held", false)]
    [InlineData("expired", false)]
    [InlineData("paid", false)]
    [InlineData("foreign", false)]
    [InlineData("http", false)]
    [InlineData("user-info", false)]
    [InlineData("port", false)]
    [InlineData("unknown", false)]
    public async Task Private_checkout_link_requires_current_original_access_and_exact_verified_provider_handoff(string mutation, bool allowed)
    {
        var ports = new Ports(mutation);
        var uri = await new PayerCollections(ports, ports, ports.Policy, TimeProvider.System, ports).ContinueAsync(ports.Original.Id);
        Assert.Equal(allowed, uri is not null); Assert.Equal(1, ports.Reads); Assert.Equal(0, ports.Creates); Assert.True(ports.Disposed);
    }
    private sealed class Ports : ICurrentActor, IPayerCollectionReader, ICollectionGatewayFactory, ICollectionGateway, IDisposable
    {
        private readonly string _mutation;
        public CollectionPolicy Policy { get; } = new(true, "acct_handoff", false, new("https://handoff.example.test", "pmc_handoff"));
        public PaymentAttempt Original { get; }
        public int Reads { get; private set; }
        public int Creates { get; private set; }
        public bool Disposed { get; private set; }
        public Ports(string mutation)
        {
            _mutation = mutation; var now = DateTimeOffset.UtcNow; var account = Guid.NewGuid();
            Original = PaymentAttempt.Admit(Guid.NewGuid(), "payer", BillingCustomerBinding.Create(account, Policy.ProviderAccount, false, "cus_handoff", now), [new(Guid.NewGuid(), account, Guid.NewGuid(), 1000)], now);
            Original.BeginDispatch(now); Original.AttachHandoff("cs_handoff", now.AddHours(1), now);
        }
        public Task<Actor?> GetAsync(CancellationToken token = default) => Task.FromResult<Actor?>(Reads > 0 && _mutation == "revoked" ? null : new("payer", new HashSet<Guid>(), false, false));
        public Task<PayerCollectionSnapshot?> ReadAsync(Actor actor, Guid attemptId, CancellationToken token)
        {
            var view = new PayerPaymentView(Original.Id, Original.AccountId, "Office", Original.State, 1000, Original.CreatedAt, 0, 0, 0, Reads > 0 && _mutation == "held", !(Reads > 0 && _mutation == "held"), [], Original.CreatedAt);
            return Task.FromResult<PayerCollectionSnapshot?>(new(view, Original, Policy.Profile!));
        }
        public Task<IReadOnlyList<PayerPaymentSummary>> RecentAsync(Actor actor, Guid accountId, CancellationToken token) => Task.FromResult<IReadOnlyList<PayerPaymentSummary>>([]);
        public ICollectionGateway Open(PaymentAttempt original, CheckoutProfile profile) => this;
        public Task<ProviderCollectionObservation> CreateAsync(PaymentAttempt original, CancellationToken token = default) { Creates++; throw new InvalidOperationException(); }
        public Task<ProviderCollectionObservation> ReadAsync(PaymentAttempt original, string sessionId, CancellationToken token = default)
        {
            Reads++; if (_mutation == "changed") Original.ObserveProcessing("cs_handoff");
            if (_mutation == "unknown") throw new HttpRequestException("Private synthetic failure");
            var uri = new Uri(_mutation switch { "foreign" => "https://checkout.stripe.com.evil.test/x", "http" => "http://checkout.stripe.com/x", "user-info" => "https://user@checkout.stripe.com/x", "port" => "https://checkout.stripe.com:8443/x", _ => "https://checkout.stripe.com/c/pay/original" });
            return Task.FromResult(new ProviderCollectionObservation(_mutation == "paid" ? ProviderCollectionState.Paid : ProviderCollectionState.Open, sessionId, uri,
                _mutation == "expired" ? DateTimeOffset.UtcNow.AddMinutes(-1) : Original.ExpiresAt));
        }
        public Task<ProviderCollectionObservation> ExpireAsync(PaymentAttempt original, string sessionId, CancellationToken token = default) => throw new NotSupportedException();
        public void Dispose() => Disposed = true;
    }
}
