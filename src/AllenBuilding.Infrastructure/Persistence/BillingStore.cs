using AllenBuilding.Application.Access;
using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AllenBuilding.Infrastructure.Persistence;

public sealed partial class BillingStore(OfficeDbContext db, ICurrentActor current, AccountInvitations audit, TimeProvider time) : IBillingStore
{
    public async Task<IReadOnlyList<BillingAccount>> ReadAsync(Actor actor, bool management, CancellationToken cancellationToken, Guid? accountId, Guid? membershipId = null)
    {
        var query = from account in Accounts(actor, management)
                    join membership in db.Memberships on account.MembershipId equals membership.Id
                    join suite in db.Suites on membership.SuiteId equals suite.Id
                    join user in db.Users on membership.UserId equals user.Id
                    where accountId == null || account.Id == accountId
                    where membershipId == null || membership.Id == membershipId
                    orderby suite.Name, account.Id
                    select new { Account = account, Suite = suite.Name, user.Email, MembershipVersion = membership.Version };
        var rows = await query.Take(50).ToListAsync(cancellationToken);
        var result = new List<BillingAccount>();
        foreach (var row in rows)
        {
            var terms = await db.RentAgreements.AsNoTracking().Where(x => x.AccountId == row.Account.Id)
                .OrderByDescending(x => x.StartsOn).ThenBy(x => x.Id).Take(50)
                .Select(x => new RentTermsView(x.Id, x.Version, x.AmountMinorUnits, x.Currency, x.DueDay, x.StartsOn, x.EndsOn, x.SupersededBy)).ToListAsync(cancellationToken);
            var charges = await (from charge in db.Obligations.AsNoTracking()
                                 join period in db.BillingPeriods on charge.PeriodId equals (Guid?)period.Id into periods
                                 from period in periods.DefaultIfEmpty()
                                 where charge.AccountId == row.Account.Id
                                 orderby charge.DueOn descending, charge.Id descending
                                 select new ObligationView(charge.Id, charge.Version, charge.Kind, charge.Description, charge.AmountMinorUnits,
                                     charge.Currency, charge.DueOn, period == null ? null : (DateOnly?)period.StartsOn, period == null ? null : (DateOnly?)period.EndsOn,
                                     db.ObligationCredits.Where(c => c.ObligationId == charge.Id).Sum(c => (long?)c.AmountMinorUnits) ?? 0))
                .Take(100).ToListAsync(cancellationToken);
            var closure = management ? await db.TenancyClosures.AsNoTracking().Where(x => x.AccountId == row.Account.Id)
                .Select(x => new BillingClosureView(x.State, x.RequestedAt, x.Reason)).SingleOrDefaultAsync(cancellationToken) : null;
            result.Add(new(row.Account.Id, row.Account.Version, row.Suite, terms, charges, false, row.Account.MembershipId, management ? row.Email : null, row.MembershipVersion, row.Account.EndedAt, closure));
        }
        if (result.Count == 0) return [];
        var fresh = await FreshReader(actor, management, cancellationToken); if (fresh is null) return [];
        // A bounded scalar recheck also avoids the MySQL provider's unmapped Guid-array parameter path.
        var released = new List<BillingAccount>();
        foreach (var row in result)
            if (await Accounts(fresh, management).AnyAsync(x => x.Id == row.Id, cancellationToken)) released.Add(row);
        return released;
    }

    private IQueryable<RentAccount> Accounts(Actor actor, bool management)
    {
        var now = time.GetUtcNow();
        return from account in db.RentAccounts.AsNoTracking()
               join membership in db.Memberships on account.MembershipId equals membership.Id
               join suite in db.Suites on membership.SuiteId equals suite.Id
               where management
                   ? actor.ManagedBuildings.Contains(suite.BuildingId) && db.BuildingManagers.Any(x => x.BuildingId == suite.BuildingId && x.UserId == actor.UserId)
                   : membership.UserId == actor.UserId && membership.RevokedAt == null && membership.StartsAt <= now && (membership.EndsAt == null || membership.EndsAt > now) &&
                       db.ActiveGrants.Any(x => x.MembershipId == membership.Id)
               orderby suite.Name, account.Id
               select account;
    }

