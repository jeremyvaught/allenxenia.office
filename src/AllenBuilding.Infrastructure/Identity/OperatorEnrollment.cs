using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Infrastructure.Identity;

// Uses existing Identity token rows. Only the digest is persisted; the operator delivers
// the random credential through the independently verified recovery channel.
public sealed class OperatorEnrollment(OfficeDbContext db, UserManager<OfficeUser> users, TimeProvider time)
{
    public const string Provider = "Office.OperatorEnrollment";
    public const string RequiredToken = "Required";
    public const string CodeToken = "Code";
    public const string SessionTime = "office.operator_enrollment_time";
    public static string NewCode() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    public static bool ValidCode(string? code) => code is { Length: 32 } && code.All(Uri.IsHexDigit);
    public static string Digest(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code.ToUpperInvariant())));

    public Task<string?> RequiredAsync(OfficeUser user) => users.GetAuthenticationTokenAsync(user, Provider, RequiredToken);

    public async Task<bool> AuthorizeAsync(string userId, string stamp, string code, CancellationToken token)
    {
        if (!ValidCode(code)) return false;
        await using var transaction = await AuthorityLocks.BeginAsync(db, token);
        await AuthorityLocks.InvitationUserAsync(db, userId, token);
        var user = await users.FindByIdAsync(userId); if (user is null) return false;
        await db.Entry(user).ReloadAsync(token);
        if (user.SecurityStamp != stamp || user.TwoFactorEnabled || !user.EmailConfirmed || await users.IsLockedOutAsync(user) ||
            await RequiredAsync(user) != "true" || !await db.BuildingManagers.AnyAsync(x => x.UserId == userId, token)) return false;
        var value = await users.GetAuthenticationTokenAsync(user, Provider, CodeToken);
        var parts = value?.Split(':');
        if (parts is not { Length: 2 } || !long.TryParse(parts[0], CultureInfo.InvariantCulture, out var expires) ||
            expires <= time.GetUtcNow().ToUnixTimeSeconds() || parts[1].Length != 64 || !parts[1].All(Uri.IsHexDigit) ||
            !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(parts[1]), Convert.FromHexString(Digest(code)))) return false;
        if (!(await users.RemoveAuthenticationTokenAsync(user, Provider, CodeToken)).Succeeded ||
            !(await users.ResetAuthenticatorKeyAsync(user)).Succeeded) return false;
        await db.SaveChangesAsync(token); await transaction.CommitAsync(token); return true;
    }
}
