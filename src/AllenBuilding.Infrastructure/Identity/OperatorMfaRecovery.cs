using AllenBuilding.Application.Access;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Infrastructure.Identity;

public sealed record OperatorRecoveryPolicy(bool Enabled, string OperatingSystemUser)
{
    public bool AllowsCurrentProcess => Enabled && OperatingSystemUser == Environment.UserName && !string.IsNullOrWhiteSpace(OperatingSystemUser);
}
public sealed record RecoverManagerFactor(string UserId, string ReviewedSecurityStamp, string VerificationReference, string Reason);

// Deployment-only entry point. No browser or assistant registration; identity verification is performed by the authorized human operator.
public sealed class OperatorMfaRecovery(OfficeDbContext db, UserManager<OfficeUser> users,
    AccountInvitations audit, OperatorRecoveryPolicy policy)
{
    public async Task<AccessCode> ExecuteAsync(RecoverManagerFactor command, CancellationToken cancellationToken = default)
    {
        if (!policy.AllowsCurrentProcess) return AccessCode.Unavailable;
        if (!Text(command.UserId, 450) || !Text(command.ReviewedSecurityStamp, 450) ||
            !Text(command.VerificationReference, 160) || !Text(command.Reason, 1500) || !Text(Environment.UserName, 160)) return AccessCode.ValidationFailed;
        await using var transaction = await AuthorityLocks.BeginAsync(db, cancellationToken);
        await AuthorityLocks.InvitationUserAsync(db, command.UserId, cancellationToken); // Exclusive user lock also serializes stamp/factor changes.
        var user = await users.FindByIdAsync(command.UserId);
        if (user is null) return AccessCode.Unavailable;
        await db.Entry(user).ReloadAsync(cancellationToken);
        var buildings = await db.BuildingManagers.AsNoTracking().Where(x => x.UserId == user.Id).Select(x => x.BuildingId).OrderBy(x => x).Take(33).ToArrayAsync(cancellationToken);
        if (!user.EmailConfirmed || string.IsNullOrWhiteSpace(user.PasswordHash) || buildings.Length is 0 or > 32) return AccessCode.Unavailable;
        foreach (var building in buildings) await AuthorityLocks.ManagerAsync(db, user.Id, building, cancellationToken);
        if (!await db.BuildingManagers.AnyAsync(x => x.UserId == user.Id, cancellationToken)) return AccessCode.Unavailable;
        if (user.SecurityStamp != command.ReviewedSecurityStamp || !user.TwoFactorEnabled) return AccessCode.Conflict;
        // These Identity saves remain inside the same transaction as the audit and protected security notice.
        if (!(await users.SetTwoFactorEnabledAsync(user, false)).Succeeded ||
            !(await users.ResetAuthenticatorKeyAsync(user)).Succeeded ||
            await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 0) is null) return AccessCode.Conflict;
        user.MustReplaceRecoveryCodes = true;
        if (!(await users.UpdateSecurityStampAsync(user)).Succeeded) return AccessCode.Conflict;
        var reference = command.VerificationReference.Trim();
        audit.Audit("deployment:" + Environment.UserName, "OperatorMfaRecovered", user.Id,
            "Verification reference: " + reference + "; Reason: " + command.Reason.Trim());
        audit.Queue(user.Id, "SecurityNotice", "{}", buildings[0]);
        try
        {
            await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); return AccessCode.Success;
        }
        catch (DbUpdateConcurrencyException) { return AccessCode.Conflict; }
    }

    private static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= maximum && !value.Any(char.IsControl);
}
