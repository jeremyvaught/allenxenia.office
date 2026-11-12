using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AllenBuilding.Infrastructure.Identity;

public sealed record EnrollmentOutcome(bool Succeeded, string[]? RecoveryCodes = null);

// Account-security writes behind the sign-in controller. The controller keeps the claim checks
// and issues cookies only after these commit. Audit saves after an identity change deliberately
// take no request token, so a disconnect cannot drop the audit row for a change already made.
public sealed class AccountSecurity(OfficeDbContext db, UserManager<OfficeUser> users, AccountInvitations invitations)
{
    public Task<bool> IsManagerAsync(string userId, CancellationToken cancellationToken) =>
        db.BuildingManagers.AnyAsync(x => x.UserId == userId, cancellationToken);

    // A signed-in person changes the name others see for them. It carries no authority, so no second factor is asked for.
    public async Task<bool> RenameAsync(string userId, string name)
    {
        if (PersonName.Normalize(name) is not { } displayName || PersonName.Of(displayName) is null)
            return false;
        var user = await users.FindByIdAsync(userId);
        if (user is null)
            return false;
        if (user.DisplayName == displayName)
            return true;
        user.DisplayName = displayName;
        if (!(await users.UpdateAsync(user)).Succeeded)
            return false;
        invitations.Audit(userId, "DisplayNameChanged", userId);
        await db.SaveChangesAsync();
        return true;
    }

    public Task ReloadAsync(OfficeUser user, CancellationToken cancellationToken) => db.Entry(user).ReloadAsync(cancellationToken);

    public async Task RecordRecoveryCodeUseAsync(string userId)
    {
        invitations.Audit(userId, "RecoveryCodeUsed", userId);
        invitations.Queue(userId, "SecurityNotice", "A recovery code was used to sign in. Replace your recovery codes.");
        await db.SaveChangesAsync();
    }

    // Locks the account and re-reads it; the caller checks claims against the fresh row, then commits.
    public async Task<IDbContextTransaction> LockForEnrollmentAsync(OfficeUser user, CancellationToken cancellationToken)
    {
        var transaction = await AuthorityLocks.BeginAsync(db, cancellationToken);
        await AuthorityLocks.InvitationUserAsync(db, user.Id, cancellationToken);
        await db.Entry(user).ReloadAsync(cancellationToken);
        return transaction;
    }

    // Runs inside LockForEnrollmentAsync's transaction. A failure leaves it uncommitted.
    public async Task<EnrollmentOutcome> CompleteEnrollmentAsync(OfficeUser user, bool firstSetup)
    {
        var failed = new EnrollmentOutcome(false);
        if (!(await users.SetTwoFactorEnabledAsync(user, true)).Succeeded)
            return failed;
        if (!(await users.RemoveAuthenticationTokenAsync(user, OperatorEnrollment.Provider, OperatorEnrollment.RequiredToken)).Succeeded)
            return failed;
        user.MustReplaceRecoveryCodes = true;
        if (!(await users.UpdateAsync(user)).Succeeded)
            return failed;
        invitations.Audit(user.Id, "MfaEnrolled", user.Id);
        if (!firstSetup)
        {
            await db.SaveChangesAsync();
            return new(true);
        }
        var codes = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))?.ToArray();
        if (codes is null)
            return failed;
        user.MustReplaceRecoveryCodes = false;
        if (!(await users.UpdateAsync(user)).Succeeded)
            return failed;
        invitations.Audit(user.Id, "RecoveryCodesReplaced", user.Id);
        invitations.Queue(user.Id, "SecurityNotice", "Your authenticator was enabled and recovery codes were created.");
        await db.SaveChangesAsync();
        return new(true, codes);
    }

    public async Task<bool> ResetAuthenticatorAsync(OfficeUser user)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (!(await users.SetTwoFactorEnabledAsync(user, false)).Succeeded ||
            !(await users.ResetAuthenticatorKeyAsync(user)).Succeeded ||
            (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 0)) is null)
            return false;
        invitations.Audit(user.Id, "AuthenticatorRecovered", user.Id);
        invitations.Queue(user.Id, "SecurityNotice", "Your authenticator was reset after recovery verification. Sign in and enroll a new device.");
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return true;
    }

    public async Task<string[]?> ReplaceRecoveryCodesAsync(OfficeUser user)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        var codes = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))?.ToArray();
        if (codes is null)
            return null;
        user.MustReplaceRecoveryCodes = false;
        if (!(await users.UpdateAsync(user)).Succeeded)
            return null;
        invitations.Audit(user.Id, "RecoveryCodesReplaced", user.Id);
        invitations.Queue(user.Id, "SecurityNotice", "Recovery codes were replaced.");
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return codes;
    }
}
