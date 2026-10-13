namespace AllenBuilding.Domain.Billing;

public enum CashMovementKind { Refund, DisputeWithdrawal, DisputeReinstatement }
public enum CashMovementReviewState { Pending, Classified, AppliedPendingVerification }
public enum RefundDisposition { RetainDebt, CreditDebt }
public sealed record CanonicalCashMovement(string ProviderAccount, bool LiveMode, Guid AccountId, Guid AttemptId, string CustomerId,
    string PaymentId, string SourceId, string TransactionId, CashMovementKind Kind, long SignedMinorUnits, string Currency, DateTimeOffset ProviderAt);

// Immutable gross movement, separate from provider fees, original capture, debt credits and manager allocation.
public sealed class CashMovement
{
    private CashMovement() { }
    public Guid Id { get; private set; }
    public Guid CaptureId { get; private set; }
    public Guid AccountId { get; private set; }
    public Guid AttemptId { get; private set; }
    public string ProviderAccount { get; private set; } = "";
    public bool LiveMode { get; private set; }
    public string CustomerId { get; private set; } = "";
    public string PaymentId { get; private set; } = "";
    public string SourceId { get; private set; } = "";
    public string TransactionId { get; private set; } = "";
    public CashMovementKind Kind { get; private set; }
    public long SignedMinorUnits { get; private set; }
    public string Currency { get; private set; } = "USD";
    public DateTimeOffset ProviderAt { get; private set; }
    public DateTimeOffset RecordedAt { get; private set; }

    public static CashMovement Record(SettlementPayment capture, PaymentAttempt original, CanonicalCashMovement verified, DateTimeOffset now)
    {
        if (capture is null || original is null || verified is null || original.State != PaymentAttemptState.Settled || capture.AttemptId != original.Id ||
            capture.AccountId != original.AccountId || capture.ProviderAccount != original.ProviderAccount || capture.LiveMode != original.LiveMode || capture.CollectionReference != original.PaymentId || capture.AmountMinorUnits != original.TotalMinorUnits ||
            verified.ProviderAccount != original.ProviderAccount || verified.LiveMode != original.LiveMode || verified.AccountId != original.AccountId || verified.AttemptId != original.Id ||
            verified.CustomerId != original.CustomerId || verified.PaymentId != original.PaymentId || !Enum.IsDefined(verified.Kind) ||
            !PaymentReferences.Valid(verified.TransactionId, "txn_") || verified.Currency != "USD" || capture.Currency != verified.Currency ||
            verified.SignedMinorUnits is < -99_999_999 or > 99_999_999 or 0 || Math.Abs(verified.SignedMinorUnits) > capture.AmountMinorUnits ||
            (verified.Kind == CashMovementKind.DisputeReinstatement ? verified.SignedMinorUnits < 0 : verified.SignedMinorUnits > 0) ||
            (verified.Kind == CashMovementKind.Refund ? !PaymentReferences.Valid(verified.SourceId, "re_") : !DisputeReference(verified.SourceId)) ||
            verified.ProviderAt == default || verified.ProviderAt < original.CreatedAt || verified.ProviderAt > now.AddMinutes(5))
            throw new InvalidOperationException("Cash movement must match verified original capture, source, scope, currency, sign and gross amount.");
        return new()
        {
            Id = Guid.NewGuid(),
            CaptureId = capture.Id,
            AccountId = original.AccountId,
            AttemptId = original.Id,
            ProviderAccount = verified.ProviderAccount,
            LiveMode = verified.LiveMode,
            CustomerId = verified.CustomerId,
            PaymentId = verified.PaymentId,
            SourceId = verified.SourceId,
            TransactionId = verified.TransactionId,
            Kind = verified.Kind,
            SignedMinorUnits = verified.SignedMinorUnits,
            Currency = verified.Currency,
            ProviderAt = verified.ProviderAt.ToUniversalTime(),
            RecordedAt = now.ToUniversalTime()
        };
    }
    public bool Matches(CanonicalCashMovement fact) => fact.ProviderAccount == ProviderAccount && fact.LiveMode == LiveMode && fact.AccountId == AccountId && fact.AttemptId == AttemptId &&
        fact.CustomerId == CustomerId && fact.PaymentId == PaymentId && fact.SourceId == SourceId && fact.TransactionId == TransactionId && fact.Kind == Kind &&
        fact.SignedMinorUnits == SignedMinorUnits && fact.Currency == Currency && fact.ProviderAt == ProviderAt;
    public static bool DisputeReference(string value) => PaymentReferences.Valid(value, "dp_") || PaymentReferences.Valid(value, "du_");
}

