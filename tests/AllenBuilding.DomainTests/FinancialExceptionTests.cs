using AllenBuilding.Domain.Billing;

namespace AllenBuilding.DomainTests;

public sealed class FinancialExceptionTests
{
    private static PaymentAttempt Attempt(DateTimeOffset now)
    {
        var binding = BillingCustomerBinding.Create(Guid.NewGuid(), "acct_issue", false, "cus_issue", now);
        return PaymentAttempt.Admit(Guid.NewGuid(), "payer", binding, [new(Guid.NewGuid(), binding.AccountId, Guid.NewGuid(), 1000)], now);
    }
    [Fact]
    public void Recovery_case_preserves_last_verified_state_through_outage_escalation_resolution_and_reopening()
    {
        var now = DateTimeOffset.UtcNow; var original = Attempt(now);
        var issue = FinancialException.Open(original, FinancialFailure.ProviderMismatch, "Processing", now);
        issue.Assign("manager"); issue.Escalate(); issue.Observe(FinancialFailure.ProviderUnknown, null, now.AddMinutes(1));
        Assert.Equal(FinancialExceptionState.Escalated, issue.State); Assert.Equal("Processing", issue.LastVerifiedState); Assert.Equal(now, issue.LastVerifiedAt);
        issue.ResolveVerified(now.AddMinutes(2)); var resolvedVersion = issue.Version;
        issue.ResolveVerified(now.AddMinutes(3)); Assert.Equal(resolvedVersion, issue.Version);
        Assert.Throws<InvalidOperationException>(() => issue.Assign("manager")); Assert.Throws<InvalidOperationException>(issue.Escalate);
        issue.Observe(FinancialFailure.TerminalDisagreement, null, now.AddMinutes(4));
        Assert.Equal(FinancialExceptionState.Open, issue.State); Assert.Null(issue.ResolvedAt); Assert.Equal("manager", issue.OwnerUserId);
        Assert.Equal(original.Id, issue.AttemptId); Assert.Equal(original.AccountId, issue.AccountId); Assert.Equal(now, issue.FirstSeenAt);
    }
    [Fact]
    public void Recovery_state_and_history_accept_only_bounded_allowed_values()
    {
        var now = DateTimeOffset.UtcNow; var original = Attempt(now);
        Assert.Throws<ArgumentException>(() => FinancialException.Open(original, (FinancialFailure)99, null, now));
        Assert.Throws<ArgumentException>(() => FinancialException.Open(original, FinancialFailure.ProviderUnknown, "Manager typed Paid", now));
        var issue = FinancialException.Open(original, FinancialFailure.ProviderUnknown, null, now);
        Assert.Throws<InvalidOperationException>(() => issue.Assign(""));
        var history = FinancialExceptionHistory.Record(issue.Id, "Opened", null, "ProviderUnknown", now);
        Assert.Null(history.ActorUserId); Assert.Equal(issue.Id, history.ExceptionId);
        Assert.Throws<ArgumentException>(() => FinancialExceptionHistory.Record(issue.Id, "ChargeCard", "manager", null, now));
        Assert.Throws<ArgumentException>(() => FinancialExceptionHistory.Record(issue.Id, "Escalated", "manager", new string('x', 2001), now));
    }
}
