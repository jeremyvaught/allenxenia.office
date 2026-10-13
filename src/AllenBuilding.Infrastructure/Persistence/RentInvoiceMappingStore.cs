using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Infrastructure.Persistence;

// Canonical provider reads happen before entering this store. No tenant access is needed to
// reconcile an already-issued original invoice after revocation; no browser can submit these facts.
public sealed class RentInvoiceMappingStore(OfficeDbContext db, TimeProvider time) : IRentInvoiceMappingStore
{
    public async Task<InvoiceMappingResult> ApplyAsync(CanonicalRentInvoice fact, CancellationToken token)
    {
        if (fact is null || fact.AccountId == Guid.Empty || fact.AuthorizationId == Guid.Empty || fact.CheckedAt > time.GetUtcNow()) return new(InvoiceMappingCode.Invalid);
        await using var tx = await AuthorityLocks.BeginAsync(db, token);
        var original = await db.AutopayAuthorizations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == fact.AuthorizationId && x.AccountId == fact.AccountId, token);
        if (original is null) return new(InvoiceMappingCode.Unavailable);
        await AuthorityLocks.UserAsync(db, original.ActorId, token);
        var membership = await db.RentAccounts.AsNoTracking().Where(x => x.Id == fact.AccountId).Select(x => x.MembershipId).SingleAsync(token);
        await AuthorityLocks.MembershipAsync(db, membership, token); await CustomerProvisionQueue.LockAccount(db, fact.AccountId, token);
        var auth = await db.AutopayAuthorizations.AsNoTracking().SingleAsync(x => x.Id == original.Id, token);
        RentPeriod calendar;
        try { calendar = ProviderRentInvoice.Calendar(auth, fact); }
        catch (InvalidOperationException) { return new(InvoiceMappingCode.Invalid); }
        if (!await db.BillingCustomers.AnyAsync(x => x.Id == auth.CustomerBindingId && x.AccountId == auth.AccountId && x.ProviderAccount == auth.ProviderAccount && x.LiveMode == auth.LiveMode && x.CustomerId == auth.CustomerId, token)) return new(InvoiceMappingCode.Invalid);
        var mapping = await db.ProviderRentInvoices.SingleOrDefaultAsync(x => x.ProviderAccount == fact.ProviderAccount && x.LiveMode == fact.LiveMode && x.InvoiceId == fact.InvoiceId, token);
        if (mapping is not null)
        {
            await db.Entry(mapping).ReloadAsync(token);
            try { mapping.Observe(auth, fact); }
            catch (InvalidOperationException) { return new(InvoiceMappingCode.Conflict); }
        }
        else
        {
            var period = await db.BillingPeriods.SingleOrDefaultAsync(x => x.AccountId == auth.AccountId && x.StartsOn == calendar.StartsOn && x.EndsOn == calendar.EndsOn && x.Kind == ChargeKind.BaseRent, token);
            if (period is not null && await db.ProviderRentInvoices.AnyAsync(x => x.PeriodId == period.Id, token)) return new(InvoiceMappingCode.Conflict);
            var terms = await db.RentAgreements.AsNoTracking().SingleAsync(x => x.Id == auth.AgreementId && x.AccountId == auth.AccountId, token);
            if (!terms.Covers(calendar) || terms.AmountMinorUnits != auth.MonthlyMinorUnits || terms.Currency != auth.Currency || terms.DueDay != auth.DueDay) return new(InvoiceMappingCode.Conflict);
            var debt = period is null ? null : await db.Obligations.SingleOrDefaultAsync(x => x.PeriodId == period.Id, token);
            if (debt is not null && (await db.ObligationCredits.AnyAsync(x => x.ObligationId == debt.Id, token) || await db.PaymentAllocations.AnyAsync(x => x.ObligationId == debt.Id, token) ||
                await db.RefundAllocations.AnyAsync(x => x.ObligationId == debt.Id, token) || await db.PaymentAttemptItems.AnyAsync(x => x.ObligationId == debt.Id, token) ||
                await db.CollectionGuards.AnyAsync(x => x.ObligationId == debt.Id, token))) return new(InvoiceMappingCode.Conflict);
            if (period is null) { period = BillingPeriod.Create(calendar); db.BillingPeriods.Add(period); }
            if (debt is null) { debt = Obligation.Rent(period, terms, calendar, auth.ActorId, time.GetUtcNow()); db.Obligations.Add(debt); }
            try { mapping = ProviderRentInvoice.Map(auth, fact, period, debt); }
            catch (InvalidOperationException) { return new(InvoiceMappingCode.Conflict); }
            debt.FinancialStateChanged(); db.ProviderRentInvoices.Add(mapping);
            db.AccountAudits.Add(new AccountAudit { ActorId = "system", Action = "OriginalRentInvoiceMapped", SubjectId = mapping.Id.ToString("D"), Reason = "Original rent account " + auth.AccountId.ToString("D"), OccurredAt = time.GetUtcNow() });
        }
        try { await db.SaveChangesAsync(token); await tx.CommitAsync(token); return new(InvoiceMappingCode.Recorded, mapping.Id, mapping.ObligationId); }
        catch (DbUpdateException) { await tx.RollbackAsync(token); db.ChangeTracker.Clear(); return new(InvoiceMappingCode.Conflict); }
    }
}
