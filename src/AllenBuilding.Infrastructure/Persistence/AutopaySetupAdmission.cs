using AllenBuilding.Domain.Billing;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Infrastructure.Persistence;

// Shared current admission for durable setup work and the private browser handoff.
internal static class AutopaySetupAdmission
{
    public static async Task<bool> AllowsAsync(OfficeDbContext db, AutopayAuthorization auth, DateTimeOffset now, CancellationToken token, AutopayState expected = AutopayState.AwaitingMethod)
    {
        if (expected is not (AutopayState.AwaitingMethod or AutopayState.AwaitingProvider) || auth.State != expected || auth.StopOperationId is not null || now >= auth.FirstAnchorUtc ||
            expected == AutopayState.AwaitingProvider && (auth.SetupState != AutopaySetupState.Ready || auth.PaymentMethodId is null)) return false;
        var current = await (from account in db.RentAccounts.AsNoTracking()
                             join member in db.Memberships on account.MembershipId equals member.Id
                             join user in db.Users on member.UserId equals user.Id
                             where account.Id == auth.AccountId && account.EndedAt == null && member.UserId == auth.ActorId && member.RevokedAt == null &&
                               member.StartsAt <= now && (member.EndsAt == null || member.EndsAt > now) && user.EmailConfirmed && (user.LockoutEnd == null || user.LockoutEnd <= now) &&
                               db.ActiveGrants.Any(x => x.MembershipId == member.Id) && db.AutopaySlots.Any(x => x.AccountId == account.Id && x.AuthorizationId == auth.Id) &&
                               db.RentAgreements.Any(x => x.Id == auth.AgreementId && x.AccountId == account.Id && x.Version == auth.AgreementVersion && x.SupersededBy == null)
                             select account.Id).AnyAsync(token);
        if (!current) return false;
        if (await db.FinancialExceptions.AnyAsync(x => x.AccountId == auth.AccountId && x.State != FinancialExceptionState.Resolved, token) ||
            await db.TenancyClosures.AnyAsync(x => x.AccountId == auth.AccountId && x.State != BillingClosureState.Verified, token) ||
            await db.CashSourceWork.AnyAsync(x => x.AccountId == auth.AccountId && x.State != CollectionWorkState.Completed, token) ||
            await db.CollectionWork.AnyAsync(x => x.ReadOnlyReconciliation && x.State != CollectionWorkState.Completed && db.PaymentAttempts.Any(a => a.Id == x.AttemptId && a.AccountId == auth.AccountId), token) ||
            await db.PaymentAttempts.AnyAsync(x => x.AccountId == auth.AccountId && x.State == PaymentAttemptState.NeedsReview, token) ||
            await db.CashMovements.AnyAsync(x => x.AccountId == auth.AccountId && !db.CashMovementReviews.Any(r => r.MovementId == x.Id && r.State == CashMovementReviewState.Classified && r.VerifiedAt != null && r.AgreementHash != null && r.AgreementHash.Length == 64), token)) return false;
        return !await (from period in db.BillingPeriods.AsNoTracking()
                       join debt in db.Obligations on period.Id equals debt.PeriodId
                       where period.AccountId == auth.AccountId && period.StartsOn >= auth.FirstPeriodStartsOn && (auth.EndsBefore == null || period.StartsOn < auth.EndsBefore) &&
                         (debt.AmountMinorUnits != auth.MonthlyMinorUnits || debt.Currency != auth.Currency || debt.AgreementId != auth.AgreementId ||
                          db.ObligationCredits.Any(x => x.ObligationId == debt.Id) || db.PaymentAllocations.Any(x => x.ObligationId == debt.Id) || db.RefundAllocations.Any(x => x.ObligationId == debt.Id) ||
                          db.PaymentAttemptItems.Any(x => x.ObligationId == debt.Id) || db.CollectionGuards.Any(x => x.ObligationId == debt.Id))
                       select debt.Id).AnyAsync(token);
    }
}
