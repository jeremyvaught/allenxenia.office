using System.Net;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AllenBuilding.IntegrationTests;

// Pins the audit rows and security notices each account-security flow commits.
public sealed class AccountAuditTests
{
    [Fact]
    public async Task First_enrollment_records_enrollment_and_new_codes_but_a_rejected_code_records_nothing()
    {
        await using var host = new AccountTestHost();
        await host.InitializeAsync();
        var (id, _) = await host.SeedManagerAsync(enrolled: false);
        using var client = host.Browser();
        using var login = await AccountTestHost.PostAsync(client, "/Account/Login", "/Account/Login",
            ("Email", "manager@example.test"), ("Password", AccountTestHost.Password));
        await client.GetAsync("/Account/Enroll");
        using var bad = await AccountTestHost.PostAsync(client, "/Account/Enroll", "/Account/Enroll", ("Code", "000000"));
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Assert.Empty(await AuditsAsync(host, id));

        using var good = await AccountTestHost.PostAsync(client, "/Account/Enroll", "/Account/Enroll", ("Code", await host.FactorCodeAsync(id)));
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        Assert.Equal(["MfaEnrolled", "RecoveryCodesReplaced"], await AuditsAsync(host, id));
        Assert.Equal(1, await NoticesAsync(host, id));
    }

    [Fact]
    public async Task Recovery_sign_in_and_authenticator_reset_are_each_audited_with_a_notice()
    {
        await using var host = new AccountTestHost();
        await host.InitializeAsync();
        var (id, _) = await host.SeedManagerAsync();
        string code;
        using (var scope = host.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
            code = (await users.GenerateNewTwoFactorRecoveryCodesAsync((await users.FindByIdAsync(id))!, 1))!.Single();
        }
        using var client = host.Browser();
        using var login = await AccountTestHost.PostAsync(client, "/Account/Login", "/Account/Login",
            ("Email", "manager@example.test"), ("Password", AccountTestHost.Password));
        using var recovered = await AccountTestHost.PostAsync(client, "/Account/Factor", "/Account/Factor", ("Code", code), ("Recovery", "true"));
        Assert.Equal("/Account/RecoveryCodes", recovered.Headers.Location?.OriginalString);
        Assert.Equal(["RecoveryCodeUsed"], await AuditsAsync(host, id));
        Assert.Equal(1, await NoticesAsync(host, id));

        using var reset = await AccountTestHost.PostAsync(client, "/Account/RecoveryCodes", "/Account/RecoverAuthenticator",
            ("Password", AccountTestHost.Password));
        Assert.Equal("/Account/Login", reset.Headers.Location?.OriginalString);
        Assert.Equal(["AuthenticatorRecovered", "RecoveryCodeUsed"], await AuditsAsync(host, id));
        Assert.Equal(2, await NoticesAsync(host, id));
    }

    [Fact]
    public async Task Replacing_recovery_codes_is_audited_with_a_notice()
    {
        await using var host = new AccountTestHost();
        await host.InitializeAsync();
        var (id, _) = await host.SeedManagerAsync();
        using var client = host.Browser();
        await host.LoginManagerAsync(client, id);
        using var replaced = await AccountTestHost.PostAsync(client, "/Account/RecoveryCodes", "/Account/RecoveryCodes",
            ("Password", AccountTestHost.Password), ("Code", await host.FactorCodeAsync(id)));
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        Assert.Contains("Keep these codes safe", await replaced.Content.ReadAsStringAsync());
        Assert.Equal(["RecoveryCodesReplaced"], await AuditsAsync(host, id));
        Assert.Equal(1, await NoticesAsync(host, id));
    }

    private static async Task<string[]> AuditsAsync(AccountTestHost host, string userId)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
        return (await db.AccountAudits.AsNoTracking().Where(x => x.ActorId == userId && x.SubjectId == userId)
            .Select(x => x.Action).ToListAsync()).Order(StringComparer.Ordinal).ToArray();
    }

    private static async Task<int> NoticesAsync(AccountTestHost host, string userId)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
        return await db.AccountMessages.CountAsync(x => x.UserId == userId && x.Kind == "SecurityNotice");
    }
}
