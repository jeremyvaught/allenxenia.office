using AllenBuilding.Domain.Billing;

namespace AllenBuilding.Application.Billing;

// Shared bounded validation for initial cash import and later agreement; attach records only after the complete batch passes.
public static class CashInventoryRules
{
    public static bool TryPrepare(PaymentAttempt original, SettlementPayment capture, VerifiedCashInventory? inventory, DateTimeOffset now, out IReadOnlyList<CashMovement> records)
    {
        records = [];
        if (inventory is null || original.State != PaymentAttemptState.Settled || inventory.ProviderAccount != original.ProviderAccount || inventory.LiveMode != original.LiveMode ||
            inventory.AccountId != original.AccountId || inventory.AttemptId != original.Id || inventory.CustomerId != original.CustomerId || inventory.PaymentId != original.PaymentId ||
            !PaymentReferences.Valid(inventory.ChargeId, "ch_") || inventory.CapturedMinorUnits != original.TotalMinorUnits || inventory.RefundedMinorUnits < 0 || inventory.RefundedMinorUnits > original.TotalMinorUnits ||
            inventory.CheckedAt < original.CreatedAt || inventory.CheckedAt > now.AddSeconds(5) || inventory.CheckedAt < now.AddMinutes(-1) || inventory.SourceIds is not { Count: <= 25 } || inventory.Movements is not { Count: <= 25 } ||
            inventory.SourceIds.Any(x => !PaymentReferences.Valid(x, "re_") && !CashMovement.DisputeReference(x)) || inventory.SourceIds.Distinct(StringComparer.Ordinal).Count() != inventory.SourceIds.Count ||
            inventory.Movements.Any(x => x is null || !inventory.SourceIds.Contains(x.SourceId, StringComparer.Ordinal)) || inventory.Movements.Select(x => x.TransactionId).Distinct(StringComparer.Ordinal).Count() != inventory.Movements.Count ||
            capture.Allocations.Count != original.Items.Count || capture.Allocations.Any(x => !original.Items.Any(i => i.ObligationId == x.ObligationId && i.AmountMinorUnits == x.AmountMinorUnits && i.AccountId == x.AccountId))) return false;
        var batch = new List<CashMovement>();
        foreach (var fact in inventory.Movements)
        { try { batch.Add(CashMovement.Record(capture, original, fact, now)); } catch (InvalidOperationException) { return false; } }
        if (inventory.Movements.Where(x => x.Kind == CashMovementKind.Refund).Sum(x => -x.SignedMinorUnits) != inventory.RefundedMinorUnits) return false;
        records = batch; return true;
    }
}
