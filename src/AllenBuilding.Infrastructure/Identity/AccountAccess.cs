using System.Security.Cryptography;
using System.Text.Json;
using AllenBuilding.Application.Access;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Infrastructure.Identity;

// Existing-account grants do not change passwords, email confirmation or old memberships.
public sealed class AccountAccess(OfficeDbContext db, UserManager<OfficeUser> users, ICurrentActor current, AccountInvitations audit, TimeProvider time)
{
    public async Task<AccessResult> GrantAsync(GrantExistingAccount command, CancellationToken cancellationToken = default)
    {
        if (command.OperationId == Guid.Empty || command.SuiteId == Guid.Empty || string.IsNullOrWhiteSpace(command.Email) ||
            command.Email.Length > 256 || string.IsNullOrWhiteSpace(command.Reason) || command.Reason.Trim().Length > 2000) return new(AccessCode.ValidationFailed);
        var initial = await current.GetAsync(cancellationToken);
        if (initial is not { ChallengedMfa: true, RecentMfa: true }) return new(AccessCode.Unavailable);
        await using var transaction = await AuthorityLocks.BeginAsync(db, cancellationToken);
        await AuthorityLocks.UserAsync(db, initial.UserId, cancellationToken);
        var suite = await db.Suites.AsNoTracking().SingleOrDefaultAsync(x => x.Id == command.SuiteId, cancellationToken);
        if (suite is null) return new(AccessCode.Unavailable);
        await AuthorityLocks.ManagerAsync(db, initial.UserId, suite.BuildingId, cancellationToken);
        var actor = await current.GetAsync(cancellationToken);
        if (actor is not { ChallengedMfa: true, RecentMfa: true } || !actor.ManagedBuildings.Contains(suite.BuildingId) ||
            !await db.BuildingManagers.AnyAsync(x => x.UserId == actor.UserId && x.BuildingId == suite.BuildingId, cancellationToken)) return new(AccessCode.Unavailable);
        var normalized = users.NormalizeEmail(command.Email.Trim());
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { command.SuiteId, Email = normalized, Reason = command.Reason.Trim() })));
        var receipt = await db.AccessReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.ActorId == actor.UserId && x.OperationId == command.OperationId, cancellationToken);
        if (receipt is not null) return receipt.PayloadHash == hash ? new(AccessCode.Success, receipt.MembershipId) : new(AccessCode.Conflict);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.NormalizedEmail == normalized, cancellationToken);
        if (user is not { EmailConfirmed: true } || string.IsNullOrWhiteSpace(user.PasswordHash)) return new(AccessCode.Unavailable);
        await AuthorityLocks.UserAsync(db, user.Id, cancellationToken);
        if (!await db.Users.AnyAsync(x => x.Id == user.Id && x.NormalizedEmail == normalized && x.EmailConfirmed && x.PasswordHash != null, cancellationToken)) return new(AccessCode.Unavailable);
        var priorMembership = await db.ActiveGrants.AsNoTracking().Where(x => x.UserId == user.Id && x.SuiteId == suite.Id).Select(x => (Guid?)x.MembershipId).SingleOrDefaultAsync(cancellationToken);
        if (priorMembership is { } priorId) await AuthorityLocks.MembershipAsync(db, priorId, cancellationToken);
        if (db is MySqlOfficeDbContext)
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT membership_id FROM active_grants WHERE user_id = {user.Id} AND suite_id = {suite.Id.ToString("D")} FOR UPDATE", cancellationToken);
        var existing = await db.ActiveGrants.SingleOrDefaultAsync(x => x.UserId == user.Id && x.SuiteId == suite.Id, cancellationToken);
        if (existing is not null)
        {
            if (existing.MembershipId != priorMembership) return new(AccessCode.Conflict);
            var membership = await db.Memberships.AsNoTracking().SingleAsync(x => x.Id == existing.MembershipId, cancellationToken);
            if (membership.IsActive(time.GetUtcNow()) || (membership.RevokedAt is null && membership.StartsAt > time.GetUtcNow())) return new(AccessCode.Conflict);
            db.ActiveGrants.Remove(existing);
            // Free the unique grant slot inside this same transaction before inserting its replacement.
            await db.SaveChangesAsync(cancellationToken);
        }
        var grant = Membership.Grant(suite.Id, user.Id, time.GetUtcNow());
        db.Memberships.Add(grant); db.ActiveGrants.Add(new ActiveGrant { UserId = user.Id, SuiteId = suite.Id, MembershipId = grant.Id });
        db.AccessReceipts.Add(new AccessReceipt { ActorId = actor.UserId, OperationId = command.OperationId, PayloadHash = hash, MembershipId = grant.Id, OccurredAt = time.GetUtcNow() });
        audit.Audit(actor.UserId, "ExistingAccountGranted", grant.Id.ToString("D"), command.Reason.Trim());
        audit.Queue(user.Id, "SecurityNotice", "{}", suite.BuildingId);
        try { await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); return new(AccessCode.Success, grant.Id); }
        catch (DbUpdateException exception) when (exception.InnerException is MySql.Data.MySqlClient.MySqlException { Number: 1062 } or Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 })
        { return new(AccessCode.Conflict); }
    }
}
