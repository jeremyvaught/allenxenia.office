using AllenBuilding.Domain.Billing;

namespace AllenBuilding.DomainTests;

public sealed class PaymentAttemptTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 15, 0, 0, TimeSpan.Zero);
    private static BillingCustomerBinding Binding() => BillingCustomerBinding.Create(Guid.NewGuid(), "acct_synthetic", false, "cus_synthetic", Now);
    private static ReviewedCollectionBalance Item(Guid account, long cents) => new(Guid.NewGuid(), account, Guid.NewGuid(), cents);

    [Fact]
    public void Itemized_review_snapshots_preserve_debt_and_have_a_canonical_order_independent_hash()
    {
        var binding = Binding(); var operation = Guid.NewGuid();
        var small = Item(binding.AccountId, 25); var current = Item(binding.AccountId, 1000);
        var submitted = new List<ReviewedCollectionBalance> { current, small };
        var attempt = PaymentAttempt.Admit(operation, "payer", binding, submitted, Now);
        var reordered = PaymentAttempt.Admit(operation, "payer", binding, submitted.AsEnumerable().Reverse(), Now);
        Assert.Equal(1025, attempt.TotalMinorUnits); Assert.Equal(attempt.PayloadHash, reordered.PayloadHash);
        Assert.Equal("USD", attempt.Currency); Assert.Equal(2, attempt.Items.Count);
        Assert.All(attempt.Items, x => Assert.Equal(binding.AccountId, x.AccountId));
        Assert.Equal(small.Version, attempt.Items.Single(x => x.ObligationId == small.ObligationId).ReviewedVersion);
        submitted.Clear(); Assert.Equal(2, attempt.Items.Count);
        Assert.Throws<NotSupportedException>(() => ((ICollection<PaymentAttemptItem>)attempt.Items).Clear());
        Assert.NotEqual(attempt.PayloadHash, PaymentAttempt.Admit(operation, "payer", binding, new[] { small with { Version = Guid.NewGuid() }, current }, Now).PayloadHash);
        Assert.NotEqual(attempt.PayloadHash, PaymentAttempt.Admit(operation, "payer", binding, new[] { small with { RemainingMinorUnits = 26 }, current }, Now).PayloadHash);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(25, false)]
    [InlineData(49, false)]
    [InlineData(50, true)]
    [InlineData(99_999_999, true)]
    [InlineData(100_000_000, false)]
    public void Actual_collection_amount_is_validated_independently_of_the_original_charge(long cents, bool accepted)
    {
        var binding = Binding(); var item = Item(binding.AccountId, cents);
        if (accepted) Assert.Equal(cents, PaymentAttempt.Admit(Guid.NewGuid(), "payer", binding, new[] { item }, Now).TotalMinorUnits);
        else Assert.Throws<ArgumentException>(() => PaymentAttempt.Admit(Guid.NewGuid(), "payer", binding, new[] { item }, Now));
    }

    [Fact]
    public void Every_item_must_be_eligible_and_a_changed_or_duplicate_item_invalidates_the_whole_review()
    {
        var binding = Binding(); var full = Item(binding.AccountId, 1000); var small = Item(binding.AccountId, 25);
        void Invalid(params ReviewedCollectionBalance[] items) => Assert.Throws<ArgumentException>(() => PaymentAttempt.Admit(Guid.NewGuid(), "payer", binding, items, Now));
        Invalid(); Invalid(null!); Invalid(full, full); Invalid(full with { Version = Guid.Empty }); Invalid(full with { ObligationId = Guid.Empty });
        Invalid(full with { AccountId = Guid.NewGuid() }); Invalid(full with { Currency = "EUR" }); Invalid(full with { HasProviderInstruction = true });
        Invalid(full, small with { Held = true }); Invalid(full, Item(binding.AccountId, 60));
        Invalid(Enumerable.Range(0, 26).Select(_ => Item(binding.AccountId, 2)).ToArray());
        Assert.Equal(50, PaymentAttempt.Admit(Guid.NewGuid(), "payer", binding, new[] { small, Item(binding.AccountId, 25) }, Now).TotalMinorUnits);
        Assert.Throws<OverflowException>(() => PaymentAttempt.Admit(Guid.NewGuid(), "payer", binding, new[] { Item(binding.AccountId, long.MaxValue), small }, Now));
    }

    [Fact]
    public void Dispatch_keeps_the_original_key_and_lost_acknowledgement_requires_canonical_reconciliation()
    {
        var binding = Binding(); var attempt = PaymentAttempt.Admit(Guid.NewGuid(), "payer", binding, new[] { Item(binding.AccountId, 1000) }, Now);
        var key = attempt.ProviderIdempotencyKey; var version = attempt.Version;
        attempt.BeginDispatch(Now); Assert.NotEqual(version, attempt.Version); Assert.Equal(key, attempt.ProviderIdempotencyKey);
        attempt.RequireReview(); Assert.Equal(PaymentAttemptState.NeedsReview, attempt.State);
        Assert.Throws<InvalidOperationException>(() => attempt.CancelBeforeDispatch());
        Assert.Throws<InvalidOperationException>(() => attempt.BeginDispatch(Now));
        Assert.Throws<InvalidOperationException>(() => attempt.ConfirmVerifiedUncollected("acct_other", false, binding.CustomerId, "cs_unknown"));
        attempt.AttachHandoff("cs_original", Now.AddHours(1), Now); attempt.ObserveProcessing("cs_original");
        Assert.Throws<InvalidOperationException>(() => attempt.ConfirmVerifiedUncollected(binding.ProviderAccount, false, "cus_other", "cs_original"));
        attempt.RequireReview(); attempt.ConfirmVerifiedUncollected(binding.ProviderAccount, false, binding.CustomerId, "cs_original");
        Assert.Equal(PaymentAttemptState.Uncollected, attempt.State); Assert.Equal(key, attempt.ProviderIdempotencyKey);
        Assert.Throws<InvalidOperationException>(() => attempt.BeginDispatch(Now));
    }

    [Fact]
    public void Local_expiry_does_not_release_an_admitted_or_dispatched_collection()
    {
        var binding = Binding(); var attempt = PaymentAttempt.Admit(Guid.NewGuid(), "payer", binding, new[] { Item(binding.AccountId, 1000) }, Now);
        Assert.Throws<InvalidOperationException>(() => attempt.BeginDispatch(Now.AddSeconds(-1)));
        Assert.Throws<InvalidOperationException>(() => attempt.BeginDispatch(Now.AddHours(23)));
        Assert.Equal(PaymentAttemptState.Admitted, attempt.State);
        attempt.BeginDispatch(Now); attempt.AttachHandoff("cs_original", Now.AddHours(1), Now);
        Assert.Throws<InvalidOperationException>(() => attempt.CancelBeforeDispatch());
        Assert.Throws<InvalidOperationException>(() => attempt.AttachHandoff("cs_replacement", Now.AddHours(2), Now));
        Assert.Throws<InvalidOperationException>(() => attempt.ObserveProcessing("cs_replacement"));
        Assert.Equal(PaymentAttemptState.HandoffReady, attempt.State);
    }

    [Fact]
    public void Settlement_matches_every_identity_and_amount_and_replay_cannot_downgrade_or_replace_it()
    {
        var binding = Binding(); var attempt = PaymentAttempt.Admit(Guid.NewGuid(), "payer", binding, new[] { Item(binding.AccountId, 1000) }, Now);
        var fact = new CanonicalSettlement(binding.ProviderAccount, false, binding.CustomerId, "cs_original", "pi_original", 1000, "USD");
        Assert.Throws<InvalidOperationException>(() => attempt.ApplyVerifiedSettlement(fact));
        attempt.BeginDispatch(Now); attempt.RequireReview();
        foreach (var wrong in new[] { fact with { ProviderAccount = "acct_other" }, fact with { LiveMode = true }, fact with { CustomerId = "cus_other" },
            fact with { Currency = "EUR" }, fact with { AmountMinorUnits = 1025 }, fact with { PaymentId = "ch_unrelated" } })
            Assert.Throws<InvalidOperationException>(() => attempt.ApplyVerifiedSettlement(wrong));
        attempt.ApplyVerifiedSettlement(fact); var version = attempt.Version;
        attempt.ApplyVerifiedSettlement(fact); Assert.Equal(version, attempt.Version); Assert.Equal(PaymentAttemptState.Settled, attempt.State);
        Assert.Throws<InvalidOperationException>(() => attempt.ApplyVerifiedSettlement(fact with { PaymentId = "pi_replacement" }));
        Assert.Throws<InvalidOperationException>(() => attempt.ApplyVerifiedSettlement(fact with { SessionId = "cs_replacement" }));
        Assert.Throws<InvalidOperationException>(() => attempt.RequireReview());
        Assert.Throws<InvalidOperationException>(() => attempt.ConfirmVerifiedUncollected(binding.ProviderAccount, false, binding.CustomerId, "cs_original"));
    }

    [Fact]
    public void Invalid_customer_scope_and_missing_payer_operation_are_rejected_before_admission()
    {
        Assert.Throws<ArgumentException>(() => BillingCustomerBinding.Create(Guid.NewGuid(), "acct_", false, "cus_synthetic", Now));
        Assert.Throws<ArgumentException>(() => BillingCustomerBinding.Create(Guid.Empty, "acct_synthetic", false, "cus_synthetic", Now));
        Assert.Throws<ArgumentException>(() => BillingCustomerBinding.Create(Guid.NewGuid(), "acct_synthetic", false, "cus_synthetic\n", Now));
        var binding = Binding(); var item = Item(binding.AccountId, 1000);
        Assert.Throws<ArgumentException>(() => PaymentAttempt.Admit(Guid.Empty, "payer", binding, new[] { item }, Now));
        Assert.Throws<ArgumentException>(() => PaymentAttempt.Admit(Guid.NewGuid(), "", binding, new[] { item }, Now));
        var attempt = PaymentAttempt.Admit(Guid.NewGuid(), "payer", binding, new[] { item }, Now);
        attempt.CancelBeforeDispatch(); Assert.Equal(PaymentAttemptState.Uncollected, attempt.State);
        Assert.Throws<InvalidOperationException>(() => attempt.CancelBeforeDispatch());
        var lost = PaymentAttempt.Admit(Guid.NewGuid(), "payer", binding, new[] { item }, Now);
        lost.BeginDispatch(Now); lost.RequireReview();
        lost.ConfirmVerifiedUncollected(binding.ProviderAccount, false, binding.CustomerId, "cs_canonically_expired");
        Assert.Equal("cs_canonically_expired", lost.SessionId); Assert.Equal(PaymentAttemptState.Uncollected, lost.State);
    }
}
