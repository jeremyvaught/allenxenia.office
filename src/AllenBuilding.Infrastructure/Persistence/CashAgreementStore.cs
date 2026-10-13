using System.Security.Cryptography;
using System.Text.Json;
using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Infrastructure.Persistence;

// Internal verification under the caller's account/original transaction and live worker fence; no HTTP Resolve setter.
internal sealed class CashAgreementStore(OfficeDbContext db, TimeProvider time)
{
    public async Task<bool> VerifyLockedAsync(PaymentAttempt original, VerifiedCashInventory inventory, CancellationToken token)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Agreement requires a caller-owned account/original transaction.");
        var now = time.GetUtcNow();
        if (inventory is null || original.State != PaymentAttemptState.Settled || inventory.ProviderAccount != original.ProviderAccount || inventory.LiveMode != original.LiveMode ||
            inventory.AccountId != original.AccountId || inventory.AttemptId != original.Id || inventory.CustomerId != original.CustomerId || inventory.PaymentId != original.PaymentId ||
            !PaymentReferences.Valid(inventory.ChargeId, "ch_") || inventory.CapturedMinorUnits != original.TotalMinorUnits || inventory.RefundedMinorUnits < 0 || inventory.RefundedMinorUnits > original.TotalMinorUnits ||
            inventory.CheckedAt < original.CreatedAt || inventory.CheckedAt > now.AddSeconds(5) || inventory.CheckedAt < now.AddMinutes(-1) || inventory.SourceIds is not { Count: <= 25 } || inventory.Movements is not { Count: <= 25 } ||
            inventory.SourceIds.Any(x => !PaymentReferences.Valid(x, "re_") && !CashMovement.DisputeReference(x)) || inventory.SourceIds.Distinct(StringComparer.Ordinal).Count() != inventory.SourceIds.Count ||
            inventory.Movements.Any(x => x is null || !inventory.SourceIds.Contains(x.SourceId, StringComparer.Ordinal)) || inventory.Movements.Select(x => x.TransactionId).Distinct(StringComparer.Ordinal).Count() != inventory.Movements.Count)
            return false;
        var capture = await db.SettlementPayments.AsNoTracking().Include(x => x.Allocations).SingleOrDefaultAsync(x => x.AttemptId == original.Id, token);
        if (capture is null || capture.Allocations.Count != original.Items.Count || capture.Allocations.Any(x => !original.Items.Any(i => i.ObligationId == x.ObligationId && i.AmountMinorUnits == x.AmountMinorUnits && i.AccountId == x.AccountId))) return false;
        foreach (var fact in inventory.Movements)
        { try { _ = CashMovement.Record(capture, original, fact, now); } catch (InvalidOperationException) { return false; } }
        if (inventory.Movements.Where(x => x.Kind == CashMovementKind.Refund).Sum(x => -x.SignedMinorUnits) != inventory.RefundedMinorUnits) return false;
        // New or missing money must be imported/investigated before a hold can be released. A subset cannot prove agreement.
        var movements = await db.CashMovements.AsNoTracking().Where(x => x.AttemptId == original.Id).Take(26).ToListAsync(token);
        if (movements.Count != inventory.Movements.Count || movements.Any(x => !inventory.Movements.Any(x.Matches)) ||
            await db.CashSourceWork.AnyAsync(x => x.AttemptId == original.Id && x.State != CollectionWorkState.Completed, token) || await db.CollectionGuards.AnyAsync(x => x.AttemptId == original.Id, token)) return false;
        var reviews = await db.CashMovementReviews.Where(x => db.CashMovements.Any(m => m.Id == x.MovementId && m.AttemptId == original.Id)).Take(26).ToListAsync(token);
        if (reviews.Count != movements.Count) return false;
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Type = "CapturedCashAgreementV1",
            inventory.ProviderAccount,
            inventory.LiveMode,
            inventory.AccountId,
            inventory.AttemptId,
            inventory.CustomerId,
            inventory.PaymentId,
            inventory.ChargeId,
            inventory.CapturedMinorUnits,
            inventory.RefundedMinorUnits,
            Sources = inventory.SourceIds.Order(StringComparer.Ordinal),
            Facts = inventory.Movements.OrderBy(x => x.TransactionId, StringComparer.Ordinal)
        })));
        var confirm = new List<Action>();
        foreach (var movement in movements)
        {
            var review = reviews.Single(x => x.MovementId == movement.Id); await db.Entry(review).ReloadAsync(token);
            if (review.VerifiedAt > inventory.CheckedAt) return false;
            if (movement.Kind == CashMovementKind.Refund)
            {
                if (review.State is not (CashMovementReviewState.AppliedPendingVerification or CashMovementReviewState.Classified) || review.OperationId is null || review.OperationId == Guid.Empty || review.Disposition is null || !Enum.IsDefined(review.Disposition.Value) || review.ActorId is null || review.AppliedAt is null || review.AppliedAt > inventory.CheckedAt) return false;
                var allocations = await db.RefundAllocations.AsNoTracking().Where(x => x.MovementId == movement.Id).Take(26).ToListAsync(token);
                if (allocations.Count is < 1 or > 25 || allocations.Sum(x => x.SignedMinorUnits) != movement.SignedMinorUnits) return false;
                var credits = await db.ObligationCredits.AsNoTracking().Where(x => x.SourceMovementId == movement.Id).Take(26).ToListAsync(token);
                if (review.Disposition == RefundDisposition.RetainDebt ? credits.Count != 0 : credits.Count != allocations.Count ||
                    credits.Any(x => x.OperationId != review.OperationId || x.ActorId != review.ActorId || !allocations.Any(a => a.ObligationId == x.ObligationId && -a.SignedMinorUnits == x.AmountMinorUnits))) return false;
                confirm.Add(() => review.ConfirmRefundAgreement(movement, hash, inventory.CheckedAt));
            }
            else
            {
                var source = movements.Where(x => x.SourceId == movement.SourceId).ToArray();
                if (review.State is not (CashMovementReviewState.Pending or CashMovementReviewState.Classified) || source.Length < 2 || source.Sum(x => x.SignedMinorUnits) != 0 ||
                    !source.Any(x => x.Kind == CashMovementKind.DisputeWithdrawal) || !source.Any(x => x.Kind == CashMovementKind.DisputeReinstatement)) return false;
                confirm.Add(() => review.ConfirmRestoredDispute(movement, source, hash, inventory.CheckedAt));
            }
        }
        // Validate remaining original funds and debt independently before mutating any review.
        foreach (var allocation in capture.Allocations)
        {
            if (db is MySqlOfficeDbContext) await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM obligations WHERE id = {allocation.ObligationId.ToString("D")} FOR UPDATE", token);
            var debt = await db.Obligations.AsNoTracking().SingleAsync(x => x.Id == allocation.ObligationId, token);
            var returnedOriginal = await db.RefundAllocations.Where(x => x.CaptureId == capture.Id && x.ObligationId == debt.Id).SumAsync(x => (long?)x.SignedMinorUnits, token) ?? 0;
            var paid = await db.PaymentAllocations.Where(x => x.ObligationId == debt.Id).SumAsync(x => (long?)x.AmountMinorUnits, token) ?? 0;
            var returned = await db.RefundAllocations.Where(x => x.ObligationId == debt.Id).SumAsync(x => (long?)x.SignedMinorUnits, token) ?? 0;
            var credited = await db.ObligationCredits.Where(x => x.ObligationId == debt.Id).SumAsync(x => (long?)x.AmountMinorUnits, token) ?? 0;
            if (allocation.AmountMinorUnits + returnedOriginal < 0 || credited > debt.AmountMinorUnits || paid + returned < 0 || debt.AmountMinorUnits - credited - paid - returned < 0) return false;
        }
        var issueId = await db.FinancialExceptions.Where(x => x.AttemptId == original.Id).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(token);
        if (issueId is null) return false;
        foreach (var apply in confirm) apply();
        db.FinancialExceptionHistory.Add(FinancialExceptionHistory.Record(issueId.Value, "CashAgreementVerified", null, hash, inventory.CheckedAt));
        return true;
    }
}
