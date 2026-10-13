using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Infrastructure.Payments;
using Stripe;

namespace AllenBuilding.IntegrationTests;

public sealed class StripeCashMovementTests
{
    [Fact]
    public async Task Refund_import_uses_verified_gross_cash_and_never_passes_provider_fees_to_tenant_debt()
    {
        using var fixture = new Fixture(); var result = await fixture.Reader.ReadAsync(fixture.Original, "re_cash", CancellationToken.None);
        Assert.Equal(CashObservationState.Verified, result.State); Assert.Equal(-200, result.Movements!.Single().SignedMinorUnits);
        Assert.Equal("txn_refund", result.Movements!.Single().TransactionId); Assert.All(fixture.Requests, x => Assert.Equal("GET", x.Method));
        Assert.Contains(fixture.Requests, x => x.Path.StartsWith("/v1/balance_transactions/txn_refund", StringComparison.Ordinal));
    }
    [Theory]
    [InlineData("pending")]
    [InlineData("failed")]
    [InlineData("canceled")]
    public async Task An_uncompleted_refund_is_pending_without_a_money_fact(string status)
    {
        using var fixture = new Fixture(); fixture.Refund["status"] = status;
        var result = await fixture.Reader.ReadAsync(fixture.Original, "re_cash", CancellationToken.None); Assert.Equal(CashObservationState.Pending, result.State); Assert.Null(result.Movements);
        Assert.DoesNotContain(fixture.Requests, x => x.Path.StartsWith("/v1/balance_transactions/", StringComparison.Ordinal));
    }
    [Fact]
    public async Task A_won_label_cannot_invent_reinstatement_without_a_linked_credit_transaction()
    {
        using var fixture = new Fixture(); fixture.Dispute["status"] = "won";
        var before = await fixture.Reader.ReadAsync(fixture.Original, "du_cash", CancellationToken.None);
        Assert.Equal(CashMovementKind.DisputeWithdrawal, before.Movements!.Single().Kind); Assert.Equal(-1000, before.Movements!.Single().SignedMinorUnits);
        ((JsonArray)fixture.Dispute["balance_transactions"]!).Add(fixture.Transactions["txn_restored"].DeepClone());
        var after = await fixture.Reader.ReadAsync(fixture.Original, "du_cash", CancellationToken.None);
        Assert.Equal(2, after.Movements!.Count); Assert.Equal(0, after.Movements.Sum(x => x.SignedMinorUnits));
        Assert.Contains(after.Movements, x => x.Kind == CashMovementKind.DisputeReinstatement && x.SignedMinorUnits == 1000);
    }
    [Theory]
    [InlineData("merchant")]
    [InlineData("customer")]
    [InlineData("customer-mode")]
    [InlineData("customer-account")]
    [InlineData("intent-mode")]
    [InlineData("intent-owner")]
    [InlineData("intent-review")]
    [InlineData("intent-received")]
    [InlineData("charge-capture")]
    [InlineData("charge-owner")]
    [InlineData("refund-charge")]
    [InlineData("refund-payment")]
    [InlineData("refund-currency")]
    [InlineData("refund-amount")]
    [InlineData("transaction-id")]
    [InlineData("transaction-source")]
    [InlineData("transaction-currency")]
    [InlineData("transaction-amount")]
    [InlineData("transaction-type")]
    [InlineData("transaction-balance")]
    [InlineData("transaction-fx")]
    public async Task Foreign_or_mismatched_cash_evidence_cannot_change_the_ledger(string mutation)
    {
        using var fixture = new Fixture(); var transaction = fixture.Transactions["txn_refund"];
        switch (mutation)
        {
            case "merchant": fixture.Merchant = "acct_wrong"; break;
            case "customer": fixture.Customer["id"] = "cus_wrong"; break;
            case "customer-mode": fixture.Customer["livemode"] = true; break;
            case "customer-account": fixture.Customer["metadata"]!["office_account_id"] = Guid.NewGuid().ToString("D"); break;
            case "intent-mode": fixture.Intent["livemode"] = true; break;
            case "intent-owner": fixture.Intent["customer"] = "cus_other"; break;
            case "intent-review": fixture.Intent["metadata"]!["office_review_hash"] = "wrong"; break;
            case "intent-received": fixture.Intent["amount_received"] = 999; break;
            case "charge-capture": fixture.Charge["captured"] = false; break;
            case "charge-owner": fixture.Charge["customer"] = "cus_other"; break;
            case "refund-charge": fixture.Refund["charge"] = "ch_other"; break;
            case "refund-payment": fixture.Refund["payment_intent"] = "pi_other"; break;
            case "refund-currency": fixture.Refund["currency"] = "eur"; break;
            case "refund-amount": fixture.Refund["amount"] = 1001; break;
            case "transaction-id": transaction["id"] = "txn_other"; break;
            case "transaction-source": transaction["source"] = "re_other"; break;
            case "transaction-currency": transaction["currency"] = "eur"; break;
            case "transaction-amount": transaction["amount"] = -230; break;
            case "transaction-type": transaction["type"] = "payout"; break;
            case "transaction-balance": transaction["balance_type"] = "refund_and_dispute_prefunding"; break;
            case "transaction-fx": transaction["exchange_rate"] = 1.1; break;
        }
        var result = await fixture.Reader.ReadAsync(fixture.Original, "re_cash", CancellationToken.None); Assert.Equal(CashObservationState.Invalid, result.State); Assert.Null(result.Movements);
    }
    [Fact]
    public async Task Missing_transaction_and_transport_uncertainty_do_not_create_cash()
    {
        using var fixture = new Fixture(); fixture.Refund["balance_transaction"] = null;
        Assert.Equal(CashObservationState.Pending, (await fixture.Reader.ReadAsync(fixture.Original, "re_cash", CancellationToken.None)).State);
        fixture.Refund["balance_transaction"] = "txn_refund"; fixture.Fail = true;
        Assert.Equal(CashObservationState.Unknown, (await fixture.Reader.ReadAsync(fixture.Original, "re_cash", CancellationToken.None)).State);
    }
    [Fact]
    public async Task An_open_warning_with_no_cash_transactions_does_not_reverse_settlement()
    {
        using var fixture = new Fixture(); fixture.Dispute["balance_transactions"] = new JsonArray(); fixture.Dispute["status"] = "warning_needs_response";
        Assert.Equal(CashObservationState.Pending, (await fixture.Reader.ReadAsync(fixture.Original, "du_cash", CancellationToken.None)).State);
    }
    [Fact]
    public async Task Whole_inventory_reads_complete_catalogs_twice_and_returns_original_gross_evidence()
    {
        using var fixture = new Fixture(); var result = await fixture.Reader.ReadInventoryAsync(fixture.Original, CancellationToken.None);
        Assert.Equal(CashObservationState.Verified, result.State); var inventory = Assert.IsType<VerifiedCashInventory>(result.Inventory);
        Assert.Equal(fixture.Original.Id, inventory.AttemptId); Assert.Equal(1000, inventory.CapturedMinorUnits); Assert.Equal(200, inventory.RefundedMinorUnits);
        Assert.Equal("ch_cash", inventory.ChargeId); Assert.Equal("re_cash", Assert.Single(inventory.SourceIds)); Assert.Equal(-200, Assert.Single(inventory.Movements).SignedMinorUnits);
        Assert.Equal(2, fixture.Requests.Count(x => x.Path.StartsWith("/v1/refunds?", StringComparison.Ordinal)));
        Assert.Equal(2, fixture.Requests.Count(x => x.Path.StartsWith("/v1/disputes?", StringComparison.Ordinal)));
        Assert.All(fixture.Requests.Where(x => x.Path.Contains('?') && (x.Path.StartsWith("/v1/refunds?") || x.Path.StartsWith("/v1/disputes?"))), x => { Assert.Contains("charge=ch_cash", x.Path); Assert.Contains("limit=26", x.Path); });
        Assert.All(fixture.Requests, x => Assert.Equal("GET", x.Method)); Assert.True(inventory.CheckedAt >= inventory.Movements[0].ProviderAt);
    }
    [Theory]
    [InlineData("refund-truncated")]
    [InlineData("dispute-truncated")]
    [InlineData("too-many")]
    [InlineData("duplicate")]
    [InlineData("foreign-refund")]
    [InlineData("refund-total")]
    [InlineData("missing-dispute")]
    [InlineData("failed-with-cash")]
    [InlineData("invalid-status")]
    public async Task Incomplete_or_inconsistent_inventory_cannot_verify_agreement(string mutation)
    {
        using var fixture = new Fixture();
        switch (mutation)
        {
            case "refund-truncated": fixture.RefundsHaveMore = true; break;
            case "dispute-truncated": fixture.DisputesHaveMore = true; break;
            case "too-many": for (var i = 0; i < 25; i++) fixture.RefundCatalog.Add(fixture.Refund); break;
            case "duplicate": fixture.RefundCatalog.Add(fixture.Refund); break;
            case "foreign-refund": fixture.Refund["charge"] = "ch_foreign"; break;
            case "refund-total": fixture.Charge["amount_refunded"] = 201; break;
            case "missing-dispute": fixture.Charge["disputed"] = true; break;
            case "failed-with-cash": fixture.Refund["status"] = "failed"; fixture.Refund["failure_balance_transaction"] = "txn_reversal"; break;
            case "invalid-status": fixture.Refund["status"] = "invented"; break;
        }
        var result = await fixture.Reader.ReadInventoryAsync(fixture.Original, CancellationToken.None); Assert.Equal(CashObservationState.Invalid, result.State); Assert.Null(result.Inventory);
    }
    [Theory]
    [InlineData("pending-refund")]
    [InlineData("requires-action")]
    [InlineData("open-dispute")]
    [InlineData("won-without-restoration")]
    [InlineData("changed-catalog")]
    public async Task Ongoing_or_changed_provider_state_keeps_whole_inventory_pending(string mutation)
    {
        using var fixture = new Fixture();
        switch (mutation)
        {
            case "pending-refund": fixture.Refund["status"] = "pending"; break;
            case "requires-action": fixture.Refund["status"] = "requires_action"; break;
            case "open-dispute": fixture.Charge["disputed"] = true; fixture.Dispute["status"] = "under_review"; fixture.DisputeCatalog.Add(fixture.Dispute); break;
            case "won-without-restoration": fixture.Charge["disputed"] = true; fixture.Dispute["status"] = "won"; fixture.DisputeCatalog.Add(fixture.Dispute); break;
            case "changed-catalog": fixture.ChangeRefundCatalog = true; break;
        }
        var result = await fixture.Reader.ReadInventoryAsync(fixture.Original, CancellationToken.None); Assert.Equal(CashObservationState.Pending, result.State); Assert.Null(result.Inventory);
    }
    [Fact]
    public async Task Dispute_inventory_requires_real_withdrawal_and_reinstatement_and_keeps_failed_cash_out_of_scope()
    {
        using var fixture = new Fixture(); fixture.Charge["disputed"] = true; fixture.Dispute["status"] = "won";
        ((JsonArray)fixture.Dispute["balance_transactions"]!).Add(fixture.Transactions["txn_restored"].DeepClone()); fixture.DisputeCatalog.Add(fixture.Dispute);
        var result = await fixture.Reader.ReadInventoryAsync(fixture.Original, CancellationToken.None); Assert.Equal(CashObservationState.Verified, result.State);
        Assert.Equal(3, result.Inventory!.Movements.Count); Assert.Equal(0, result.Inventory.Movements.Where(x => x.SourceId == "du_cash").Sum(x => x.SignedMinorUnits));
        fixture.Fail = true; var unknown = await fixture.Reader.ReadInventoryAsync(fixture.Original, CancellationToken.None); Assert.Equal(CashObservationState.Unknown, unknown.State); Assert.Null(unknown.Inventory);
    }
    [Fact]
    public async Task Closed_warning_with_verified_fee_only_transactions_does_not_create_tenant_money()
    {
        using var fixture = new Fixture(); fixture.Charge["disputed"] = true; fixture.Dispute["status"] = "warning_closed";
        fixture.Transactions["txn_withdrawn"]["amount"] = 0; fixture.Transactions["txn_withdrawn"]["net"] = -30;
        fixture.DisputeCatalog.Add(fixture.Dispute);
        var result = await fixture.Reader.ReadInventoryAsync(fixture.Original, CancellationToken.None); Assert.Equal(CashObservationState.Verified, result.State);
        Assert.Equal("re_cash", Assert.Single(result.Inventory!.Movements).SourceId); Assert.Contains("du_cash", result.Inventory.SourceIds);
    }
    private sealed record Request(string Method, string Path);
    private sealed class Fixture : HttpMessageHandler
    {
        public PaymentAttempt Original { get; }
        public StripeCashMovementReader Reader { get; }
        public JsonObject Customer { get; }
        public JsonObject Intent { get; }
        public JsonObject Charge { get; }
        public JsonObject Refund { get; }
        public JsonObject Dispute { get; }
        public Dictionary<string, JsonObject> Transactions { get; } = [];
        public List<Request> Requests { get; } = [];
        public string Merchant { get; set; } = "acct_cash";
        public bool Fail { get; set; }
        public List<JsonObject> RefundCatalog { get; } = [];
        public List<JsonObject> DisputeCatalog { get; } = [];
        public bool RefundsHaveMore { get; set; }
        public bool DisputesHaveMore { get; set; }
        public bool ChangeRefundCatalog { get; set; }
        private int _refundLists;
        private readonly HttpClient _http;
        public Fixture()
        {
            var now = new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero); var account = Guid.NewGuid(); var binding = BillingCustomerBinding.Create(account, Merchant, false, "cus_cash", now);
            Original = PaymentAttempt.Admit(Guid.NewGuid(), "payer", binding, [new(Guid.NewGuid(), account, Guid.NewGuid(), 1000)], now); Original.BeginDispatch(now);
            Original.ApplyVerifiedSettlement(new(Merchant, false, "cus_cash", "cs_cash", "pi_cash", 1000, "USD"));
            Customer = new() { ["id"] = "cus_cash", ["object"] = "customer", ["livemode"] = false, ["metadata"] = new JsonObject { ["office_account_id"] = account.ToString("D") } };
            Charge = new() { ["id"] = "ch_cash", ["object"] = "charge", ["livemode"] = false, ["customer"] = "cus_cash", ["payment_intent"] = "pi_cash", ["amount"] = 1000, ["amount_captured"] = 1000, ["amount_refunded"] = 200, ["currency"] = "usd", ["paid"] = true, ["captured"] = true };
            Intent = new()
            {
                ["id"] = "pi_cash",
                ["object"] = "payment_intent",
                ["livemode"] = false,
                ["customer"] = "cus_cash",
                ["status"] = "succeeded",
                ["amount"] = 1000,
                ["amount_received"] = 1000,
                ["currency"] = "usd",
                ["latest_charge"] = Charge,
                ["metadata"] = new JsonObject { ["office_account_id"] = account.ToString("D"), ["office_attempt_id"] = Original.Id.ToString("D"), ["office_review_hash"] = Original.PayloadHash }
            };
            Refund = new() { ["id"] = "re_cash", ["object"] = "refund", ["payment_intent"] = "pi_cash", ["charge"] = "ch_cash", ["amount"] = 200, ["currency"] = "usd", ["status"] = "succeeded", ["balance_transaction"] = "txn_refund" };
            RefundCatalog.Add(Refund);
            JsonObject Transaction(string id, string source, long amount, string type) => new() { ["id"] = id, ["object"] = "balance_transaction", ["source"] = source, ["amount"] = amount, ["net"] = amount - 30, ["fee"] = 30, ["currency"] = "usd", ["type"] = type, ["balance_type"] = "payments", ["status"] = "available", ["created"] = now.ToUnixTimeSeconds() };
            Transactions["txn_refund"] = Transaction("txn_refund", "re_cash", -200, "refund"); Transactions["txn_withdrawn"] = Transaction("txn_withdrawn", "du_cash", -1000, "adjustment"); Transactions["txn_restored"] = Transaction("txn_restored", "du_cash", 1000, "adjustment");
            Dispute = new() { ["id"] = "du_cash", ["object"] = "dispute", ["livemode"] = false, ["payment_intent"] = "pi_cash", ["charge"] = "ch_cash", ["amount"] = 1000, ["currency"] = "usd", ["status"] = "lost", ["balance_transactions"] = new JsonArray(Transactions["txn_withdrawn"].DeepClone()) };
            _http = new HttpClient(this, disposeHandler: false); var client = new StripeClient("rk_test_synthetic", httpClient: new SystemNetHttpClient(_http, maxNetworkRetries: 0, enableTelemetry: false));
            Reader = new StripeCashMovementReader(new() { ApiKey = "rk_test_synthetic", ProviderAccount = Merchant }, client);
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath; Requests.Add(new(request.Method.Method, request.RequestUri.PathAndQuery)); if (Fail) throw new HttpRequestException("Synthetic uncertainty");
            JsonObject Catalog(List<JsonObject> items, bool more) => new() { ["object"] = "list", ["url"] = path, ["has_more"] = more, ["data"] = new JsonArray(items.Select(x => x.DeepClone()).ToArray()) };
            if (path == "/v1/refunds" && ++_refundLists > 1 && ChangeRefundCatalog) RefundCatalog.Add(new JsonObject { ["id"] = "re_changed", ["object"] = "refund", ["status"] = "failed" });
            JsonObject value = path switch
            {
                "/v1/account" => new() { ["id"] = Merchant, ["object"] = "account" },
                "/v1/customers/cus_cash" => Customer,
                "/v1/payment_intents/pi_cash" => Intent,
                "/v1/refunds" => Catalog(RefundCatalog, RefundsHaveMore),
                "/v1/disputes" => Catalog(DisputeCatalog, DisputesHaveMore),
                "/v1/refunds/re_cash" => Refund,
                "/v1/disputes/du_cash" => Dispute,
                _ when path.StartsWith("/v1/balance_transactions/", StringComparison.Ordinal) => Transactions[path.Split('/').Last()],
                _ => throw new InvalidOperationException("Unexpected cash provider request.")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(value.ToJsonString(), Encoding.UTF8, "application/json") });
        }
        protected override void Dispose(bool disposing) { if (disposing) { Reader.Dispose(); _http.Dispose(); } base.Dispose(disposing); }
    }
}
