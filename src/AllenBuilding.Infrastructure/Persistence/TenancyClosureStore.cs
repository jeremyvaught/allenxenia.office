using AllenBuilding.Application.Access;
using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Infrastructure.Persistence;

public sealed partial class BillingStore
{
    public async Task<IReadOnlyList<BillingClosureItem>> ClosuresAsync(Actor actor, CancellationToken cancellationToken)
    {
        if (!actor.ChallengedMfa) return [];
        var rows = await (from account in Accounts(actor, true)
                          join closure in db.TenancyClosures.AsNoTracking() on account.Id equals closure.AccountId
                          join member in db.Memberships on account.MembershipId equals member.Id
                          join suite in db.Suites on member.SuiteId equals suite.Id
                          join user in db.Users on member.UserId equals user.Id
                          where closure.State != BillingClosureState.Verified
                          orderby closure.RequestedAt, closure.AccountId
                          select new BillingClosureItem(account.Id, suite.Name, user.Email ?? "", closure.State, closure.RequestedAt, closure.Reason)).Take(50).ToListAsync(cancellationToken);
        var fresh = await FreshReader(actor, true, cancellationToken); if (fresh is null) return [];
        foreach (var row in rows) if (!await Accounts(fresh, true).AnyAsync(x => x.Id == row.AccountId, cancellationToken)) return [];
        return rows;
    }
    public async Task<BillingResult> EndAsync(Actor actor, EndTenancy command, string hash, CancellationToken cancellationToken)
    {
        await using var transaction = await AuthorityLocks.BeginAsync(db, cancellationToken);
        await AuthorityLocks.UserAsync(db, actor.UserId, cancellationToken);
        var owner = await (from account in db.RentAccounts.AsNoTracking()
                           join member in db.Memberships on account.MembershipId equals member.Id
                           join suite in db.Suites on member.SuiteId equals suite.Id
                           where account.Id == command.AccountId
                           select new { account.MembershipId, suite.BuildingId }).SingleOrDefaultAsync(cancellationToken);
        if (owner is null) return new(BillingCode.Unavailable);
        await AuthorityLocks.ManagerAsync(db, actor.UserId, owner.BuildingId, cancellationToken);
        // Match payment admission's membership-before-account order. No provider or tenant account lock is needed here.
        await AuthorityLocks.MembershipAsync(db, owner.MembershipId, cancellationToken);
        var accountToEnd = await Admit(actor, command.AccountId, cancellationToken);
        if (accountToEnd is null) return new(BillingCode.Unavailable);
        var replay = await Replay(actor.UserId, command.OperationId, hash, cancellationToken); if (replay is not null) return replay;
        var memberToEnd = await db.Memberships.SingleAsync(x => x.Id == owner.MembershipId, cancellationToken); await db.Entry(memberToEnd).ReloadAsync(cancellationToken);
        if (accountToEnd.Version != command.Version || memberToEnd.Version != command.MembershipVersion || accountToEnd.EndedAt is not null) return new(BillingCode.Conflict);
        var now = time.GetUtcNow();
        var closure = TenancyClosure.Request(command.OperationId, accountToEnd, memberToEnd.Version, actor.UserId, command.Reason, now);
        accountToEnd.EndTenancy(now); memberToEnd.Revoke(actor.UserId, command.Reason, now);
        var grant = await db.ActiveGrants.SingleOrDefaultAsync(x => x.MembershipId == owner.MembershipId, cancellationToken); if (grant is not null) db.ActiveGrants.Remove(grant);
        db.TenancyClosures.Add(closure);
        // Existing dispatched attempts, original references, guards and balances remain for canonical reconciliation.
        // Pending is a durable operational alert; saving it never means Stripe collection was stopped.
        audit.Audit(actor.UserId, "TenancyAccessEnded", owner.MembershipId.ToString("D"), command.Reason);
        return await Commit(transaction, actor, command.OperationId, hash, accountToEnd, accountToEnd.Id, "BillingClosurePending", cancellationToken);
    }
}
