using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using Stripe;

namespace AllenBuilding.Infrastructure.Payments;

// Retrieves real source/transaction evidence. No refund, dispute response or collection write exists here.
public sealed class StripeCashMovementReader : ICashMovementReader, IDisposable
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
            if (original.State != PaymentAttemptState.Settled || original.ProviderAccount != _credentials.ProviderAccount || original.LiveMode != _credentials.LiveMode ||
                !PaymentReferences.Valid(original.PaymentId!, "pi_") || (!PaymentReferences.Valid(sourceId, "re_") && !CashMovement.DisputeReference(sourceId))) return Invalid();
            if ((await new AccountService(_client).GetSelfAsync(cancellationToken: token)).Id != original.ProviderAccount) return Invalid();
            var customer = await new CustomerService(_client).GetAsync(original.CustomerId, cancellationToken: token);
            if (customer.Id != original.CustomerId || customer.Deleted == true || customer.Livemode != original.LiveMode || customer.Metadata?.GetValueOrDefault("office_account_id") != original.AccountId.ToString("D")) return Invalid();
            var intent = await new PaymentIntentService(_client).GetAsync(original.PaymentId, new() { Expand = ["latest_charge"] }, cancellationToken: token);
            var charge = intent.LatestCharge;
            if (intent.Id != original.PaymentId || intent.Livemode != original.LiveMode || intent.CustomerId != original.CustomerId || intent.Status != "succeeded" ||
                intent.Amount != original.TotalMinorUnits || intent.AmountReceived != original.TotalMinorUnits || intent.Currency != "usd" ||
                intent.Metadata?.GetValueOrDefault("office_account_id") != original.AccountId.ToString("D") || intent.Metadata?.GetValueOrDefault("office_attempt_id") != original.Id.ToString("D") ||
                intent.Metadata?.GetValueOrDefault("office_review_hash") != original.PayloadHash || charge is null || !PaymentReferences.Valid(charge.Id, "ch_") || charge.Id != intent.LatestChargeId ||
                charge.PaymentIntentId != original.PaymentId || charge.CustomerId != original.CustomerId || charge.Livemode != original.LiveMode || !charge.Paid || !charge.Captured ||
                charge.Amount != original.TotalMinorUnits || charge.AmountCaptured != original.TotalMinorUnits || charge.Currency != "usd") return Invalid();
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
            return facts.Count == 0 ? new(CashObservationState.Pending) : new(CashObservationState.Verified, facts);
        }
        catch (OperationCanceledException) { return new(CashObservationState.Unknown); }
        catch (HttpRequestException) { return new(CashObservationState.Unknown); }
        catch (StripeException) { return new(CashObservationState.Unknown); }
    }
    private static bool MatchesTransaction(BalanceTransaction transaction, string id, string source) => transaction.Id == id && transaction.SourceId == source && transaction.Currency == "usd" &&
        transaction.Status is "available" or "pending" && transaction.ExchangeRate is null && transaction.BalanceType is null or "payments";
    private static CanonicalCashMovement Fact(PaymentAttempt original, string source, CashMovementKind kind, BalanceTransaction transaction) => new(original.ProviderAccount, original.LiveMode,
        original.AccountId, original.Id, original.CustomerId, original.PaymentId!, source, transaction.Id, kind, transaction.Amount, "USD", new DateTimeOffset(transaction.Created.ToUniversalTime()));
    private static CashObservation Invalid() => new(CashObservationState.Invalid);
    public void Dispose() => _ownedHttp?.Dispose();
}
