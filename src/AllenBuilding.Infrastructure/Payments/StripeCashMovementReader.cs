using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using Stripe;

namespace AllenBuilding.Infrastructure.Payments;

// Retrieves real source/transaction evidence. No refund, dispute response or collection write exists here.
public sealed class StripeCashMovementReader : ICashMovementReader, ICashInventoryReader, IDisposable
{
    private readonly StripeCustomerCredentials _credentials;
    private readonly StripeClient _client;
    private readonly HttpClient? _ownedHttp;
    public StripeCashMovementReader(StripeCustomerCredentials credentials) : this(credentials, null) { }
    internal StripeCashMovementReader(StripeCustomerCredentials credentials, StripeClient? testClient)
    {
        credentials.Validate(); _credentials = new() { ApiKey = credentials.ApiKey, ProviderAccount = credentials.ProviderAccount, LiveMode = credentials.LiveMode };
        if (testClient is null) _ownedHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _client = testClient ?? new StripeClient(new StripeClientOptions
        { ApiKey = credentials.ApiKey, HttpClient = new SystemNetHttpClient(_ownedHttp, maxNetworkRetries: 0, enableTelemetry: false) });
    }
    public async Task<CashObservation> ReadAsync(PaymentAttempt original, string sourceId, CancellationToken cancellationToken)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(20)); var token = deadline.Token;
            if (!PaymentReferences.Valid(sourceId, "re_") && !CashMovement.DisputeReference(sourceId)) return Invalid();
            if (original.State != PaymentAttemptState.Settled) return Invalid();
            var evidence = CapturedPaymentEvidence.FromSettled(original);
            var charge = await LoadChargeAsync(evidence, token); if (charge is null) return Invalid();
            return await ReadSourceAsync(evidence, charge, sourceId, token);
        }
        catch (OperationCanceledException) { return new(CashObservationState.Unknown); }
        catch (HttpRequestException) { return new(CashObservationState.Unknown); }
        catch (StripeException) { return new(CashObservationState.Unknown); }
    }
    private async Task<Charge?> LoadChargeAsync(CapturedPaymentEvidence original, CancellationToken token)
    {
        if (original.ProviderAccount != _credentials.ProviderAccount || original.LiveMode != _credentials.LiveMode ||
            !PaymentReferences.Valid(original.PaymentId!, "pi_")) return null;
        if ((await new AccountService(_client).GetSelfAsync(cancellationToken: token)).Id != original.ProviderAccount) return null;
        var customer = await new CustomerService(_client).GetAsync(original.CustomerId, cancellationToken: token);
        if (customer.Id != original.CustomerId || customer.Deleted == true || customer.Livemode != original.LiveMode || customer.Metadata?.GetValueOrDefault("office_account_id") != original.AccountId.ToString("D")) return null;
        var intent = await new PaymentIntentService(_client).GetAsync(original.PaymentId, new() { Expand = ["latest_charge"] }, cancellationToken: token);
        var charge = intent.LatestCharge;
        if (intent.Id != original.PaymentId || intent.Livemode != original.LiveMode || intent.CustomerId != original.CustomerId || intent.Status != "succeeded" ||
            intent.Amount != original.TotalMinorUnits || intent.AmountReceived != original.TotalMinorUnits || intent.Currency != "usd" ||
            intent.Metadata?.GetValueOrDefault("office_account_id") != original.AccountId.ToString("D") || intent.Metadata?.GetValueOrDefault("office_attempt_id") != original.Id.ToString("D") ||
            intent.Metadata?.GetValueOrDefault("office_review_hash") != original.PayloadHash || charge is null || !PaymentReferences.Valid(charge.Id, "ch_") || charge.Id != intent.LatestChargeId ||
            charge.PaymentIntentId != original.PaymentId || charge.CustomerId != original.CustomerId || charge.Livemode != original.LiveMode || !charge.Paid || !charge.Captured ||
            charge.Amount != original.TotalMinorUnits || charge.AmountCaptured != original.TotalMinorUnits || charge.Currency != "usd") return null;
        return charge;
    }
    private async Task<CashObservation> ReadSourceAsync(CapturedPaymentEvidence original, Charge charge, string sourceId, CancellationToken token, bool allowNoCash = false)
    {
        if (PaymentReferences.Valid(sourceId, "re_"))
        {
            var refund = await new RefundService(_client).GetAsync(sourceId, cancellationToken: token);
            if (refund.Id != sourceId || refund.PaymentIntentId != original.PaymentId || refund.ChargeId != charge.Id || refund.Currency != "usd" || refund.Amount is < 1 || refund.Amount > charge.AmountCaptured) return Invalid();
            if (refund.Status != "succeeded" || refund.BalanceTransactionId is null) return new(CashObservationState.Pending);
            if (charge.AmountRefunded < refund.Amount || !PaymentReferences.Valid(refund.BalanceTransactionId, "txn_")) return Invalid();
            var transaction = await new BalanceTransactionService(_client).GetAsync(refund.BalanceTransactionId, cancellationToken: token);
            if (!MatchesTransaction(transaction, refund.BalanceTransactionId, sourceId) || transaction.Amount != -refund.Amount || transaction.Type is not ("refund" or "payment_refund")) return Invalid();
            return new(CashObservationState.Verified, [Fact(original, sourceId, CashMovementKind.Refund, transaction)]);
        }
        var dispute = await new DisputeService(_client).GetAsync(sourceId, cancellationToken: token);
        if (dispute.Id != sourceId || dispute.Livemode != original.LiveMode || dispute.PaymentIntentId != original.PaymentId || dispute.ChargeId != charge.Id || dispute.Currency != "usd" || dispute.Amount < 1 || dispute.Amount > charge.AmountCaptured) return Invalid();
        if (dispute.BalanceTransactions is not { Count: > 0 }) return new(CashObservationState.Pending);
        if (dispute.BalanceTransactions.Count > 25 || dispute.BalanceTransactions.Select(x => x.Id).Distinct().Count() != dispute.BalanceTransactions.Count) return Invalid();
        var facts = new List<CanonicalCashMovement>();
        foreach (var reference in dispute.BalanceTransactions)
        {
            if (!PaymentReferences.Valid(reference.Id, "txn_")) return Invalid();
            var transaction = await new BalanceTransactionService(_client).GetAsync(reference.Id, cancellationToken: token);
            if (!MatchesTransaction(transaction, reference.Id, sourceId) || transaction.Type != "adjustment" || transaction.Amount is < -99_999_999 or > 99_999_999 || Math.Abs(transaction.Amount) > dispute.Amount) return Invalid();
            // Provider fees are never passed through as tenant debt or a fabricated cash refund.
            if (transaction.Amount != 0) facts.Add(Fact(original, sourceId, transaction.Amount < 0 ? CashMovementKind.DisputeWithdrawal : CashMovementKind.DisputeReinstatement, transaction));
        }
        return facts.Count == 0 && !allowNoCash ? new(CashObservationState.Pending) : new(CashObservationState.Verified, facts);
    }

    public async Task<CashInventoryObservation> ReadInventoryAsync(PaymentAttempt original, CancellationToken cancellationToken)
    {
        try { return await ReadInventoryCoreAsync(CapturedPaymentEvidence.FromSettled(original), cancellationToken); }
        catch (ArgumentException) { return new(CashObservationState.Invalid); }
    }
    public async Task<CashInventoryObservation> ReadCapturedInventoryAsync(PaymentAttempt original, CanonicalSettlement settlement, CancellationToken cancellationToken)
    {
        try { return await ReadInventoryCoreAsync(CapturedPaymentEvidence.FromSettlement(original, settlement), cancellationToken); }
        catch (ArgumentException) { return new(CashObservationState.Invalid); }
    }
    private async Task<CashInventoryObservation> ReadInventoryCoreAsync(CapturedPaymentEvidence original, CancellationToken cancellationToken)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(20)); var token = deadline.Token;
            var charge = await LoadChargeAsync(original, token); if (charge is null) return new(CashObservationState.Invalid);
            var refunds = await new RefundService(_client).ListAsync(new() { Charge = charge.Id, Limit = 26 }, cancellationToken: token);
            var disputes = await new DisputeService(_client).ListAsync(new() { Charge = charge.Id, Limit = 26 }, cancellationToken: token);
            if (!Bounded(refunds) || !Bounded(disputes) || refunds.Data.Count + disputes.Data.Count > 25 ||
                refunds.Data.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != refunds.Data.Count || disputes.Data.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != disputes.Data.Count)
                return new(CashObservationState.Invalid);
            var facts = new List<CanonicalCashMovement>(); var sources = new List<string>(); long refunded = 0;
            foreach (var refund in refunds.Data)
            {
                if (!PaymentReferences.Valid(refund.Id, "re_") || refund.PaymentIntentId != original.PaymentId || refund.ChargeId != charge.Id || refund.Currency != "usd" || refund.Amount < 1 || refund.Amount > charge.AmountCaptured) return new(CashObservationState.Invalid);
                sources.Add(refund.Id);
                if (refund.Status is "pending" or "requires_action") return new(CashObservationState.Pending);
                if (refund.Status is "failed" or "canceled")
                {
                    // A failed refund can have both withdrawal and reversal transactions; do not treat it as zero cash without reconciliation.
                    if (refund.BalanceTransactionId is not null || refund.FailureBalanceTransactionId is not null) return new(CashObservationState.Invalid);
                    continue;
                }
                if (refund.Status != "succeeded") return new(CashObservationState.Invalid);
                var observed = await ReadSourceAsync(original, charge, refund.Id, token);
                if (observed.State != CashObservationState.Verified) return new(observed.State);
                if (observed.Movements is not { Count: 1 } || observed.Movements[0].TransactionId != refund.BalanceTransactionId || observed.Movements[0].SignedMinorUnits != -refund.Amount) return new(CashObservationState.Invalid);
                facts.AddRange(observed.Movements); refunded = checked(refunded + refund.Amount);
            }
            if (refunded != charge.AmountRefunded || refunded > charge.AmountCaptured || (charge.Disputed && disputes.Data.Count == 0)) return new(CashObservationState.Invalid);
            foreach (var dispute in disputes.Data)
            {
                if (!CashMovement.DisputeReference(dispute.Id) || dispute.Livemode != original.LiveMode || dispute.PaymentIntentId != original.PaymentId || dispute.ChargeId != charge.Id || dispute.Currency != "usd" || dispute.Amount < 1 || dispute.Amount > charge.AmountCaptured) return new(CashObservationState.Invalid);
                sources.Add(dispute.Id);
                if (dispute.Status is "needs_response" or "under_review" or "warning_needs_response" or "warning_under_review") return new(CashObservationState.Pending);
                if (dispute.Status is not ("won" or "lost" or "warning_closed" or "prevented")) return new(CashObservationState.Invalid);
                if (dispute.BalanceTransactions is null || dispute.BalanceTransactions.Count > 25) return new(CashObservationState.Invalid);
                if (dispute.BalanceTransactions.Count == 0)
                {
                    if (dispute.Status is "won" or "lost") return new(CashObservationState.Pending);
                    continue;
                }
                var observed = await ReadSourceAsync(original, charge, dispute.Id, token, allowNoCash: true);
                if (observed.State != CashObservationState.Verified) return new(observed.State);
                if (observed.Movements is null || observed.Movements.Any(x => !dispute.BalanceTransactions.Any(t => t.Id == x.TransactionId))) return new(CashObservationState.Invalid);
                var net = observed.Movements.Sum(x => x.SignedMinorUnits);
                if (dispute.Status == "lost" ? net != -dispute.Amount : net != 0) return new(CashObservationState.Pending);
                facts.AddRange(observed.Movements);
            }
            if (facts.Count > 25 || facts.Select(x => x.TransactionId).Distinct(StringComparer.Ordinal).Count() != facts.Count) return new(CashObservationState.Invalid);
            // Re-read the original and full catalogs. Changes during retrieval retain the hold; no truncated embedded refund list is trusted.
            var after = await LoadChargeAsync(original, token);
            var afterRefunds = await new RefundService(_client).ListAsync(new() { Charge = charge.Id, Limit = 26 }, cancellationToken: token);
            var afterDisputes = await new DisputeService(_client).ListAsync(new() { Charge = charge.Id, Limit = 26 }, cancellationToken: token);
            if (after is null || after.Id != charge.Id || after.AmountRefunded != charge.AmountRefunded || after.Disputed != charge.Disputed ||
                !Bounded(afterRefunds) || !Bounded(afterDisputes) || RefundCatalog(refunds) != RefundCatalog(afterRefunds) || DisputeCatalog(disputes) != DisputeCatalog(afterDisputes)) return new(CashObservationState.Pending);
            return new(CashObservationState.Verified, new(original.ProviderAccount, original.LiveMode, original.AccountId, original.Id, original.CustomerId, original.PaymentId!, charge.Id,
                charge.AmountCaptured, refunded, sources.Order(StringComparer.Ordinal).ToArray(), facts.OrderBy(x => x.TransactionId, StringComparer.Ordinal).ToArray(), DateTimeOffset.UtcNow));
        }
        catch (OperationCanceledException) { return new(CashObservationState.Unknown); }
        catch (HttpRequestException) { return new(CashObservationState.Unknown); }
        catch (StripeException) { return new(CashObservationState.Unknown); }
        catch (OverflowException) { return new(CashObservationState.Invalid); }
    }
    private static bool Bounded<T>(StripeList<T> list) where T : IHasId => list is not null && !list.HasMore && list.Data is { Count: <= 25 } && list.Data.All(x => x is not null);
    private static string RefundCatalog(StripeList<Refund> list) => System.Text.Json.JsonSerializer.Serialize(list.Data.OrderBy(x => x.Id, StringComparer.Ordinal)
        .Select(x => new { x.Id, x.ChargeId, x.PaymentIntentId, x.Amount, x.Currency, x.Status, x.BalanceTransactionId, x.FailureBalanceTransactionId }));
    private static string DisputeCatalog(StripeList<Dispute> list) => System.Text.Json.JsonSerializer.Serialize(list.Data.OrderBy(x => x.Id, StringComparer.Ordinal)
        .Select(x => new { x.Id, x.ChargeId, x.PaymentIntentId, x.Amount, x.Currency, x.Status, x.Livemode, Transactions = x.BalanceTransactions?.Select(t => t.Id).Order(StringComparer.Ordinal).ToArray() }));
    private static bool MatchesTransaction(BalanceTransaction transaction, string id, string source) => transaction.Id == id && transaction.SourceId == source && transaction.Currency == "usd" &&
        transaction.Status is "available" or "pending" && transaction.ExchangeRate is null && transaction.BalanceType is null or "payments";
    private static CanonicalCashMovement Fact(CapturedPaymentEvidence original, string source, CashMovementKind kind, BalanceTransaction transaction) => new(original.ProviderAccount, original.LiveMode,
        original.AccountId, original.Id, original.CustomerId, original.PaymentId!, source, transaction.Id, kind, transaction.Amount, "USD", new DateTimeOffset(transaction.Created.ToUniversalTime()));
    private static CashObservation Invalid() => new(CashObservationState.Invalid);
    public void Dispose() => _ownedHttp?.Dispose();
}
