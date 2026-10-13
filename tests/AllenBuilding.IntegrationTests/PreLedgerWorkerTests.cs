using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;

namespace AllenBuilding.IntegrationTests;

public sealed class PreLedgerWorkerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Workers_read_returns_before_local_capture_without_mutating_the_leased_original(bool eventWorker, bool failInventory)
    {
        var ports = new Ports(failInventory); var version = ports.Original.Version;
        if (eventWorker) Assert.True(await new PaymentEventWorker(ports, ports, ports, ports).ProcessOneAsync());
        else Assert.True(await new CollectionWorker(ports, ports, ports).ProcessOneAsync());
        Assert.Equal(1, ports.InventoryReads); Assert.Equal(version, ports.Original.Version);
        Assert.Equal(PaymentAttemptState.Dispatching, ports.Original.State); Assert.Null(ports.Original.PaymentId);
        if (!failInventory) Assert.Equal(CashObservationState.Pending, ports.Observation?.CashInventory?.State);
        else if (eventWorker) Assert.Equal(EventReferenceState.Unknown, ports.Reference?.State);
        else Assert.Equal(ProviderCollectionState.Unknown, ports.Observation?.State);
        Assert.False(ports.CompletionCancelled);
    }
    private sealed class Ports : ICollectionWorkQueue, IPaymentEventQueue, IPaymentEventResolver, ICollectionGatewayFactory, ICollectionGateway, ICashInventoryReader
    {
        public PaymentAttempt Original { get; }
        private readonly bool _fail;
        private readonly CollectionLease _lease;
        private readonly PaymentEventLease _event;
        public int InventoryReads { get; private set; }
        public ProviderCollectionObservation? Observation { get; private set; }
        public ProviderEventReference? Reference { get; private set; }
        public bool CompletionCancelled { get; private set; }
        public Ports(bool fail)
        {
            _fail = fail; var now = DateTimeOffset.UtcNow; var account = Guid.NewGuid();
            var binding = BillingCustomerBinding.Create(account, "acct_preledger", false, "cus_preledger", now);
            Original = PaymentAttempt.Admit(Guid.NewGuid(), "payer", binding, [new(Guid.NewGuid(), account, Guid.NewGuid(), 1000)], now); Original.BeginDispatch(now);
            _lease = new(Original, Guid.NewGuid(), new("https://preledger.example.test", "pmc_preledger"));
            var row = PaymentEventInbox.Receive(binding.ProviderAccount, false, "evt_preledger", "checkout.session.completed", "cs_preledger", CheckoutProfile.ApiContract, new string('A', 64), true, now);
            row.Claim(now); _event = new(row, row.LeaseToken);
        }
        Task<CollectionLease?> ICollectionWorkQueue.ClaimAsync(CancellationToken token) => Task.FromResult<CollectionLease?>(_lease);
        Task<PaymentEventLease?> IPaymentEventQueue.ClaimAsync(CancellationToken token) => Task.FromResult<PaymentEventLease?>(_event);
        public Task<ProviderEventReference> ResolveAsync(PaymentEventInbox original, CancellationToken token) => Task.FromResult(new ProviderEventReference(EventReferenceState.Known, Original.Id, Original.AccountId, Original.PayloadHash, "cs_preledger"));
        public Task<EventCollectionTarget?> LoadTargetAsync(PaymentEventLease lease, ProviderEventReference reference, CancellationToken token) => Task.FromResult<EventCollectionTarget?>(new(Original, _lease.Profile));
        public Task<bool> CompleteAsync(CollectionLease lease, ProviderCollectionObservation observation, CancellationToken token)
        { Observation = observation; CompletionCancelled = token.IsCancellationRequested; return Task.FromResult(true); }
        public Task<bool> CompleteAsync(PaymentEventLease lease, ProviderEventReference reference, EventCollectionTarget? target, ProviderCollectionObservation? observation, CancellationToken token)
        { Observation = observation; Reference = reference; CompletionCancelled = token.IsCancellationRequested; return Task.FromResult(true); }
        public ICollectionGateway Open(PaymentAttempt original, CheckoutProfile profile) => this;
        public Task<ProviderCollectionObservation> CreateAsync(PaymentAttempt original, CancellationToken token = default) => ReadAsync(original, "cs_preledger", token);
        public Task<ProviderCollectionObservation> ReadAsync(PaymentAttempt original, string sessionId, CancellationToken token = default) => Task.FromResult(new ProviderCollectionObservation(ProviderCollectionState.Paid, sessionId,
            Settlement: new(original.ProviderAccount, false, original.CustomerId, sessionId, "pi_preledger", 1000, "USD")));
        public Task<ProviderCollectionObservation> ExpireAsync(PaymentAttempt original, string sessionId, CancellationToken token = default) => throw new NotSupportedException();
        public Task<CashInventoryObservation> ReadInventoryAsync(PaymentAttempt original, CancellationToken token) => throw new InvalidOperationException("Pre-ledger calls must use the captured context.");
        public Task<CashInventoryObservation> ReadCapturedInventoryAsync(PaymentAttempt original, CanonicalSettlement capture, CancellationToken token)
        {
            InventoryReads++; Assert.Equal(PaymentAttemptState.Dispatching, original.State); Assert.Null(original.PaymentId);
            Assert.Equal("pi_preledger", CapturedPaymentEvidence.FromSettlement(original, capture).PaymentId);
            if (_fail) throw new HttpRequestException("Synthetic inventory outage");
            return Task.FromResult(new CashInventoryObservation(CashObservationState.Pending));
        }
    }
}
