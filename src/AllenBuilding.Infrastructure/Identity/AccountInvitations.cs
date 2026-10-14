using System.Text.Json;
using AllenBuilding.Application.Access;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Infrastructure.Identity;

public sealed class AccountInvitations(OfficeDbContext db, UserManager<OfficeUser> users,
    ICurrentActor current, IDataProtectionProvider protection, TimeProvider time)
{
    private readonly IDataProtector _messages = protection.CreateProtector("AllenOffice.AccountMessage.v1");

    public async Task<Guid?> IssueAsync(Guid suiteId, string email, CancellationToken cancellationToken = default)
    {
        var initial = await current.GetAsync(cancellationToken);
        if (initial is null) return null;
        await using var transaction = await AuthorityLocks.BeginAsync(db, cancellationToken);
        await AuthorityLocks.UserAsync(db, initial.UserId, cancellationToken);
        var suite = await db.Suites.AsNoTracking().SingleOrDefaultAsync(x => x.Id == suiteId, cancellationToken);
        if (suite is null) return null;
        await AuthorityLocks.ManagerAsync(db, initial.UserId, suite.BuildingId, cancellationToken);
        var actor = await current.GetAsync(cancellationToken);
        if (actor is not { ChallengedMfa: true, RecentMfa: true } || !actor.ManagedBuildings.Contains(suite.BuildingId) ||
            !await db.BuildingManagers.AnyAsync(x => x.UserId == actor.UserId && x.BuildingId == suite.BuildingId, cancellationToken))
            return null;
        email = email.Trim();
        if (email.Length > 256 || !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email)) return null;
        // Adding suites to existing accounts is a separate, authenticated grant flow; never overwrite a password.
        if (await users.FindByEmailAsync(email) is not null) return null;
        var user = new OfficeUser { Email = email, UserName = email, DisplayName = "Tenant" };
        if (!(await users.CreateAsync(user)).Succeeded) return null;
        var invitation = new Invitation { SuiteId = suite.Id, UserId = user.Id, IssuedBy = actor.UserId, ExpiresAt = time.GetUtcNow().AddDays(7) };
        invitation.TokenVersion = invitation.Version;
        var token = await users.GenerateUserTokenAsync(user, "Invitation", Purpose(invitation));
        db.Invitations.Add(invitation);
        Queue(user.Id, "Invitation", JsonSerializer.Serialize(new { invitationId = invitation.Id, version = invitation.TokenVersion, token }), suite.BuildingId);
        Audit(actor.UserId, "InvitationIssued", invitation.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return invitation.Id;
    }

    public async Task<bool> AcceptAsync(Guid invitationId, string token, string password, CancellationToken cancellationToken = default)
    {
        await using var transaction = await AuthorityLocks.BeginAsync(db, cancellationToken);
        var userId = await db.Invitations.AsNoTracking().Where(x => x.Id == invitationId).Select(x => x.UserId).SingleOrDefaultAsync(cancellationToken);
        if (userId is null) return false;
        await AuthorityLocks.InvitationUserAsync(db, userId, cancellationToken);
        await AuthorityLocks.InvitationAsync(db, invitationId, cancellationToken);
        var invitation = await db.Invitations.SingleOrDefaultAsync(x => x.Id == invitationId, cancellationToken);
        if (invitation is not null) await db.Entry(invitation).ReloadAsync(cancellationToken);
        if (invitation is null || invitation.AcceptedAt is not null || invitation.RevokedAt is not null || invitation.ExpiresAt <= time.GetUtcNow()) return false;
        var user = await users.FindByIdAsync(invitation.UserId);
        if (user is null || await users.HasPasswordAsync(user) ||
            !await users.VerifyUserTokenAsync(user, "Invitation", Purpose(invitation), token)) return false;
        try
        {
            if (!(await users.AddPasswordAsync(user, password)).Succeeded) return false;
            user.EmailConfirmed = true;
            if (!(await users.UpdateAsync(user)).Succeeded) return false;
            var membership = Membership.Grant(invitation.SuiteId, user.Id, time.GetUtcNow());
            invitation.AcceptedAt = time.GetUtcNow(); invitation.Version = Guid.NewGuid();
            db.Memberships.Add(membership);
            db.ActiveGrants.Add(new ActiveGrant { UserId = user.Id, SuiteId = membership.SuiteId, MembershipId = membership.Id });
            Audit(user.Id, "InvitationAccepted", invitation.Id.ToString());
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException) { return false; }
    }

    public async Task QueueResetAsync(string email)
    {
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is not { EmailConfirmed: true }) return;
        await using var transaction = await AuthorityLocks.BeginAsync(db, CancellationToken.None);
        await AuthorityLocks.InvitationUserAsync(db, user.Id, CancellationToken.None);
        await db.Entry(user).ReloadAsync();
        if (!user.EmailConfirmed) return;
        var token = await users.GeneratePasswordResetTokenAsync(user);
        // A new account request resolves old failed deliveries without replaying an expired reset credential.
        await db.AccountMessages.Where(x => x.UserId == user.Id && x.Kind == "PasswordReset" && x.State == MessageState.NeedsReview)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.State, MessageState.Suppressed)
                .SetProperty(x => x.LastError, "SupersededReset"));
        Queue(user.Id, "PasswordReset", JsonSerializer.Serialize(new { userId = user.Id, token }));
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    public async Task<bool> ResetAsync(string userId, string token, string password)
    {
        await using var transaction = await AuthorityLocks.BeginAsync(db, CancellationToken.None);
        await AuthorityLocks.InvitationUserAsync(db, userId, CancellationToken.None);
        var user = await users.FindByIdAsync(userId);
        if (user is not null) await db.Entry(user).ReloadAsync();
        if (user is not { EmailConfirmed: true }) return false;
        if (!(await users.ResetPasswordAsync(user, token, password)).Succeeded) return false;
        if (!user.TwoFactorEnabled && !(await users.ResetAuthenticatorKeyAsync(user)).Succeeded) return false;
        Audit(user.Id, "PasswordReset", user.Id);
        Queue(user.Id, "SecurityNotice", "{}");
        await db.SaveChangesAsync(); await transaction.CommitAsync();
        return true;
    }

    public void Queue(string userId, string kind, string payload, Guid? buildingId = null) => db.AccountMessages.Add(new AccountMessage
    { UserId = userId, BuildingId = buildingId, Kind = kind, ProtectedPayload = _messages.Protect(payload), CreatedAt = time.GetUtcNow(), NextAttemptAt = time.GetUtcNow() });

    public void Audit(string actor, string action, string subject, string? reason = null) => db.AccountAudits.Add(new AccountAudit
    { ActorId = actor, Action = action, SubjectId = subject, Reason = reason, OccurredAt = time.GetUtcNow() });
    private static string Purpose(Invitation invitation) => invitation.TokenVersion is { } version
        ? $"Invitation:{invitation.Id:D}:{version:D}" : $"Invitation:{invitation.Id:D}";

    public async Task<AccessCode> ChangeAsync(Guid invitationId, Guid version, bool revoke, string reason, CancellationToken cancellationToken = default)
    {
        if (invitationId == Guid.Empty || version == Guid.Empty || string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 2000) return AccessCode.ValidationFailed;
        var initial = await current.GetAsync(cancellationToken);
        if (initial is not { ChallengedMfa: true, RecentMfa: true }) return AccessCode.Unavailable;
        await using var transaction = await AuthorityLocks.BeginAsync(db, cancellationToken);
        await AuthorityLocks.UserAsync(db, initial.UserId, cancellationToken);
        var target = await (from invitation in db.Invitations.AsNoTracking()
                            join suite in db.Suites on invitation.SuiteId equals suite.Id
                            where invitation.Id == invitationId
                            select new { invitation.UserId, suite.BuildingId }).SingleOrDefaultAsync(cancellationToken);
        if (target is null) return AccessCode.Unavailable;
        await AuthorityLocks.ManagerAsync(db, initial.UserId, target.BuildingId, cancellationToken);
        await AuthorityLocks.InvitationUserAsync(db, target.UserId, cancellationToken);
        await AuthorityLocks.InvitationAsync(db, invitationId, cancellationToken);
        var actor = await current.GetAsync(cancellationToken);
        if (actor is not { ChallengedMfa: true, RecentMfa: true } || !actor.ManagedBuildings.Contains(target.BuildingId) ||
            !await db.BuildingManagers.AnyAsync(x => x.UserId == actor.UserId && x.BuildingId == target.BuildingId, cancellationToken)) return AccessCode.Unavailable;
        var invitationToChange = await db.Invitations.SingleAsync(x => x.Id == invitationId, cancellationToken);
        await db.Entry(invitationToChange).ReloadAsync(cancellationToken);
        if (invitationToChange.Version != version || invitationToChange.AcceptedAt is not null) return AccessCode.Conflict;
        var user = await users.FindByIdAsync(target.UserId);
        if (user is null || await users.HasPasswordAsync(user) || user.EmailConfirmed) return AccessCode.Conflict;
        invitationToChange.Version = Guid.NewGuid();
        if (revoke)
        {
            if (invitationToChange.RevokedAt is not null) return AccessCode.Conflict;
            invitationToChange.RevokedAt = time.GetUtcNow(); invitationToChange.RevokedBy = actor.UserId;
        }
        else
        {
            invitationToChange.RevokedAt = null; invitationToChange.RevokedBy = null;
            invitationToChange.IssuedBy = actor.UserId; invitationToChange.ExpiresAt = time.GetUtcNow().AddDays(7);
            invitationToChange.TokenVersion = invitationToChange.Version;
            var token = await users.GenerateUserTokenAsync(user, "Invitation", Purpose(invitationToChange));
            Queue(user.Id, "Invitation", JsonSerializer.Serialize(new { invitationId, version = invitationToChange.TokenVersion, token }), target.BuildingId);
        }
        Audit(actor.UserId, revoke ? "InvitationRevoked" : "InvitationReissued", invitationId.ToString("D"), reason.Trim());
        try { await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); return AccessCode.Success; }
        catch (DbUpdateConcurrencyException) { return AccessCode.Conflict; }
    }
}
