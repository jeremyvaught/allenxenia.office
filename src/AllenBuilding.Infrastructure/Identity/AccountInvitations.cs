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
        if (actor is not { RecentMfa: true } || suite is null || !actor.ManagedBuildings.Contains(suite.BuildingId))
            return null;
        email = email.Trim();
        if (email.Length > 256 || !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email)) return null;
        // Adding suites to existing accounts is a separate, authenticated grant flow; never overwrite a password.
        if (await users.FindByEmailAsync(email) is not null) return null;
        var user = new OfficeUser { Email = email, UserName = email, DisplayName = "Tenant" };
        if (!(await users.CreateAsync(user)).Succeeded) return null;
        var invitation = new Invitation { SuiteId = suite.Id, UserId = user.Id, IssuedBy = actor.UserId, ExpiresAt = time.GetUtcNow().AddDays(7) };
        var token = await users.GenerateUserTokenAsync(user, "Invitation", Purpose(invitation.Id));
        db.Invitations.Add(invitation);
        Queue(user.Id, "Invitation", JsonSerializer.Serialize(new { invitationId = invitation.Id, token }));
        Audit(actor.UserId, "InvitationIssued", invitation.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return invitation.Id;
    }

    public async Task<bool> AcceptAsync(Guid invitationId, string token, string password, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var invitation = await db.Invitations.SingleOrDefaultAsync(x => x.Id == invitationId, cancellationToken);
        if (invitation is null || invitation.AcceptedAt is not null || invitation.ExpiresAt <= time.GetUtcNow()) return false;
        var user = await users.FindByIdAsync(invitation.UserId);
        if (user is null || await users.HasPasswordAsync(user) ||
            !await users.VerifyUserTokenAsync(user, "Invitation", Purpose(invitation.Id), token)) return false;
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
        var token = await users.GeneratePasswordResetTokenAsync(user);
        Queue(user.Id, "PasswordReset", JsonSerializer.Serialize(new { userId = user.Id, token }));
        await db.SaveChangesAsync();
    }

    public async Task<bool> ResetAsync(string userId, string token, string password)
    {
        var user = await users.FindByIdAsync(userId);
        if (user is not { EmailConfirmed: true }) return false;
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (!(await users.ResetPasswordAsync(user, token, password)).Succeeded) return false;
        Audit(user.Id, "PasswordReset", user.Id);
        await db.SaveChangesAsync(); await transaction.CommitAsync();
        return true;
    }

    public void Queue(string userId, string kind, string payload) => db.AccountMessages.Add(new AccountMessage
    { UserId = userId, Kind = kind, ProtectedPayload = _messages.Protect(payload), CreatedAt = time.GetUtcNow() });

    public void Audit(string actor, string action, string subject) => db.AccountAudits.Add(new AccountAudit
    { ActorId = actor, Action = action, SubjectId = subject, OccurredAt = time.GetUtcNow() });
    private static string Purpose(Guid id) => $"Invitation:{id:D}";
}
