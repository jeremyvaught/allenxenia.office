namespace AllenBuilding.Domain.Billing;

public enum FinancialExceptionState { Open, Escalated, Resolved }
public enum FinancialFailure { ProviderUnknown, ProviderMismatch, LedgerChanged, ClaimsIncomplete, TerminalDisagreement, ExternalCashMovement }

// One durable recovery case for an original attempt. Separate from immutable cash/history.
public sealed class FinancialException
{
    private FinancialException() { }
    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }
    public Guid AttemptId { get; private set; }
    public Guid Version { get; private set; }
    public FinancialExceptionState State { get; private set; }
    public FinancialFailure Failure { get; private set; }
    public string? OwnerUserId { get; private set; }
    public string? LastVerifiedState { get; private set; }
    public DateTimeOffset? LastVerifiedAt { get; private set; }
    public DateTimeOffset FirstSeenAt { get; private set; }
    public DateTimeOffset LastSeenAt { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }

    public static FinancialException Open(PaymentAttempt attempt, FinancialFailure failure, string? verifiedState, DateTimeOffset now)
    {
        if (attempt is null) throw new ArgumentException("The original collection is required.");
        var issue = new FinancialException { Id = Guid.NewGuid(), AccountId = attempt.AccountId, AttemptId = attempt.Id, FirstSeenAt = now.ToUniversalTime() };
        issue.Observe(failure, verifiedState, now); return issue;
    }
    public void Observe(FinancialFailure failure, string? verifiedState, DateTimeOffset now)
    {
        if (!Enum.IsDefined(failure) || verifiedState is not (null or "Open" or "Processing" or "Paid" or "Uncollected")) throw new ArgumentException("Invalid financial recovery state.");
        if (State == FinancialExceptionState.Resolved) { State = FinancialExceptionState.Open; ResolvedAt = null; }
        Failure = failure; LastSeenAt = now.ToUniversalTime(); Version = Guid.NewGuid();
        if (verifiedState is not null) { LastVerifiedState = verifiedState; LastVerifiedAt = now.ToUniversalTime(); }
    }
    public void Assign(string managerId)
    {
        if (State == FinancialExceptionState.Resolved || string.IsNullOrWhiteSpace(managerId) || managerId.Length > 450) throw new InvalidOperationException("Only an open case can be assigned to a manager.");
        OwnerUserId = managerId; Version = Guid.NewGuid();
    }
    public void Escalate()
    {
        if (State == FinancialExceptionState.Resolved) throw new InvalidOperationException("Resolved history cannot be escalated.");
        State = FinancialExceptionState.Escalated; Version = Guid.NewGuid();
    }
    public void ReconciliationRequested(DateTimeOffset now)
    {
        if (State == FinancialExceptionState.Resolved) throw new InvalidOperationException("Only an unresolved case can request recovery.");
        LastSeenAt = now.ToUniversalTime(); Version = Guid.NewGuid();
    }
    // Only canonical reconciliation can resolve a case; there is no arbitrary manager Paid/Resolve setter.
    public void ResolveVerified(DateTimeOffset now)
    {
        if (State == FinancialExceptionState.Resolved) return;
        State = FinancialExceptionState.Resolved; ResolvedAt = now.ToUniversalTime(); Version = Guid.NewGuid();
    }
}

public sealed class FinancialExceptionHistory
{
    private FinancialExceptionHistory() { }
    public Guid Id { get; private set; }
    public Guid ExceptionId { get; private set; }
    public string Action { get; private set; } = "";
    public string? ActorUserId { get; private set; }
    public string? Reason { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public static FinancialExceptionHistory Record(Guid exceptionId, string action, string? actor, string? reason, DateTimeOffset now)
    {
        if (exceptionId == Guid.Empty || action is not ("Opened" or "Observed" or "Reopened" or "Resolved" or "Assigned" or "Escalated" or "ReconcileRequested" or "RefundDispositionApplied") ||
            (actor is not null && (string.IsNullOrWhiteSpace(actor) || actor.Length > 450)) || reason?.Length > 2000)
            throw new ArgumentException("Invalid financial recovery history.");
        return new() { Id = Guid.NewGuid(), ExceptionId = exceptionId, Action = action, ActorUserId = actor, Reason = reason, OccurredAt = now.ToUniversalTime() };
    }
}
