using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;

namespace AllenBuilding.IntegrationTests;

public sealed class AutopaySetupWorkerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_only_work_cannot_call_create_and_original_keys_are_preserved(bool readOnly)
    {
        var fake = new Probe(readOnly); var key = fake.Lease.Original.ProviderOperationKey;
        Assert.True(await new AutopaySetupWorker(fake, fake, fake).ProcessOneAsync()); Assert.Equal(readOnly ? 0 : 1, fake.Creates); Assert.Equal(readOnly ? 1 : 0, fake.Reads); Assert.Equal(key, fake.Lease.Original.ProviderOperationKey); Assert.True(fake.Disposed);
    }
    [Fact]
    public async Task A_write_window_that_expires_after_claim_still_uses_read_only_investigation()
    {
        var fake = new Probe(false); fake.Now = fake.Now.AddHours(23); Assert.True(fake.Lease.Original.Claim(fake.Now)); fake.Lease = fake.Lease with { Token = fake.Lease.Original.LeaseToken };
        Assert.True(await new AutopaySetupWorker(fake, fake, fake).ProcessOneAsync()); Assert.Equal(0, fake.Creates); Assert.Equal(1, fake.Reads);
    }
    [Fact]
    public async Task An_expired_lease_cannot_open_a_provider_and_shutdown_completion_has_an_independent_budget()
    {
        var expired = new Probe(false); expired.Now = expired.Now.AddMinutes(2); Assert.True(await new AutopaySetupWorker(expired, expired, expired).ProcessOneAsync()); Assert.Equal(0, expired.Opened); Assert.Equal(AutopaySetupOutcome.Unknown, expired.Result!.Outcome);
        using var shutdown = new CancellationTokenSource(); var interrupted = new Probe(false) { Shutdown = shutdown };
        Assert.True(await new AutopaySetupWorker(interrupted, interrupted, interrupted).ProcessOneAsync(shutdown.Token)); Assert.Equal(AutopaySetupOutcome.Unknown, interrupted.Result!.Outcome); Assert.False(interrupted.CompletionCancelled); Assert.True(interrupted.Disposed);
    }
    private sealed class Probe : TimeProvider, IAutopaySetupQueue, IAutopaySetupGatewayFactory, IAutopaySetupGateway, IDisposable
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        public AutopaySetupLease Lease { get; set; }
        public int Creates { get; private set; }
        public int Reads { get; private set; }
        public int Opened { get; private set; }
        public bool Disposed { get; private set; }
        public bool CompletionCancelled { get; private set; }
        public AutopaySetupObservation? Result { get; private set; }
        public CancellationTokenSource? Shutdown { get; init; }
        public Probe(bool readOnly)
        {
            var account = RentAccount.Open(Guid.NewGuid(), Guid.NewGuid()); var binding = BillingCustomerBinding.Create(account.Id, "acct_worker", false, "cus_worker", Now); var terms = RentAgreement.Create(account.Id, new Money(100000), 1, new(2026, 1, 1), null, "payer", Now);
            var auth = AutopayAuthorization.Consent(Guid.NewGuid(), "payer", account, binding, AutopaySchedule.Select(terms, Now, []), true, Now);
            var work = AutopayWork.Enqueue(auth, AutopayWorkStage.Setup, Now, new("https://worker.example.test", "pmc_worker")); work.Claim(Now); work.BeginDispatch(Now); Lease = new(work, auth, work.LeaseToken, readOnly);
        }
        public override DateTimeOffset GetUtcNow() => Now;
        public Task<AutopaySetupLease?> ClaimAsync(CancellationToken cancellationToken) => Task.FromResult<AutopaySetupLease?>(Lease);
        public Task<bool> CompleteAsync(AutopaySetupLease lease, AutopaySetupObservation observation, CancellationToken cancellationToken) { Result = observation; CompletionCancelled = cancellationToken.IsCancellationRequested; return Task.FromResult(true); }
        public IAutopaySetupGateway Open(AutopaySetupLease lease) { Opened++; return this; }
        public Task<AutopaySetupObservation> CreateAsync(AutopaySetupLease lease, CancellationToken cancellationToken) { Creates++; if (Shutdown is not null) { Shutdown.Cancel(); throw new OperationCanceledException(cancellationToken); } return Task.FromResult(new AutopaySetupObservation(AutopaySetupOutcome.Unknown)); }
        public Task<AutopaySetupObservation> ReadAsync(AutopaySetupLease lease, CancellationToken cancellationToken) { Reads++; return Task.FromResult(new AutopaySetupObservation(AutopaySetupOutcome.Unknown)); }
        public void Dispose() => Disposed = true;
    }
}