// A separate mutable review lifecycle cannot rewrite the money fact or manufacture forgiveness.
public sealed class CashMovementReview
{
    private CashMovementReview() { }
    public Guid MovementId { get; private set; }
    public Guid AccountId { get; private set; }
    public CashMovementReviewState State { get; private set; }
    public Guid Version { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? OperationId { get; private set; }
    public string? ActorId { get; private set; }
    public RefundDisposition? Disposition { get; private set; }
    public string? Reason { get; private set; }
    public DateTimeOffset? AppliedAt { get; private set; }
    public DateTimeOffset? VerifiedAt { get; private set; }
    public string? AgreementHash { get; private set; }
    public static CashMovementReview Open(CashMovement movement) => new()
    { MovementId = movement.Id, AccountId = movement.AccountId, State = CashMovementReviewState.Pending, Version = Guid.NewGuid(), CreatedAt = movement.RecordedAt };

    // Called only by canonical whole-payment agreement after complete ledger/provider validation.
    public void ConfirmRefundAgreement(CashMovement movement, string agreementHash, DateTimeOffset checkedAt)
    {
        if (movement is null || movement.Id != MovementId || movement.AccountId != AccountId || movement.Kind != CashMovementKind.Refund ||
            State is not (CashMovementReviewState.AppliedPendingVerification or CashMovementReviewState.Classified) || OperationId is null || ActorId is null || Disposition is null || AppliedAt is null || checkedAt < AppliedAt ||
            !ValidHash(agreementHash)) throw new InvalidOperationException("Only a reviewed refund can acquire canonical agreement evidence.");
        Verify(agreementHash, checkedAt);
    }
    public void ConfirmRestoredDispute(CashMovement movement, IReadOnlyList<CashMovement> completeSource, string agreementHash, DateTimeOffset checkedAt)
    {
        if (movement is null || completeSource is not { Count: >= 2 and <= 25 } || movement.Id != MovementId || movement.AccountId != AccountId || movement.Kind == CashMovementKind.Refund ||
            State is not (CashMovementReviewState.Pending or CashMovementReviewState.Classified) || checkedAt < CreatedAt || !ValidHash(agreementHash) ||
            completeSource.Select(x => x.Id).Distinct().Count() != completeSource.Count || !completeSource.Any(x => x.Id == movement.Id) ||
            completeSource.Any(x => x.CaptureId != movement.CaptureId || x.AccountId != movement.AccountId || x.AttemptId != movement.AttemptId || x.SourceId != movement.SourceId || x.Kind == CashMovementKind.Refund) ||
            !completeSource.Any(x => x.Kind == CashMovementKind.DisputeWithdrawal) || !completeSource.Any(x => x.Kind == CashMovementKind.DisputeReinstatement) || completeSource.Sum(x => x.SignedMinorUnits) != 0)
            throw new InvalidOperationException("Dispute restoration needs the complete verified original-source withdrawal and reinstatement.");
        Verify(agreementHash, checkedAt);
    }
    private static bool ValidHash(string hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
    private void Verify(string hash, DateTimeOffset checkedAt)
    {
        if (VerifiedAt > checkedAt) throw new InvalidOperationException("Older verification cannot replace newer agreement evidence.");
        if (State == CashMovementReviewState.Classified && AgreementHash == hash && VerifiedAt == checkedAt) return;
        State = CashMovementReviewState.Classified; AgreementHash = hash; VerifiedAt = checkedAt.ToUniversalTime(); Version = Guid.NewGuid();
    }

    public void ApplyRefund(CashMovement movement, Guid operationId, string actorId, RefundDisposition disposition, string reason, DateTimeOffset now)
    {
        reason = reason?.Trim() ?? "";
        if (movement is null || movement.Id != MovementId || movement.AccountId != AccountId || movement.Kind != CashMovementKind.Refund ||
            State != CashMovementReviewState.Pending || operationId == Guid.Empty || string.IsNullOrWhiteSpace(actorId) || actorId.Length > 450 ||
            !Enum.IsDefined(disposition) || reason.Length is < 1 or > 2000 || now < CreatedAt)
            throw new InvalidOperationException("A refund disposition requires the pending original fact, authority, operation and reason.");
        OperationId = operationId; ActorId = actorId; Disposition = disposition; Reason = reason; AppliedAt = now.ToUniversalTime();
        // Local allocation is not proof that all provider instructions and transactions agree.
        State = CashMovementReviewState.AppliedPendingVerification; Version = Guid.NewGuid();
    }
}
