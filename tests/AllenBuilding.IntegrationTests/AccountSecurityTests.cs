using System.Net;
using System.Text.Json;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AllenBuilding.IntegrationTests;

public sealed class AccountSecurityTests
{
    [Fact]
    public async Task Recovery_sign_in_requires_replenishment_and_can_recover_a_lost_device_without_email_bypass()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync();
        var (id, _) = await host.SeedManagerAsync(); string code;
        using (var scope = host.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
            code = (await users.GenerateNewTwoFactorRecoveryCodesAsync((await users.FindByIdAsync(id))!, 1))!.Single();
        }
        using var client = host.Browser();
        using var login = await AccountTestHost.PostAsync(client, "/Account/Login", "/Account/Login", ("Email", "manager@example.test"), ("Password", AccountTestHost.Password));
        using var recovered = await AccountTestHost.PostAsync(client, "/Account/Factor", "/Account/Factor", ("Code", code), ("Recovery", "true"));
        Assert.Equal("/Account/RecoveryCodes", recovered.Headers.Location?.OriginalString);
        using var denied = await client.GetAsync("/Management"); Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        using var reset = await AccountTestHost.PostAsync(client, "/Account/RecoveryCodes", "/Account/RecoverAuthenticator", ("Password", AccountTestHost.Password));
        Assert.Equal("/Account/Login", reset.Headers.Location?.OriginalString);
        using var scopeCheck = host.Services.CreateScope();
        var check = scopeCheck.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>(); var user = (await check.FindByIdAsync(id))!;
        Assert.False(user.TwoFactorEnabled); Assert.Equal(0, await check.CountRecoveryCodesAsync(user));
        using var blocked = await client.GetAsync("/Management"); Assert.Equal(HttpStatusCode.Redirect, blocked.StatusCode);
    }

    [Fact]
    public async Task Recent_password_and_factor_expire_independently_of_management_read_access()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync();
        var (id, building) = await host.SeedManagerAsync(); using var client = host.Browser();
        await host.LoginManagerAsync(client, id);
        host.Clock.Offset = TimeSpan.FromMinutes(6);
        using var read = await client.GetAsync("/Management"); Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var change = await AccountTestHost.PostAsync(client, "/Management", "/Management/AddSuite", ("BuildingId", building.ToString()), ("Name", "Suite A"));
        Assert.StartsWith("http://localhost/Account/Denied", change.Headers.Location?.OriginalString);
        using var scope = host.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().Suites.ToListAsync());
    }

    [Fact]
    public async Task Bootstrap_is_empty_only_and_password_session_cannot_manage_before_enrollment()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync();
        using (var scope = host.Services.CreateScope())
        {
            var bootstrap = scope.ServiceProvider.GetRequiredService<BootstrapManager>();
            await bootstrap.CreateAsync("manager@example.test", AccountTestHost.Password);
            await Assert.ThrowsAsync<InvalidOperationException>(() => bootstrap.CreateAsync("second@example.test", AccountTestHost.Password));
        }
        using var client = host.Browser();
        using var login = await AccountTestHost.PostAsync(client, "/Account/Login", "/Account/Login", ("Email", "manager@example.test"), ("Password", AccountTestHost.Password));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        using var portal = await client.GetAsync("/Portal");
        Assert.Equal("/Account/Enroll", portal.Headers.Location?.OriginalString);
        using var manager = await client.GetAsync("/Management");
        Assert.StartsWith("http://localhost/Account/Denied", manager.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Mfa_requires_valid_challenge_and_current_grant_and_security_stamp()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync();
        var (id, _) = await host.SeedManagerAsync();
        using var client = host.Browser();
        using var login = await AccountTestHost.PostAsync(client, "/Account/Login", "/Account/Login", ("Email", "manager@example.test"), ("Password", AccountTestHost.Password));
        using var before = await client.GetAsync("/Management");
        Assert.Equal(HttpStatusCode.Redirect, before.StatusCode);
        using var invalid = await AccountTestHost.PostAsync(client, "/Account/Factor", "/Account/Factor", ("Code", "invalid"));
        Assert.Contains("Unable to verify", await invalid.Content.ReadAsStringAsync());
        using var factor = await AccountTestHost.PostAsync(client, "/Account/Factor", "/Account/Factor", ("Code", await host.FactorCodeAsync(id)));
        Assert.Equal(HttpStatusCode.Redirect, factor.StatusCode);
        using var admitted = await client.GetAsync("/Management");
        Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
        Assert.Contains("no-store", admitted.Headers.CacheControl?.ToString());
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            db.BuildingManagers.Remove(await db.BuildingManagers.SingleAsync()); await db.SaveChangesAsync();
        }
        using var removed = await client.GetAsync("/Management");
        Assert.Equal(HttpStatusCode.Redirect, removed.StatusCode);
        using (var scope = host.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
            Assert.True((await users.UpdateSecurityStampAsync((await users.FindByIdAsync(id))!)).Succeeded);
        }
        using var revoked = await client.GetAsync("/Portal");
        Assert.StartsWith("http://localhost/Account/Login", revoked.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Enrollment_verifies_a_real_code_and_withholds_authority_until_recovery_codes_saved()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync();
        var (id, _) = await host.SeedManagerAsync(enrolled: false);
        using var client = host.Browser();
        using var login = await AccountTestHost.PostAsync(client, "/Account/Login", "/Account/Login", ("Email", "manager@example.test"), ("Password", AccountTestHost.Password));
        using var page = await client.GetAsync("/Account/Enroll");
        using var bad = await AccountTestHost.PostAsync(client, "/Account/Enroll", "/Account/Enroll", ("Code", "wrong"));
        Assert.Contains("Unable to verify", await bad.Content.ReadAsStringAsync());
        using var good = await AccountTestHost.PostAsync(client, "/Account/Enroll", "/Account/Enroll", ("Code", await host.FactorCodeAsync(id)));
        Assert.Equal("/Account/Login", good.Headers.Location?.OriginalString);
        await host.LoginManagerAsync(client, id);
        using var portal = await client.GetAsync("/Portal");
        Assert.Equal("/Account/RecoveryCodes", portal.Headers.Location?.OriginalString);
        using var denied = await client.GetAsync("/Management");
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        using var issued = await AccountTestHost.PostAsync(client, "/Account/RecoveryCodes", "/Account/RecoveryCodes",
            ("Password", AccountTestHost.Password), ("Code", await host.FactorCodeAsync(id)));
        Assert.Contains("Keep these codes safe", await issued.Content.ReadAsStringAsync());
        using var manager = await client.GetAsync("/Management"); Assert.Equal(HttpStatusCode.OK, manager.StatusCode);
    }

    [Fact]
    public async Task Secrets_are_protected_and_recovery_codes_are_hashed_and_single_use_under_concurrency()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync();
        var (id, _) = await host.SeedManagerAsync();
        string code;
        using (var scope = host.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
            var user = (await users.FindByIdAsync(id))!;
            var key = await users.GetAuthenticatorKeyAsync(user);
            var codes = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 2))!.ToArray(); code = codes[0];
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            var values = await db.UserTokens.Where(x => x.UserId == id).Select(x => x.Value).ToListAsync();
            Assert.DoesNotContain(key, values);
            Assert.All(values, value => Assert.DoesNotContain(code, value));
        }
        using var first = host.Services.CreateScope(); using var second = host.Services.CreateScope();
        var a = first.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
        var b = second.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
        var u = (await a.FindByIdAsync(id))!; var stale = (await b.FindByIdAsync(id))!;
        Assert.True((await a.RedeemTwoFactorRecoveryCodeAsync(u, code)).Succeeded);
        Assert.False((await b.RedeemTwoFactorRecoveryCodeAsync(stale, code)).Succeeded);
        using var check = host.Services.CreateScope();
        var final = check.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
        var saved = (await final.FindByIdAsync(id))!;
        Assert.True(saved.MustReplaceRecoveryCodes);
        Assert.False((await final.RedeemTwoFactorRecoveryCodeAsync(saved, code)).Succeeded);
        Assert.Equal(1, await final.CountRecoveryCodesAsync(saved));
    }

    [Fact]
    public async Task Invitation_activation_is_scoped_atomic_one_use_and_requires_the_recorded_token()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync();
        var (id, buildingId) = await host.SeedManagerAsync(); using var client = host.Browser(); await host.LoginManagerAsync(client, id);
        Guid suiteId;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            var suite = new Suite { BuildingId = buildingId, Name = "Suite A" }; suiteId = suite.Id; db.Suites.Add(suite); await db.SaveChangesAsync();
        }
        using var invite = await AccountTestHost.PostAsync(client, "/Management", "/Management/Invite", ("SuiteId", suiteId.ToString()), ("Email", "tenant@example.test"));
        Assert.Equal(HttpStatusCode.Redirect, invite.StatusCode);
        Guid invitationId; string token;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            var invitation = await db.Invitations.SingleAsync(); invitationId = invitation.Id;
            Assert.Empty(await db.Memberships.ToListAsync());
            var message = await db.AccountMessages.SingleAsync(x => x.Kind == "Invitation");
            var payload = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("AllenOffice.AccountMessage.v1").Unprotect(message.ProtectedPayload);
            token = JsonDocument.Parse(payload).RootElement.GetProperty("token").GetString()!;
        }
        using var tenant = host.Browser();
        using var bad = await AccountTestHost.PostAsync(tenant, $"/Invitation/Accept/{invitationId}", "/Invitation/Accept",
            ("InvitationId", invitationId.ToString()), ("Token", "wrong"), ("Password", AccountTestHost.Password), ("ConfirmPassword", AccountTestHost.Password));
        Assert.Contains("Unable to activate", await bad.Content.ReadAsStringAsync());
        using var activated = await AccountTestHost.PostAsync(tenant, $"/Invitation/Accept/{invitationId}", "/Invitation/Accept",
            ("InvitationId", invitationId.ToString()), ("Token", token), ("Password", AccountTestHost.Password), ("ConfirmPassword", AccountTestHost.Password), ("SuiteId", Guid.NewGuid().ToString()));
        Assert.Contains("ready to sign in", await activated.Content.ReadAsStringAsync());
        using var replay = await AccountTestHost.PostAsync(tenant, $"/Invitation/Accept/{invitationId}", "/Invitation/Accept",
            ("InvitationId", invitationId.ToString()), ("Token", token), ("Password", AccountTestHost.Password), ("ConfirmPassword", AccountTestHost.Password));
        Assert.Contains("Unable to activate", await replay.Content.ReadAsStringAsync());
        using var verification = host.Services.CreateScope();
        var savedDb = verification.ServiceProvider.GetRequiredService<OfficeDbContext>();
        Assert.Equal(suiteId, (await savedDb.Memberships.SingleAsync()).SuiteId);
        Assert.Single(await savedDb.ActiveGrants.ToListAsync());
        Assert.Equal(2, await savedDb.AccountAudits.CountAsync());
    }

    [Fact]
    public async Task Account_posts_require_antiforgery_and_reset_responses_do_not_enumerate_accounts()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync();
        await host.SeedManagerAsync(); using var client = host.Browser();
        using var forgery = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["Email"] = "manager@example.test", ["Password"] = AccountTestHost.Password }));
        Assert.Equal(HttpStatusCode.BadRequest, forgery.StatusCode);
        using var known = await AccountTestHost.PostAsync(client, "/Invitation/Forgot", "/Invitation/Forgot", ("Email", "manager@example.test"));
        using var unknown = await AccountTestHost.PostAsync(client, "/Invitation/Forgot", "/Invitation/Forgot", ("Email", "unknown@example.test"));
        Assert.Equal(known.StatusCode, unknown.StatusCode);
        var a = await known.Content.ReadAsStringAsync(); var b = await unknown.Content.ReadAsStringAsync();
        Assert.Contains("If a confirmed account matches", a); Assert.Contains("If a confirmed account matches", b);
    }
}
