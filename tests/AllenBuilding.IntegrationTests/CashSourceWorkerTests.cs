using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;

namespace AllenBuilding.IntegrationTests;

public sealed class CashSourceWorkerTests
{
    [Fact]
    public async Task Shutdown_or_transport_failure_finishes_with_an_independent_unknown_completion()
    {
        using var shutdown = new CancellationTokenSource(); var queue = new Queue(); var reader = new Reader((_, _) => { shutdown.Cancel(); throw new OperationCanceledException(); });
        Assert.True(await new CashSourceWorker(queue, reader).ProcessOneAsync(shutdown.Token));
        Assert.Equal(CashObservationState.Unknown, queue.Observed?.State); Assert.False(queue.CompletionCancelled);
        var failed = new Queue(); Assert.True(await new CashSourceWorker(failed, new Reader((_, _) => throw new HttpRequestException("private-provider-detail"))).ProcessOneAsync());
        Assert.Equal(CashObservationState.Unknown, failed.Observed?.State);
    }
    [Fact]
    public async Task Unsupported_frozen_api_never_calls_the_provider()
    {
        var queue = new Queue(); queue.Lease = queue.Lease with { ApiVersion = "unsupported" };
        var reader = new Reader((_, _) => throw new InvalidOperationException("Provider must not run"));
        Assert.True(await new CashSourceWorker(queue, reader).ProcessOneAsync()); Assert.Equal(0, reader.Calls); Assert.Equal(CashObservationState.Invalid, queue.Observed?.State);
    }
    [Fact]
    public async Task Another_source_or_account_cannot_be_substituted_by_a_reader()
    {
        foreach (var mismatch in new[] { "source", "account", "customer", "empty", "valid" })
        {
            var queue = new Queue(); var original = queue.Lease.Original;
            var fact = new CanonicalCashMovement(original.ProviderAccount, false, original.AccountId, original.Id, original.CustomerId, original.PaymentId!, queue.Lease.SourceId, "txn_source", CashMovementKind.Refund, -200, "USD", DateTimeOffset.UtcNow);
            fact = mismatch switch { "source" => fact with { SourceId = "re_other" }, "account" => fact with { AccountId = Guid.NewGuid() }, "customer" => fact with { CustomerId = "cus_other" }, _ => fact };
            var reader = new Reader((_, _) => Task.FromResult(new CashObservation(CashObservationState.Verified, mismatch == "empty" ? [] : [fact])));
            Assert.True(await new CashSourceWorker(queue, reader).ProcessOneAsync()); Assert.Equal(mismatch == "valid" ? CashObservationState.Verified : CashObservationState.Invalid, queue.Observed?.State);
            if (mismatch == "valid") Assert.Equal(fact, queue.Observed!.Movements!.Single());
        }
    }
    [Fact]
    public async Task Known_source_is_passed_exactly_and_a_lost_completion_does_not_repeat_the_read()
    {
        var queue = new Queue { Accepted = false }; var reader = new Reader((_, _) => Task.FromResult(new CashObservation(CashObservationState.Pending)));
        Assert.True(await new CashSourceWorker(queue, reader).ProcessOneAsync()); Assert.Equal(1, reader.Calls); Assert.Equal(queue.Lease.SourceId, reader.Source);
        Assert.Equal(CashObservationState.Pending, queue.Observed?.State); Assert.False(queue.CompletionCancelled);
        var empty = new Queue { Empty = true }; Assert.False(await new CashSourceWorker(empty, reader).ProcessOneAsync()); Assert.Equal(1, reader.Calls);
    }
    private sealed class Reader(Func<PaymentAttempt, CancellationToken, Task<CashObservation>> read) : ICashMovementReader
    {
        public int Calls { get; private set; }
        public string? Source { get; private set; }
        public Task<CashObservation> ReadAsync(PaymentAttempt original, string sourceId, CancellationToken cancellationToken)
        { Calls++; Source = sourceId; return read(original, cancellationToken); }
    }
    private sealed class Queue : ICashSourceQueue
    {
        public CashSourceLease Lease { get; set; }
        public CashObservation? Observed { get; private set; }
        public bool CompletionCancelled { get; private set; }
        public bool Accepted { get; set; } = true;
        public bool Empty { get; set; }
        public Queue()
        {
            var now = DateTimeOffset.UtcNow; var account = Guid.NewGuid(); var binding = BillingCustomerBinding.Create(account, "acct_source", false, "cus_source", now);
            var original = PaymentAttempt.Admit(Guid.NewGuid(), "payer", binding, [new(Guid.NewGuid(), account, Guid.NewGuid(), 1000)], now); original.BeginDispatch(now);
            SettlementPayment.Record(original, new(binding.ProviderAccount, false, binding.CustomerId, "cs_source", "pi_source", 1000, "USD"), now);
            Lease = new(Guid.NewGuid(), original, "re_source", CheckoutProfile.ApiContract, Guid.NewGuid(), Guid.NewGuid());
        }
        public Task<CashSourceLease?> ClaimAsync(CancellationToken cancellationToken) => Task.FromResult(Empty ? null : Lease);
        public Task<bool> CompleteAsync(CashSourceLease lease, CashObservation observation, CancellationToken cancellationToken)
        { Assert.Equal(Lease.Token, lease.Token); Observed = observation; CompletionCancelled = cancellationToken.IsCancellationRequested; return Task.FromResult(Accepted); }
    }
}
