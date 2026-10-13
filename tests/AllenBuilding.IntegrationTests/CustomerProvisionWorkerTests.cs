using AllenBuilding.Application.Access;
using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;

namespace AllenBuilding.IntegrationTests;

public sealed class CustomerProvisionWorkerTests
{
    [Fact]
    public async Task Shutdown_retains_unknown_creation_and_completes_with_an_independent_budget()
    {
        using var shutdown = new CancellationTokenSource(); var fake = new Interrupted(shutdown);
        Assert.True(await new CustomerProvisionWorker(fake, fake).ProcessOneAsync(shutdown.Token));
        Assert.Equal(CustomerProvisionOutcome.Unknown, fake.Result?.Outcome); Assert.False(fake.CompletionCancelled); Assert.True(fake.Disposed);
    }
    [Fact]
    public async Task Disabled_or_unchallenged_admission_never_touches_the_customer_store()
    {
        var current = new TestActor { Actor = new("payer", new HashSet<Guid> { Guid.NewGuid() }, false, false) }; var store = new UntouchedStore();
        Assert.Equal(CollectionCode.Unavailable, (await new CustomerProvisionActions(current, store, new(false, "acct_customers", false)).EnsureAsync(Guid.NewGuid())).Code);
        Assert.Equal(CollectionCode.Unavailable, (await new CustomerProvisionActions(current, store, new(true, "acct_customers", false)).EnsureAsync(Guid.NewGuid())).Code);
        current.Actor = current.Actor with { ChallengedMfa = true };
        Assert.Equal(CollectionCode.ValidationFailed, (await new CustomerProvisionActions(current, store, new(true, "acct_customers", false)).EnsureAsync(Guid.Empty)).Code);
    }
    private sealed class UntouchedStore : ICustomerProvisionStore
    { public Task<CustomerProvisionResult> EnsureAsync(Actor actor, Guid accountId, CancellationToken cancellationToken) => throw new InvalidOperationException("Admission must not reach storage."); }
    private sealed class Interrupted : ICustomerProvisionQueue, ICustomerProvisionGatewayFactory, ICustomerProvisionGateway, IDisposable
    {
        private readonly CancellationTokenSource _shutdown;
        private readonly CustomerProvisionLease _lease;
        public CustomerObservation? Result { get; private set; }
        public bool CompletionCancelled { get; private set; }
        public bool Disposed { get; private set; }
        public Interrupted(CancellationTokenSource shutdown)
        {
            _shutdown = shutdown; var now = DateTimeOffset.UtcNow;
            var original = CustomerProvision.Enqueue(Guid.NewGuid(), "payer", "acct_customers", false, now); original.Claim(now); original.BeginDispatch(now);
            _lease = new(original, original.LeaseToken);
        }
        public Task<CustomerProvisionLease?> ClaimAsync(CancellationToken cancellationToken) => Task.FromResult<CustomerProvisionLease?>(_lease);
        public Task<bool> CompleteAsync(CustomerProvisionLease lease, CustomerObservation observation, CancellationToken cancellationToken)
        { Assert.Equal(_lease.Token, lease.Token); Result = observation; CompletionCancelled = cancellationToken.IsCancellationRequested; return Task.FromResult(true); }
        public ICustomerProvisionGateway Open(CustomerProvision original) => this;
        public Task<CustomerObservation> CreateAsync(CustomerProvision original, CancellationToken cancellationToken)
        { _shutdown.Cancel(); throw new OperationCanceledException(cancellationToken); }
        public void Dispose() => Disposed = true;
    }
}
