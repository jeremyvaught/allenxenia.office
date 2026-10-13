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
    [Fact]
    public async Task Settled_read_only_recovery_requires_inventory_and_never_creates_collection()
    {
        var missing = new Queue(true); var gateway = new PaidGateway();
        Assert.True(await new CollectionWorker(missing, gateway).ProcessOneAsync()); Assert.Equal(CashObservationState.Invalid, missing.Result?.CashInventory?.State); Assert.Equal(1, gateway.Reads);
        var queue = new Queue(true); var reader = new InventoryReader();
        Assert.True(await new CollectionWorker(queue, gateway, reader).ProcessOneAsync()); Assert.Equal(CashObservationState.Verified, queue.Result?.CashInventory?.State); Assert.Equal(queue.Lease.Original.Id, reader.OriginalId); Assert.False(queue.CompletionWasCancelled);
        reader.Fail = true; var failed = new Queue(true); Assert.True(await new CollectionWorker(failed, gateway, reader).ProcessOneAsync()); Assert.Equal(ProviderCollectionState.Unknown, failed.Result?.State); Assert.False(failed.CompletionWasCancelled);
    }
    private sealed class PaidGateway : ICollectionGatewayFactory, ICollectionGateway
    {
        public int Reads { get; private set; }
        public ICollectionGateway Open(PaymentAttempt original, CheckoutProfile profile) => this;
        public Task<ProviderCollectionObservation> CreateAsync(PaymentAttempt original, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Recovery must never create a collection");
        public Task<ProviderCollectionObservation> ReadAsync(PaymentAttempt original, string sessionId, CancellationToken cancellationToken = default)
        { Reads++; return Task.FromResult(new ProviderCollectionObservation(ProviderCollectionState.Paid, sessionId, Settlement: new(original.ProviderAccount, false, original.CustomerId, sessionId, original.PaymentId!, original.TotalMinorUnits, "USD"))); }
        public Task<ProviderCollectionObservation> ExpireAsync(PaymentAttempt original, string sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class InventoryReader : ICashInventoryReader
    {
        public Guid OriginalId { get; private set; }
        public bool Fail { get; set; }
        public Task<CashInventoryObservation> ReadInventoryAsync(PaymentAttempt original, CancellationToken cancellationToken)
        {
            OriginalId = original.Id; if (Fail) throw new HttpRequestException("Synthetic unknown inventory");
            return Task.FromResult(new CashInventoryObservation(CashObservationState.Verified, new(original.ProviderAccount, false, original.AccountId, original.Id, original.CustomerId, original.PaymentId!, "ch_inventory", original.TotalMinorUnits, 0, [], [], DateTimeOffset.UtcNow)));
        }
    }
    private sealed class Queue : ICollectionWorkQueue
    {
        public CollectionLease Lease { get; }
        public ProviderCollectionObservation? Result { get; private set; }
        public bool CompletionWasCancelled { get; private set; }
        public Queue(bool settled = false)
        {
            var now = DateTimeOffset.UtcNow; var account = Guid.NewGuid(); var binding = BillingCustomerBinding.Create(account, "acct_worker", false, "cus_worker", now);
            var original = PaymentAttempt.Admit(Guid.NewGuid(), "synthetic-payer", binding, [new(Guid.NewGuid(), account, Guid.NewGuid(), 1000)], now); original.BeginDispatch(now);
            if (settled) SettlementPayment.Record(original, new(binding.ProviderAccount, false, binding.CustomerId, "cs_inventory", "pi_inventory", 1000, "USD"), now);
            Lease = new(original, Guid.NewGuid(), new("https://original.example.test", "pmc_original"), ReadOnly: settled);
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
