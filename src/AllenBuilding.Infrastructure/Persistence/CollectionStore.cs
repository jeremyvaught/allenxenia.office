using AllenBuilding.Application.Access;
using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Infrastructure.Persistence;

public sealed class CollectionStore(OfficeDbContext db, ICurrentActor current, CollectionPolicy policy, AccountInvitations audit, TimeProvider time) : ICollectionStore
{
    public async Task<IReadOnlyList<ReviewedCollectionBalance>> ReviewAsync(Actor actor, Guid accountId, IReadOnlyList<Guid> obligations, CancellationToken cancellationToken)
    {
        await using var transaction = await AuthorityLocks.BeginAsync(db, cancellationToken);
        if (!await Authorize(actor, accountId, cancellationToken)) return [];
        return await Balances(accountId, obligations, cancellationToken);
    }

    public async Task<CollectionResult> AdmitAsync(Actor actor, CollectReviewedBalances command, CancellationToken cancellationToken)
    {
        await using var transaction = await AuthorityLocks.BeginAsync(db, cancellationToken);
        if (!await Authorize(actor, command.AccountId, cancellationToken)) return new(CollectionCode.Unavailable);
        // Replays are checked only after current tenancy authorization, even for completed payments.
        var original = await db.PaymentAttempts.AsNoTracking().Include(x => x.Items)
            .SingleOrDefaultAsync(x => x.ActorId == actor.UserId && x.OperationId == command.OperationId, cancellationToken);
        if (original is not null)
        {
            var matches = original.AccountId == command.AccountId && original.ProviderAccount == policy.ProviderAccount && original.LiveMode == policy.LiveMode &&
                original.Items.Count == command.Items.Count && original.Items.All(x => command.Items.Any(y =>
                    y.ObligationId == x.ObligationId && y.Version == x.ReviewedVersion && y.RemainingMinorUnits == x.AmountMinorUnits && y.AccountId == x.AccountId && y.Currency == original.Currency));
            return matches ? new(CollectionCode.Success, original.Id, original.State) : new(CollectionCode.Conflict);
        }
        var binding = await db.BillingCustomers.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == command.AccountId &&
            x.ProviderAccount == policy.ProviderAccount && x.LiveMode == policy.LiveMode, cancellationToken);
        if (binding is null) return new(CollectionCode.Unavailable);
        var balances = await Balances(command.AccountId, command.Items.Select(x => x.ObligationId).ToArray(), cancellationToken);
        if (balances.Count != command.Items.Count || balances.Any(x => x.HasProviderInstruction || x.Held || !command.Items.Any(y =>
            y.ObligationId == x.ObligationId && y.Version == x.Version && y.RemainingMinorUnits == x.RemainingMinorUnits))) return new(CollectionCode.Conflict);
        PaymentAttempt attempt;
        try { attempt = PaymentAttempt.Admit(command.OperationId, actor.UserId, binding, balances, time.GetUtcNow()); }
        catch (ArgumentException) { return new(CollectionCode.ValidationFailed); }
        catch (OverflowException) { return new(CollectionCode.ValidationFailed); }
        db.PaymentAttempts.Add(attempt);
        foreach (var item in attempt.Items) db.CollectionGuards.Add(new CollectionGuard { AttemptId = attempt.Id, AccountId = attempt.AccountId, ObligationId = item.ObligationId });
        audit.Audit(actor.UserId, "CollectionAdmitted", command.AccountId.ToString("D"), "Attempt " + attempt.Id.ToString("D"));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(CollectionCode.Success, attempt.Id, attempt.State);
        }
        catch (DbUpdateConcurrencyException) { return new(CollectionCode.Conflict); }
        catch (DbUpdateException e) when (e.InnerException is MySql.Data.MySqlClient.MySqlException { Number: 1062 } or Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 })
        { return new(CollectionCode.Conflict); }
    }

    private async Task<bool> Authorize(Actor actor, Guid accountId, CancellationToken cancellationToken)
    {
        if (!policy.Enabled || !PaymentReferences.Valid(policy.ProviderAccount, "acct_")) return false;
        await AuthorityLocks.UserAsync(db, actor.UserId, cancellationToken);
        var membershipId = await db.RentAccounts.AsNoTracking().Where(x => x.Id == accountId).Select(x => (Guid?)x.MembershipId).SingleOrDefaultAsync(cancellationToken);
        if (membershipId is null) return false;
        await AuthorityLocks.MembershipAsync(db, membershipId.Value, cancellationToken);
        if (db is MySqlOfficeDbContext)
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM rent_accounts WHERE id = {accountId.ToString("D")} FOR UPDATE", cancellationToken);
        var fresh = await current.GetAsync(cancellationToken);
        if (fresh is null || fresh.UserId != actor.UserId || (fresh.ManagedBuildings.Count > 0 && !fresh.ChallengedMfa)) return false;
        var now = time.GetUtcNow();
        return await db.Memberships.AsNoTracking().AnyAsync(x => x.Id == membershipId && x.UserId == actor.UserId && x.RevokedAt == null &&
            x.StartsAt <= now && (x.EndsAt == null || x.EndsAt > now) && db.ActiveGrants.Any(g => g.MembershipId == x.Id), cancellationToken);
    }

    private async Task<IReadOnlyList<ReviewedCollectionBalance>> Balances(Guid accountId, IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        foreach (var id in ids.OrderBy(x => x.ToString("D"), StringComparer.Ordinal))
            if (db is MySqlOfficeDbContext)
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM obligations WHERE id = {id.ToString("D")} AND account_id = {accountId.ToString("D")} FOR UPDATE", cancellationToken);
        // Separate aggregate per obligation: joins cannot multiply cash allocations.
        // Credits/refunds/holds must extend this ledger before their capabilities are enabled.
        return await db.Obligations.AsNoTracking().Where(x => x.AccountId == accountId && ids.Contains(x.Id))
            .OrderBy(x => x.Id).Select(x => new ReviewedCollectionBalance(x.Id, x.AccountId, x.Version,
                x.AmountMinorUnits - (db.PaymentAllocations.Where(a => a.ObligationId == x.Id).Sum(a => (long?)a.AmountMinorUnits) ?? 0), x.Currency,
                db.CollectionGuards.Any(g => g.ObligationId == x.Id), db.PaymentAttemptItems.Any(i => i.ObligationId == x.Id &&
                    db.PaymentAttempts.Any(a => a.Id == i.AttemptId && a.State == PaymentAttemptState.NeedsReview)))).ToListAsync(cancellationToken);
    }
}
