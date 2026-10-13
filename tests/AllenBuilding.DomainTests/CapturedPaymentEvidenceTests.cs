using AllenBuilding.Domain.Billing;

namespace AllenBuilding.DomainTests;

public sealed class CapturedPaymentEvidenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private static (PaymentAttempt Original, CanonicalSettlement Settlement) Fixture()
    {
        var account = Guid.NewGuid(); var binding = BillingCustomerBinding.Create(account, "acct_evidence", false, "cus_evidence", Now);
        var original = PaymentAttempt.Admit(Guid.NewGuid(), "payer", binding, [new(Guid.NewGuid(), account, Guid.NewGuid(), 1000)], Now); original.BeginDispatch(Now);
        return (original, new(binding.ProviderAccount, false, binding.CustomerId, "cs_evidence", "pi_evidence", 1000, "USD"));
    }
    [Fact]
    public void Canonical_pre_ledger_evidence_does_not_mutate_the_original_state_references_or_fence_version()
    {
        var fixture = Fixture(); var version = fixture.Original.Version; var source = CapturedPaymentEvidence.FromSettlement(fixture.Original, fixture.Settlement);
        Assert.Equal(fixture.Original.Id, source.Id); Assert.Equal(fixture.Original.PayloadHash, source.PayloadHash); Assert.Equal("pi_evidence", source.PaymentId);
        Assert.Equal(PaymentAttemptState.Dispatching, fixture.Original.State); Assert.Equal(version, fixture.Original.Version); Assert.Null(fixture.Original.PaymentId); Assert.Null(fixture.Original.SessionId);
        Assert.Throws<ArgumentException>(() => CapturedPaymentEvidence.FromSettled(fixture.Original));
    }
    [Fact]
    public void Foreign_amount_mode_customer_session_or_payment_cannot_replace_the_dispatched_original()
    {
        var fixture = Fixture(); fixture.Original.AttachHandoff("cs_evidence", Now.AddHours(1), Now);
        foreach (var wrong in new[] { fixture.Settlement with { AmountMinorUnits = 999 }, fixture.Settlement with { Currency = "EUR" }, fixture.Settlement with { LiveMode = true }, fixture.Settlement with { CustomerId = "cus_foreign" }, fixture.Settlement with { SessionId = "cs_foreign" }, fixture.Settlement with { PaymentId = "untrusted" } })
            Assert.Throws<ArgumentException>(() => CapturedPaymentEvidence.FromSettlement(fixture.Original, wrong));
        fixture.Original.ApplyVerifiedSettlement(fixture.Settlement); var version = fixture.Original.Version;
        Assert.Throws<ArgumentException>(() => CapturedPaymentEvidence.FromSettlement(fixture.Original, fixture.Settlement with { PaymentId = "pi_foreign" }));
        Assert.Equal("pi_evidence", CapturedPaymentEvidence.FromSettled(fixture.Original).PaymentId); Assert.Equal(version, fixture.Original.Version);
    }
}