    public async Task<IReadOnlyList<RentCandidate>> CandidatesAsync(Actor actor, CancellationToken cancellationToken)
    {
        var rows = await (from membership in CandidateMemberships(actor)
                          join suite in db.Suites on membership.SuiteId equals suite.Id
                          join user in db.Users on membership.UserId equals user.Id
                          orderby suite.Name, membership.Id
                          select new RentCandidate(membership.Id, suite.Name, user.Email!)).Take(100).ToListAsync(cancellationToken);
        if (rows.Count == 0) return [];
        var fresh = await FreshReader(actor, true, cancellationToken); if (fresh is null) return [];
        var released = new List<RentCandidate>();
        foreach (var row in rows)
            if (await CandidateMemberships(fresh).AnyAsync(x => x.Id == row.MembershipId, cancellationToken)) released.Add(row);
        return released;
    }
    private IQueryable<Membership> CandidateMemberships(Actor actor)
    {
        var now = time.GetUtcNow();
        return from membership in db.Memberships.AsNoTracking()
               join grant in db.ActiveGrants on membership.Id equals grant.MembershipId
               join suite in db.Suites on membership.SuiteId equals suite.Id
               where actor.ManagedBuildings.Contains(suite.BuildingId) && db.BuildingManagers.Any(x => x.BuildingId == suite.BuildingId && x.UserId == actor.UserId) &&
                   membership.RevokedAt == null && membership.StartsAt <= now && (membership.EndsAt == null || membership.EndsAt > now) &&
                   !db.RentAccounts.Any(x => x.MembershipId == membership.Id)
               select membership;
    }
    private async Task<Actor?> FreshReader(Actor actor, bool management, CancellationToken token)
    {
        var fresh = await current.GetAsync(token);
        return fresh is null || fresh.UserId != actor.UserId || ((management || fresh.ManagedBuildings.Count > 0) && !fresh.ChallengedMfa) ? null : fresh;
    }
    private async Task<RentPreview?> ReleasePreview(Actor actor, RentPreview preview, CancellationToken token)
    {
        var fresh = await FreshReader(actor, true, token);
        return fresh is not null && await Accounts(fresh, true).AnyAsync(x => x.Id == preview.AccountId, token) ? preview : null;
    }

