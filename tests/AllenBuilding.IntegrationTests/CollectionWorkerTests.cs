using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Infrastructure.Payments;

namespace AllenBuilding.IntegrationTests;

public sealed class CollectionWorkerTests
{
    [Fact]
    public async Task Shutdown_during_dispatch_retains_an_unknown_result_with_an_independent_completion_budget()
    {
        using var shutdown = new CancellationTokenSource(); var queue = new Queue(); var gateway = new InterruptedGateway(shutdown);
        Assert.True(await new CollectionWorker(queue, gateway).ProcessOneAsync(shutdown.Token));
        Assert.True(shutdown.IsCancellationRequested); Assert.Equal(ProviderCollectionState.Unknown, queue.Result?.State);
        Assert.False(queue.CompletionWasCancelled); Assert.True(gateway.Disposed);
    }
    [Fact]
    public void Gateway_factory_uses_the_frozen_profile_and_rejects_different_merchant_or_mode()
    {
        Assert.Equal(CheckoutProfile.ApiContract, Stripe.StripeConfiguration.ApiVersion);
        var queue = new Queue(); var settings = new StripeCollectionOptions
        {
            ApiKey = "sk_test_synthetic_no_network",
            ProviderAccount = "acct_worker",
            LiveMode = false,
            PublicOrigin = "invalid-new-setting",
            PaymentMethodConfiguration = "invalid-new-setting"
        };
        var factory = new StripeCollectionGatewayFactory(settings, TimeProvider.System);
        using var adapter = (IDisposable)factory.Open(queue.Lease.Original, queue.Lease.Profile);
        settings.ProviderAccount = "acct_other"; Assert.Throws<InvalidOperationException>(() => factory.Open(queue.Lease.Original, queue.Lease.Profile));
        settings.ProviderAccount = "acct_worker"; settings.LiveMode = true; Assert.Throws<InvalidOperationException>(() => factory.Open(queue.Lease.Original, queue.Lease.Profile));
    }
    private sealed class Queue : ICollectionWorkQueue
    {
        public CollectionLease Lease { get; }
        public ProviderCollectionObservation? Result { get; private set; }
        public bool CompletionWasCancelled { get; private set; }
        public Queue()
        {
            var now = DateTimeOffset.UtcNow; var account = Guid.NewGuid(); var binding = BillingCustomerBinding.Create(account, "acct_worker", false, "cus_worker", now);
            var original = PaymentAttempt.Admit(Guid.NewGuid(), "synthetic-payer", binding, [new(Guid.NewGuid(), account, Guid.NewGuid(), 1000)], now); original.BeginDispatch(now);
            Lease = new(original, Guid.NewGuid(), new("https://original.example.test", "pmc_original"));
        }
        public Task<CollectionLease?> ClaimAsync(CancellationToken cancellationToken) => Task.FromResult<CollectionLease?>(Lease);
        public Task<bool> CompleteAsync(CollectionLease lease, ProviderCollectionObservation observation, CancellationToken cancellationToken)
        { Result = observation; CompletionWasCancelled = cancellationToken.IsCancellationRequested; Assert.Equal(Lease.Token, lease.Token); return Task.FromResult(true); }
    }
    private sealed class InterruptedGateway(CancellationTokenSource shutdown) : ICollectionGatewayFactory, ICollectionGateway, IDisposable
    {
        public bool Disposed { get; private set; }
        public ICollectionGateway Open(PaymentAttempt original, CheckoutProfile profile) => this;
        public Task<ProviderCollectionObservation> CreateAsync(PaymentAttempt original, CancellationToken cancellationToken = default)
        { shutdown.Cancel(); throw new OperationCanceledException(cancellationToken); }
        public Task<ProviderCollectionObservation> ReadAsync(PaymentAttempt original, string sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ProviderCollectionObservation> ExpireAsync(PaymentAttempt original, string sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() => Disposed = true;
    }
}
