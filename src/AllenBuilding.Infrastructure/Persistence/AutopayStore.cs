using AllenBuilding.Application.Access;
using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AllenBuilding.Infrastructure.Persistence;

// Pages and future assistant preparation share this coordinator. It never contacts a provider.
public sealed class AutopayStore(OfficeDbContext db, ICurrentActor current, AutopayPolicy policy, AccountInvitations audit, TimeProvider time) : IAutopayStore, IAutopayMethodStore
{
    public async Task<AutopayMethodSnapshot?> ReadAsync(Actor actor, Guid authorizationId, CancellationToken token)
    {
        if (!policy.Enabled || authorizationId == Guid.Empty) return null;
        var accountId = await db.AutopayAuthorizations.AsNoTracking().Where(x => x.Id == authorizationId && x.ActorId == actor.UserId).Select(x => (Guid?)x.AccountId).SingleOrDefaultAsync(token);
        if (accountId is null) return null;
        await using var tx = await AuthorityLocks.BeginAsync(db, token); var account = await Admit(actor, accountId.Value, token); if (account is null) return null;
        var auth = await db.AutopayAuthorizations.AsNoTracking().SingleAsync(x => x.Id == authorizationId, token);
        var work = await db.AutopayWork.AsNoTracking().SingleOrDefaultAsync(x => x.AuthorizationId == auth.Id && x.Stage == AutopayWorkStage.Setup, token);
        if (auth.ActorId != actor.UserId || auth.ProviderAccount != policy.ProviderAccount || auth.LiveMode != policy.LiveMode || auth.SetupState != AutopaySetupState.Waiting ||
            auth.SetupSessionId is null || auth.SetupExpiresAt is null || auth.SetupExpiresAt <= time.GetUtcNow() || work is null || work.DispatchedAt is null || work.Profile is null || work.State is CollectionWorkState.NeedsReview or CollectionWorkState.Suppressed or CollectionWorkState.Completed ||
            !await AutopaySetupAdmission.AllowsAsync(db, auth, time.GetUtcNow(), token) || !await IsCurrent(actor, account, token)) return null;
        return new(auth, work, account.Version);
    }
    public async Task<AutopayConsentReview?> ReviewAsync(Actor actor, Guid accountId, Guid agreementId, CancellationToken token)
    {
        await using var tx = await AuthorityLocks.BeginAsync(db, token);
        var account = await Admit(actor, accountId, token); if (account is null) return null;
        var schedule = await EligibleSchedule(account, agreementId, token);
        if (schedule is null || !await IsCurrent(actor, account, token)) return null;
        var suite = await Suite(account, token);
        return new(Guid.NewGuid(), account.Id, account.Version, schedule, suite);
    }

    public async Task<IReadOnlyList<AutopayView>> ReadAsync(Actor actor, Guid? accountId, CancellationToken token)
    {
        if (!policy.Enabled) return [];
        var now = time.GetUtcNow();
        var rows = await (from auth in db.AutopayAuthorizations.AsNoTracking()
                          join account in db.RentAccounts on auth.AccountId equals account.Id
                          join member in db.Memberships on account.MembershipId equals member.Id
                          join suite in db.Suites on member.SuiteId equals suite.Id
                          where (accountId == null || account.Id == accountId) && account.EndedAt == null && member.UserId == actor.UserId &&
                            member.RevokedAt == null && member.StartsAt <= now && (member.EndsAt == null || member.EndsAt > now) &&
                            db.ActiveGrants.Any(x => x.MembershipId == member.Id) && auth.ProviderAccount == policy.ProviderAccount && auth.LiveMode == policy.LiveMode
                          orderby auth.ConsentedAt descending, auth.Id
                          select new AutopayView(auth.Id, account.Id, auth.Version, suite.Name, auth.State, auth.MonthlyMinorUnits,
                              auth.DueDay, auth.FirstPeriodStartsOn, auth.FirstDueOn, auth.EndsBefore, auth.StopEffectiveAt,
                              auth.State == AutopayState.AwaitingMethod && auth.StopOperationId == null && auth.SetupState == AutopaySetupState.Waiting && auth.SetupSessionId != null && auth.SetupExpiresAt > now)).Take(50).ToListAsync(token);
        var result = new List<AutopayView>();
        foreach (var row in rows)
        {
            var account = await db.RentAccounts.AsNoTracking().SingleAsync(x => x.Id == row.AccountId, token);
            if (await IsCurrent(actor, account, token)) result.Add(row);
        }
        return result;
    }

