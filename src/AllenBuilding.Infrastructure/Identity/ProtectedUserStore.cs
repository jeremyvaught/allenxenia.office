using System.Text.Json;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;

namespace AllenBuilding.Infrastructure.Identity;

public sealed class ProtectedUserStore(OfficeDbContext context, IDataProtectionProvider protection,
    IPasswordHasher<OfficeUser> hasher) : UserStore<OfficeUser, IdentityRole, OfficeDbContext>(context)
{
    private readonly IDataProtector _seeds = protection.CreateProtector("AllenOffice.AuthenticatorSeed.v1");

    public override Task SetAuthenticatorKeyAsync(OfficeUser user, string key, CancellationToken cancellationToken)
        => base.SetAuthenticatorKeyAsync(user, _seeds.Protect(key), cancellationToken);

    public override async Task<string?> GetAuthenticatorKeyAsync(OfficeUser user, CancellationToken cancellationToken)
    {
        var protectedKey = await base.GetAuthenticatorKeyAsync(user, cancellationToken);
        return protectedKey is null ? null : _seeds.Unprotect(protectedKey);
    }

    public override Task ReplaceCodesAsync(OfficeUser user, IEnumerable<string> recoveryCodes, CancellationToken cancellationToken)
        => SetTokenAsync(user, "Office", "RecoveryHashes", JsonSerializer.Serialize(
            recoveryCodes.Select(code => hasher.HashPassword(user, code))), cancellationToken);

    public override async Task<bool> RedeemCodeAsync(OfficeUser user, string code, CancellationToken cancellationToken)
    {
        var hashes = await ReadHashes(user, cancellationToken);
        var index = hashes.FindIndex(hash => hasher.VerifyHashedPassword(user, hash, code) != PasswordVerificationResult.Failed);
        if (index < 0) return false;
        hashes.RemoveAt(index);
        user.MustReplaceRecoveryCodes = true;
        // UserManager subsequently updates the user's concurrency stamp and saves both rows atomically.
        await SetTokenAsync(user, "Office", "RecoveryHashes", JsonSerializer.Serialize(hashes), cancellationToken);
        return true;
    }

    public override async Task<int> CountCodesAsync(OfficeUser user, CancellationToken cancellationToken)
        => (await ReadHashes(user, cancellationToken)).Count;

    private async Task<List<string>> ReadHashes(OfficeUser user, CancellationToken cancellationToken)
    {
        var value = await GetTokenAsync(user, "Office", "RecoveryHashes", cancellationToken);
        return value is null ? [] : JsonSerializer.Deserialize<List<string>>(value)!;
    }
}
