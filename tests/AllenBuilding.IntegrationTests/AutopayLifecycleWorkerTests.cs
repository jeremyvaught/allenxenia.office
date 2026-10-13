using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;

namespace AllenBuilding.IntegrationTests;

public sealed class AutopayLifecycleWorkerTests
{
    [Theory]
    [InlineData(AutopayWorkStage.Activate, false)]
    [InlineData(AutopayWorkStage.Activate, true)]
    [InlineData(AutopayWorkStage.Stop, false)]
    [InlineData(AutopayWorkStage.Stop, true)]
    public async Task Original_stage_writes_and_read_only_investigation_have_distinct_ports(AutopayWorkStage stage, bool readOnly)
    {
        var probe = new Probe(stage, readOnly); var key = probe.Lease.Original.ProviderOperationKey;
        Assert.True(await new AutopayLifecycleWorker(probe, probe, probe).ProcessOneAsync());
        Assert.Equal(readOnly ? 1 : 0, probe.Reads); Assert.Equal(!readOnly && stage == AutopayWorkStage.Activate ? 1 : 0, probe.Activations);
        Assert.Equal(!readOnly && stage == AutopayWorkStage.Stop ? 1 : 0, probe.Stops); Assert.Equal(key, probe.Lease.Original.ProviderOperationKey); Assert.True(probe.Disposed);
    }
    [Fact]
    public async Task Expired_window_reclaimed_fence_reads_only_and_expired_lease_never_opens_a_gateway()
    {
        var old = new Probe(AutopayWorkStage.Activate, false); old.Now = old.Now.AddHours(23); Assert.True(old.Lease.Original.Claim(old.Now)); old.Lease = old.Lease with { Token = old.Lease.Original.LeaseToken };
        Assert.True(await new AutopayLifecycleWorker(old, old, old).ProcessOneAsync()); Assert.Equal(1, old.Reads); Assert.Equal(0, old.Activations);
        var expired = new Probe(AutopayWorkStage.Stop, false); expired.Now = expired.Now.AddMinutes(2);
        Assert.True(await new AutopayLifecycleWorker(expired, expired, expired).ProcessOneAsync()); Assert.Equal(0, expired.Opened); Assert.Equal(AutopayLifecycleOutcome.Unknown, expired.Result!.Outcome);
    }
    [Fact]
    public async Task Shutdown_during_provider_call_still_persists_unknown_with_an_independent_completion_token()
    {
        using var shutdown = new CancellationTokenSource(); var probe = new Probe(AutopayWorkStage.Stop, false) { Shutdown = shutdown };
        Assert.True(await new AutopayLifecycleWorker(probe, probe, probe).ProcessOneAsync(shutdown.Token)); Assert.True(shutdown.IsCancellationRequested);
        Assert.Equal(AutopayLifecycleOutcome.Unknown, probe.Result!.Outcome); Assert.False(probe.CompletionCancelled); Assert.True(probe.Disposed);
    }
    private sealed class Probe : TimeProvider, IAutopayLifecycleQueue, IAutopayLifecycleGatewayFactory, IAutopayLifecycleGateway, IDisposable
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        public AutopayLifecycleLease Lease { get; set; }
        public int Opened { get; private set; }
        public int Activations { get; private set; }
        public int Stops { get; private set; }
        public int Reads { get; private set; }
        public bool Disposed { get; private set; }
        public bool CompletionCancelled { get; private set; }
        public CancellationTokenSource? Shutdown { get; init; }
        public AutopayLifecycleObservation? Result { get; private set; }
        public Probe(AutopayWorkStage stage, bool readOnly)
        {
            var account = RentAccount.Open(Guid.NewGuid(), Guid.NewGuid()); var binding = BillingCustomerBinding.Create(account.Id, "acct_lifecycle_worker", false, "cus_lifecycle_worker", Now);
            var terms = RentAgreement.Create(account.Id, new Money(100000), 1, new(2026, 1, 1), null, "payer", Now);
            var auth = AutopayAuthorization.Consent(Guid.NewGuid(), "payer", account, binding, AutopaySchedule.Select(terms, Now, []), true, Now);
            auth.ObserveVerifiedSetup(new(binding.ProviderAccount, false, account.Id, binding.CustomerId, auth.ConsentHash, "cs_worker_setup", Now.AddHours(24), AutopaySetupState.Ready, "pm_worker", true, true, Now), true);
            if (stage == AutopayWorkStage.Stop) auth.RequestStop(Guid.NewGuid(), "payer", AutopayStopTiming.Immediately, Now);
            var work = AutopayWork.Enqueue(auth, stage, Now, new("https://worker.example.test", "pmc_worker")); work.Claim(Now); work.BeginDispatch(Now);
            Lease = new(work, auth, work.LeaseToken, readOnly);
        }
        public override DateTimeOffset GetUtcNow() => Now;
        public Task<AutopayLifecycleLease?> ClaimAsync(CancellationToken cancellationToken) => Task.FromResult<AutopayLifecycleLease?>(Lease);
        public Task<bool> CompleteAsync(AutopayLifecycleLease lease, AutopayLifecycleObservation observation, CancellationToken cancellationToken) { Result = observation; CompletionCancelled = cancellationToken.IsCancellationRequested; return Task.FromResult(true); }
        public IAutopayLifecycleGateway Open(AutopayLifecycleLease lease) { Opened++; return this; }
        public Task<AutopayLifecycleObservation> ActivateAsync(AutopayLifecycleLease lease, CancellationToken cancellationToken) { Activations++; return Observe(cancellationToken); }
        public Task<AutopayLifecycleObservation> StopAsync(AutopayLifecycleLease lease, CancellationToken cancellationToken) { Stops++; return Observe(cancellationToken); }
        public Task<AutopayLifecycleObservation> ReadAsync(AutopayLifecycleLease lease, CancellationToken cancellationToken) { Reads++; return Observe(cancellationToken); }
        private Task<AutopayLifecycleObservation> Observe(CancellationToken token) { if (Shutdown is not null) { Shutdown.Cancel(); throw new OperationCanceledException(token); } return Task.FromResult(new AutopayLifecycleObservation(AutopayLifecycleOutcome.Unknown)); }
        public void Dispose() => Disposed = true;
    }
}