    public async Task<RentPreview?> PreviewRentAsync(Actor actor, Guid accountId, int year, int month, CancellationToken cancellationToken)
    {
        var account = await Accounts(actor, true).SingleOrDefaultAsync(x => x.Id == accountId, cancellationToken);
        if (account is null) return null;
        var monthPeriod = RentCalendar.ForMonth(account.Id, year, month, 1);
        var existing = await (from period in db.BillingPeriods.AsNoTracking()
                              join charge in db.Obligations on period.Id equals charge.PeriodId
                              where period.AccountId == account.Id && period.StartsOn == monthPeriod.StartsOn && period.EndsOn == monthPeriod.EndsOn && period.Kind == ChargeKind.BaseRent
                              select charge).SingleOrDefaultAsync(cancellationToken);
        if (existing is not null) return await ReleasePreview(actor, new(account.Id, account.Version, existing.AmountMinorUnits, existing.DueOn, monthPeriod.StartsOn, monthPeriod.EndsOn, true), cancellationToken);
        var terms = await db.RentAgreements.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == account.Id && x.StartsOn <= monthPeriod.StartsOn && (x.EndsOn == null || x.EndsOn >= monthPeriod.EndsOn), cancellationToken);
        if (terms is null || account.EndedAt is not null) return null;
        var calendar = RentCalendar.ForMonth(account.Id, year, month, terms.DueDay);
        return await ReleasePreview(actor, new(account.Id, account.Version, terms.AmountMinorUnits, calendar.DueOn, calendar.StartsOn, calendar.EndsOn, false), cancellationToken);
    }

    public async Task<BillingResult> OpenAsync(Actor actor, OpenRentAccount command, string hash, CancellationToken cancellationToken)
    {
        await using var transaction = await AuthorityLocks.BeginAsync(db, cancellationToken);
        await AuthorityLocks.UserAsync(db, actor.UserId, cancellationToken);
        var building = await (from membership in db.Memberships
                              join suite in db.Suites on membership.SuiteId equals suite.Id
                              where membership.Id == command.MembershipId
                              select (Guid?)suite.BuildingId).SingleOrDefaultAsync(cancellationToken);
        if (building is null) return new(BillingCode.Unavailable);
        await AuthorityLocks.ManagerAsync(db, actor.UserId, building.Value, cancellationToken);
        await AuthorityLocks.MembershipAsync(db, command.MembershipId, cancellationToken);
        if (!await FreshManager(actor, building.Value, cancellationToken)) return new(BillingCode.Unavailable);
        var replay = await Replay(actor.UserId, command.OperationId, hash, cancellationToken);
        if (replay is not null) return replay;
        var now = time.GetUtcNow();
        if (!await db.Memberships.AnyAsync(x => x.Id == command.MembershipId && x.RevokedAt == null && x.StartsAt <= now &&
            (x.EndsAt == null || x.EndsAt > now), cancellationToken) || !await db.ActiveGrants.AnyAsync(x => x.MembershipId == command.MembershipId, cancellationToken))
            return new(BillingCode.Unavailable);
        if (await db.RentAccounts.AnyAsync(x => x.Id == command.AccountId || x.MembershipId == command.MembershipId, cancellationToken)) return new(BillingCode.Conflict);
        var account = RentAccount.Open(command.AccountId, command.MembershipId); db.RentAccounts.Add(account);
        return await Commit(transaction, actor, command.OperationId, hash, account, account.Id, "RentAccountOpened", cancellationToken);
    }

    public async Task<BillingResult> TermsAsync(Actor actor, SetRentTerms command, string hash, CancellationToken cancellationToken)
    {
        await using var transaction = await AuthorityLocks.BeginAsync(db, cancellationToken);
        var account = await Admit(actor, command.AccountId, cancellationToken);
        if (account is null) return new(BillingCode.Unavailable);
        var replay = await Replay(actor.UserId, command.OperationId, hash, cancellationToken); if (replay is not null) return replay;
        if (account.Version != command.Version || account.EndedAt is not null) return new(BillingCode.Conflict);
        var agreements = await db.RentAgreements.Where(x => x.AccountId == account.Id).ToListAsync(cancellationToken);
        foreach (var agreement in agreements) await db.Entry(agreement).ReloadAsync(cancellationToken);
        var replacement = RentAgreement.Create(account.Id, new Money(command.AmountMinorUnits), command.DueDay, command.StartsOn, command.EndsOn, actor.UserId, time.GetUtcNow());
        var prior = command.ReplacesId is { } id ? agreements.SingleOrDefault(x => x.Id == id) : null;
        if (command.ReplacesId is not null && prior is null) return new(BillingCode.Unavailable);
        if (agreements.Any(x => (x.EndsOn is null || x.EndsOn > x.StartsOn) && x.Id != prior?.Id && x.StartsOn < (replacement.EndsOn ?? DateOnly.MaxValue) && replacement.StartsOn < (x.EndsOn ?? DateOnly.MaxValue)))
            return new(BillingCode.Conflict);
        try { prior?.Supersede(replacement); } catch (InvalidOperationException) { return new(BillingCode.Conflict); }
        // Changed terms cannot silently amend the original tenant consent. Preserve an existing stop, otherwise queue a stop under this manager action.
        var activeAutopay = await (from slot in db.AutopaySlots join auth in db.AutopayAuthorizations on slot.AuthorizationId equals auth.Id where slot.AccountId == account.Id select auth).SingleOrDefaultAsync(cancellationToken);
        if (activeAutopay is not null)
        {
            await db.Entry(activeAutopay).ReloadAsync(cancellationToken);
            if (activeAutopay.State is not (AutopayState.Stopped or AutopayState.StopPending))
            {
                activeAutopay.RequestStop(Guid.NewGuid(), actor.UserId, AutopayStopTiming.Immediately, time.GetUtcNow());
                var originalSetup = await db.AutopayWork.AsNoTracking().SingleAsync(x => x.AuthorizationId == activeAutopay.Id && x.Stage == AutopayWorkStage.Setup, cancellationToken);
                db.AutopayWork.Add(AutopayWork.Enqueue(activeAutopay, AutopayWorkStage.Stop, time.GetUtcNow(), originalSetup.Profile));
                audit.Audit(actor.UserId, "AutopayStopForChangedTerms", activeAutopay.Id.ToString("D"), "Original agreement " + activeAutopay.AgreementId.ToString("D"));
            }
        }
        account.TermsChanged(); db.RentAgreements.Add(replacement);
        return await Commit(transaction, actor, command.OperationId, hash, account, replacement.Id, prior is null ? "RentTermsSet" : "RentTermsReplaced", cancellationToken);
    }

    public async Task<BillingResult> ChargeAsync(Actor actor, IssueCharge command, string hash, CancellationToken cancellationToken)
    {
        await using var transaction = await AuthorityLocks.BeginAsync(db, cancellationToken);
        var account = await Admit(actor, command.AccountId, cancellationToken);
        if (account is null) return new(BillingCode.Unavailable);
        var replay = await Replay(actor.UserId, command.OperationId, hash, cancellationToken); if (replay is not null) return replay;
        if (account.Version != command.Version || account.EndedAt is not null) return new(BillingCode.Conflict);
        var charge = Obligation.Issue(account.Id, new Money(command.AmountMinorUnits), command.Description, command.DueOn, actor.UserId, time.GetUtcNow());
        db.Obligations.Add(charge);
        return await Commit(transaction, actor, command.OperationId, hash, account, charge.Id, "ChargeIssued", cancellationToken);
    }

    public async Task<BillingResult> RentAsync(Actor actor, IssueRent command, string hash, CancellationToken cancellationToken)
    {
        await using var transaction = await AuthorityLocks.BeginAsync(db, cancellationToken);
        var account = await Admit(actor, command.AccountId, cancellationToken);
        if (account is null) return new(BillingCode.Unavailable);
        var replay = await Replay(actor.UserId, command.OperationId, hash, cancellationToken); if (replay is not null) return replay;
        if (account.Version != command.Version || account.EndedAt is not null) return new(BillingCode.Conflict);
        var month = RentCalendar.ForMonth(account.Id, command.Year, command.Month, 1);
        var existing = await (from period in db.BillingPeriods
                              join charge in db.Obligations on period.Id equals charge.PeriodId
                              where period.AccountId == account.Id && period.StartsOn == month.StartsOn && period.EndsOn == month.EndsOn && period.Kind == ChargeKind.BaseRent
                              select charge).SingleOrDefaultAsync(cancellationToken);
        if (existing is not null) return await Commit(transaction, actor, command.OperationId, hash, account, existing.Id, "RentPeriodReused", cancellationToken);
        var terms = await db.RentAgreements.Where(x => x.AccountId == account.Id && x.StartsOn <= month.StartsOn && (x.EndsOn == null || x.EndsOn >= month.EndsOn))
            .SingleOrDefaultAsync(cancellationToken);
        if (terms is null) return new(BillingCode.NoFullPeriod);
        var calendar = RentCalendar.ForMonth(account.Id, command.Year, command.Month, terms.DueDay);
        var billingPeriod = BillingPeriod.Create(calendar); var obligation = Obligation.Rent(billingPeriod, terms, calendar, actor.UserId, time.GetUtcNow());
        db.AddRange(billingPeriod, obligation);
        return await Commit(transaction, actor, command.OperationId, hash, account, obligation.Id, "RentIssued", cancellationToken);
    }

    private async Task<RentAccount?> Admit(Actor actor, Guid accountId, CancellationToken cancellationToken)
    {
        await AuthorityLocks.UserAsync(db, actor.UserId, cancellationToken);
        var building = await (from account in db.RentAccounts
                              join membership in db.Memberships on account.MembershipId equals membership.Id
                              join suite in db.Suites on membership.SuiteId equals suite.Id
                              where account.Id == accountId
                              select (Guid?)suite.BuildingId).SingleOrDefaultAsync(cancellationToken);
        if (building is null) return null;
        await AuthorityLocks.ManagerAsync(db, actor.UserId, building.Value, cancellationToken);
        if (db is MySqlOfficeDbContext)
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM rent_accounts WHERE id = {accountId.ToString("D")} FOR UPDATE", cancellationToken);
        if (!await FreshManager(actor, building.Value, cancellationToken)) return null;
        var loadedAccount = await db.RentAccounts.SingleAsync(x => x.Id == accountId, cancellationToken);
        // A scoped context may serve more than one tool/use case. Do not reuse a
        // previously tracked terms version after acquiring the account lock.
        await db.Entry(loadedAccount).ReloadAsync(cancellationToken);
        return loadedAccount;
    }
    private async Task<bool> FreshManager(Actor actor, Guid building, CancellationToken cancellationToken)
    {
        var fresh = await current.GetAsync(cancellationToken);
        return fresh is { ChallengedMfa: true, RecentMfa: true } && fresh.UserId == actor.UserId && fresh.ManagedBuildings.Contains(building) &&
            await db.BuildingManagers.AnyAsync(x => x.UserId == actor.UserId && x.BuildingId == building, cancellationToken);
    }
    private async Task<BillingResult?> Replay(string actor, Guid operation, string hash, CancellationToken cancellationToken)
    {
        var receipt = await db.BillingReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.ActorId == actor && x.OperationId == operation, cancellationToken);
        return receipt is null ? null : receipt.PayloadHash == hash
            ? new(BillingCode.Success, receipt.AccountId, receipt.EntityId, receipt.AccountVersion) : new(BillingCode.Conflict);
    }
    private async Task<BillingResult> Commit(IDbContextTransaction transaction, Actor actor, Guid operation, string hash, RentAccount account, Guid entityId, string action, CancellationToken cancellationToken)
    {
        db.BillingReceipts.Add(new BillingReceipt
        {
            ActorId = actor.UserId,
            OperationId = operation,
            PayloadHash = hash,
            AccountId = account.Id,
            EntityId = entityId,
            AccountVersion = account.Version,
            OccurredAt = time.GetUtcNow()
        });
        audit.Audit(actor.UserId, action, account.Id.ToString("D"), "Record " + entityId.ToString("D"));
        try
        {
            await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
            return new(BillingCode.Success, account.Id, entityId, account.Version);
        }
        catch (DbUpdateConcurrencyException) { return new(BillingCode.Conflict); }
        catch (DbUpdateException exception) when (exception.InnerException is MySql.Data.MySqlClient.MySqlException { Number: 1062 } or Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 })
        { return new(BillingCode.Conflict); }
    }
}