    public async Task<AutopayResult> ConfirmAsync(Actor actor, ConfirmAutopay command, string hash, CancellationToken token)
    {
        await using var tx = await AuthorityLocks.BeginAsync(db, token);
        var account = await Admit(actor, command.AccountId, token); if (account is null) return new(BillingCode.Unavailable);
        var replay = await Replay(actor, account.Id, command.OperationId, hash, token); if (replay is not null) return replay;
        if (account.Version != command.AccountVersion || !command.ExplicitlyAccepted) return new(BillingCode.Conflict);
        var schedule = await EligibleSchedule(account, command.Schedule.AgreementId, token);
        if (schedule is null || schedule != command.Schedule) return new(BillingCode.Conflict);
        var binding = await db.BillingCustomers.AsNoTracking().SingleAsync(x => x.AccountId == account.Id && x.ProviderAccount == policy.ProviderAccount && x.LiveMode == policy.LiveMode, token);
        var authorization = AutopayAuthorization.Consent(command.OperationId, actor.UserId, account, binding, schedule, command.ExplicitlyAccepted, time.GetUtcNow());
        var slot = await db.AutopaySlots.SingleOrDefaultAsync(x => x.AccountId == account.Id, token);
        if (slot is null) db.AutopaySlots.Add(new() { AccountId = account.Id, AuthorizationId = authorization.Id });
        else slot.AuthorizationId = authorization.Id;
        db.AutopayAuthorizations.Add(authorization); db.AutopayWork.Add(AutopayWork.Enqueue(authorization, AutopayWorkStage.Setup, time.GetUtcNow(), policy.Profile));
        account.AuthorizationChanged();
        return await Commit(tx, actor, command.OperationId, hash, account, authorization, "AutopayConsentRecorded", token);
    }

    public async Task<AutopayResult> StopAsync(Actor actor, StopAutopay command, string hash, CancellationToken token)
    {
        if (!policy.Enabled) return new(BillingCode.Unavailable);
        var accountId = await db.AutopayAuthorizations.AsNoTracking().Where(x => x.Id == command.AuthorizationId).Select(x => (Guid?)x.AccountId).SingleOrDefaultAsync(token);
        if (accountId is null) return new(BillingCode.Unavailable);
        await using var tx = await AuthorityLocks.BeginAsync(db, token);
        var account = await Admit(actor, accountId.Value, token); if (account is null) return new(BillingCode.Unavailable);
        var replay = await Replay(actor, account.Id, command.OperationId, hash, token); if (replay is not null) return replay;
        var authorization = await db.AutopayAuthorizations.SingleAsync(x => x.Id == command.AuthorizationId, token); await db.Entry(authorization).ReloadAsync(token);
        if (authorization.ProviderAccount != policy.ProviderAccount || authorization.LiveMode != policy.LiveMode) return new(BillingCode.Unavailable);
        if (authorization.Version != command.Version || authorization.State is AutopayState.StopPending or AutopayState.Stopped) return new(BillingCode.Conflict);
        authorization.RequestStop(command.OperationId, actor.UserId, command.Timing, time.GetUtcNow());
        var setup = await db.AutopayWork.AsNoTracking().SingleAsync(x => x.AuthorizationId == authorization.Id && x.Stage == AutopayWorkStage.Setup, token);
        db.AutopayWork.Add(AutopayWork.Enqueue(authorization, AutopayWorkStage.Stop, time.GetUtcNow(), setup.Profile)); account.AuthorizationChanged();
        return await Commit(tx, actor, command.OperationId, hash, account, authorization, "AutopayStopRequested", token);
    }

