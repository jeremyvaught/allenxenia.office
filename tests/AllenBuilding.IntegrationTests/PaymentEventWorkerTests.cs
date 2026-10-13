using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;

namespace AllenBuilding.IntegrationTests;

public sealed class PaymentEventWorkerTests
{
    [Fact]
    public async Task Interrupted_lookup_completes_as_unknown_with_an_independent_shutdown_budget()
    {
        using var shutdown = new CancellationTokenSource(); var work = new Interrupted(shutdown);
        Assert.True(await new PaymentEventWorker(work, work, work).ProcessOneAsync(shutdown.Token));
        Assert.True(shutdown.IsCancellationRequested); Assert.Equal(EventReferenceState.Unknown, work.Result?.State);
        Assert.False(work.CompletionCancelled); Assert.Equal(0, work.TargetLoads);
    }
    private sealed class Interrupted(CancellationTokenSource shutdown) : IPaymentEventQueue, IPaymentEventResolver, ICollectionGatewayFactory
    {
        public ProviderEventReference? Result { get; private set; }
        public bool CompletionCancelled { get; private set; }
        public int TargetLoads { get; private set; }
        public Task<PaymentEventLease?> ClaimAsync(CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow; var row = PaymentEventInbox.Receive("acct_event", false, "evt_cancel", "checkout.session.completed", "cs_cancel", CheckoutProfile.ApiContract, new string('A', 64), true, now);
            row.Claim(now); return Task.FromResult<PaymentEventLease?>(new(row, row.LeaseToken));
        }
        public Task<ProviderEventReference> ResolveAsync(PaymentEventInbox original, CancellationToken cancellationToken) { shutdown.Cancel(); throw new OperationCanceledException(cancellationToken); }
        public Task<EventCollectionTarget?> LoadTargetAsync(PaymentEventLease lease, ProviderEventReference reference, CancellationToken cancellationToken) { TargetLoads++; throw new InvalidOperationException("Unverified lookup has no target."); }
        public Task<bool> CompleteAsync(PaymentEventLease lease, ProviderEventReference reference, EventCollectionTarget? target, ProviderCollectionObservation? observation, CancellationToken cancellationToken)
        { Result = reference; CompletionCancelled = cancellationToken.IsCancellationRequested; Assert.Null(target); Assert.Null(observation); return Task.FromResult(true); }
        public ICollectionGateway Open(PaymentAttempt original, CheckoutProfile profile) => throw new InvalidOperationException("Unverified lookup cannot open a provider adapter.");
    }
}
