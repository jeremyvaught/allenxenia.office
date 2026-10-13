namespace AllenBuilding.Domain.Billing;

public enum ObligationCreditKind { Credit, WriteOff }

// Debt reduction is distinct from cash. One cent is valid after an adjustment.
public sealed class ObligationCredit
{
    private ObligationCredit() { }
    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }
    public Guid ObligationId { get; private set; }
    public Guid ReviewedVersion { get; private set; }
    public Guid OperationId { get; private set; }
    public string ActorId { get; private set; } = "";
    public ObligationCreditKind Kind { get; private set; }
    public long AmountMinorUnits { get; private set; }
    public string Currency { get; private set; } = "USD";
    public string Reason { get; private set; } = "";
    public DateTimeOffset RecordedAt { get; private set; }

    // The coordinator has proved this is local debt with no possible provider instruction.
    public static ObligationCredit RecordLocal(Guid operationId, string actorId, ReviewedCollectionBalance reviewed, long amount,
        ObligationCreditKind kind, string reason, DateTimeOffset now)
    {
        reason = reason?.Trim() ?? "";
        if (operationId == Guid.Empty || string.IsNullOrWhiteSpace(actorId) || actorId.Length > 450 || reviewed is null || reviewed.ObligationId == Guid.Empty ||
            reviewed.AccountId == Guid.Empty || reviewed.Version == Guid.Empty || reviewed.Currency != "USD" || reviewed.Held || reviewed.HasProviderInstruction ||
            amount is < 1 or > 99_999_999 || amount > reviewed.RemainingMinorUnits || !Enum.IsDefined(kind) || reason.Length is < 1 or > 2000)
            throw new ArgumentException("A credit requires an authorized exact reviewed balance, positive USD amount and reason, without unresolved provider collection.");
        return new()
        {
            Id = Guid.NewGuid(),
            AccountId = reviewed.AccountId,
            ObligationId = reviewed.ObligationId,
            ReviewedVersion = reviewed.Version,
            OperationId = operationId,
            ActorId = actorId,
            Kind = kind,
            AmountMinorUnits = amount,
            Reason = reason,
            RecordedAt = now.ToUniversalTime()
        };
    }
}