    private async Task<RentAccount?> Admit(Actor actor, Guid accountId, CancellationToken token)
    {
        if (!policy.Enabled || !PaymentReferences.Valid(policy.ProviderAccount, "acct_")) return null;
        await AuthorityLocks.UserAsync(db, actor.UserId, token);
        var membership = await db.RentAccounts.AsNoTracking().Where(x => x.Id == accountId).Select(x => (Guid?)x.MembershipId).SingleOrDefaultAsync(token); if (membership is null) return null;
        await AuthorityLocks.MembershipAsync(db, membership.Value, token); await CustomerProvisionQueue.LockAccount(db, accountId, token);
        var account = await db.RentAccounts.SingleAsync(x => x.Id == accountId, token); await db.Entry(account).ReloadAsync(token);
        return await IsCurrent(actor, account, token) ? account : null;
    }
    private async Task<bool> IsCurrent(Actor actor, RentAccount account, CancellationToken token)
    {
        var fresh = await current.GetAsync(token); var now = time.GetUtcNow();
        if (fresh is null || fresh.UserId != actor.UserId || fresh.ManagedBuildings.Count > 0 && !fresh.ChallengedMfa) return false;
        return await db.Users.AsNoTracking().AnyAsync(x => x.Id == actor.UserId && x.EmailConfirmed && (x.LockoutEnd == null || x.LockoutEnd <= now), token) &&
            await db.RentAccounts.AsNoTracking().AnyAsync(x => x.Id == account.Id && x.EndedAt == null, token) &&
            await db.Memberships.AsNoTracking().AnyAsync(x => x.Id == account.MembershipId && x.UserId == actor.UserId && x.RevokedAt == null && x.StartsAt <= now && (x.EndsAt == null || x.EndsAt > now) && db.ActiveGrants.Any(g => g.MembershipId == x.Id), token);
    }
    private async Task<AutopaySchedule?> EligibleSchedule(RentAccount account, Guid agreementId, CancellationToken token)
    {
        var active = await (from slot in db.AutopaySlots.AsNoTracking()
                            join auth in db.AutopayAuthorizations on slot.AuthorizationId equals auth.Id
                            where slot.AccountId == account.Id
                            select (AutopayState?)auth.State).SingleOrDefaultAsync(token);
        if (active is not null && active != AutopayState.Stopped ||
            !await db.BillingCustomers.AnyAsync(x => x.AccountId == account.Id && x.ProviderAccount == policy.ProviderAccount && x.LiveMode == policy.LiveMode, token) ||
            await db.TenancyClosures.AnyAsync(x => x.AccountId == account.Id && x.State != BillingClosureState.Verified, token) ||
            await db.FinancialExceptions.AnyAsync(x => x.AccountId == account.Id && x.State != FinancialExceptionState.Resolved, token) ||
            await db.CashSourceWork.AnyAsync(x => x.AccountId == account.Id && x.State != CollectionWorkState.Completed, token) ||
            await db.CollectionWork.AnyAsync(x => x.ReadOnlyReconciliation && x.State != CollectionWorkState.Completed && db.PaymentAttempts.Any(a => a.Id == x.AttemptId && a.AccountId == account.Id), token) ||
            await db.PaymentAttempts.AnyAsync(x => x.AccountId == account.Id && x.State == PaymentAttemptState.NeedsReview, token) ||
            await db.CashMovements.AnyAsync(x => x.AccountId == account.Id && !db.CashMovementReviews.Any(r => r.MovementId == x.Id && r.State == CashMovementReviewState.Classified && r.VerifiedAt != null && r.AgreementHash != null && r.AgreementHash.Length == 64), token)) return null;
        var terms = await db.RentAgreements.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == account.Id && x.Id == agreementId && x.SupersededBy == null, token); if (terms is null) return null;
        var local = TimeZoneInfo.ConvertTime(time.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById(AutopaySchedule.TimeZone)); var next = new DateOnly(local.Year, local.Month, 1).AddMonths(1);
        // Future periods with any payment, adjustment or earlier provider instruction are never silently billed again.
        var excluded = await (from period in db.BillingPeriods.AsNoTracking()
                              join debt in db.Obligations on period.Id equals debt.PeriodId
                              where period.AccountId == account.Id && period.StartsOn >= next && (terms.EndsOn == null || period.StartsOn < terms.EndsOn) &&
                                (db.ObligationCredits.Any(x => x.ObligationId == debt.Id) || db.PaymentAllocations.Any(x => x.ObligationId == debt.Id) ||
                                 db.RefundAllocations.Any(x => x.ObligationId == debt.Id) || db.CollectionGuards.Any(x => x.ObligationId == debt.Id) ||
                                 db.PaymentAttemptItems.Any(x => x.ObligationId == debt.Id) || db.ProviderRentInvoices.Any(x => x.ObligationId == debt.Id))
                              orderby period.StartsOn
                              select period.StartsOn).Distinct().Take(26).ToListAsync(token);
        if (excluded.Count > 25) return null;
        AutopaySchedule schedule;
        try { schedule = AutopaySchedule.Select(terms, time.GetUtcNow(), excluded); }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
        // A recurring subscription cannot skip an arbitrary later month. Require manual review instead of crossing it.
        if (excluded.Any(x => x >= schedule.FirstPeriodStartsOn)) return null;
        // An existing unmodified invoice can reuse its stable identity only if its frozen amount/terms match this schedule.
        if (await (from period in db.BillingPeriods.AsNoTracking()
                   join debt in db.Obligations on period.Id equals debt.PeriodId
                   where period.AccountId == account.Id && period.StartsOn >= schedule.FirstPeriodStartsOn && (schedule.EndsBefore == null || period.StartsOn < schedule.EndsBefore) &&
                     (debt.AmountMinorUnits != schedule.AmountMinorUnits || debt.Currency != schedule.Currency || debt.AgreementId != schedule.AgreementId)
                   select debt.Id).AnyAsync(token)) return null;
        return schedule;
    }
    private Task<string> Suite(RentAccount account, CancellationToken token) => (from member in db.Memberships join suite in db.Suites on member.SuiteId equals suite.Id where member.Id == account.MembershipId select suite.Name).SingleAsync(token);
    private async Task<AutopayResult?> Replay(Actor actor, Guid accountId, Guid operation, string hash, CancellationToken token)
    {
        var receipt = await db.BillingReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.ActorId == actor.UserId && x.OperationId == operation, token);
        return receipt is null ? null : receipt.PayloadHash == hash && receipt.AccountId == accountId ? new(BillingCode.Success, receipt.EntityId) : new(BillingCode.Conflict);
    }
    private async Task<AutopayResult> Commit(IDbContextTransaction tx, Actor actor, Guid operation, string hash, RentAccount account, AutopayAuthorization authorization, string action, CancellationToken token)
    {
        if (!await IsCurrent(actor, account, token)) return new(BillingCode.Unavailable);
        db.BillingReceipts.Add(new() { ActorId = actor.UserId, OperationId = operation, PayloadHash = hash, AccountId = account.Id, AccountVersion = account.Version, EntityId = authorization.Id, OccurredAt = time.GetUtcNow() });
        audit.Audit(actor.UserId, action, authorization.Id.ToString("D"), "Original rent account " + account.Id.ToString("D"));
        try { await db.SaveChangesAsync(token); await tx.CommitAsync(token); return new(BillingCode.Success, authorization.Id); }
        catch (DbUpdateConcurrencyException) { return new(BillingCode.Conflict); }
        catch (DbUpdateException e) when (e.InnerException is MySql.Data.MySqlClient.MySqlException { Number: 1062 } or Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 }) { return new(BillingCode.Conflict); }
    }
}
