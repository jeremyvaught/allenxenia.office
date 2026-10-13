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
    public async Task Local_invitation_delivery_keeps_credentials_in_the_fragment_and_expired_acceptance_cannot_grant_access()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync();
        var (managerId, buildingId) = await host.SeedManagerAsync(); using var client = host.Browser(); await host.LoginManagerAsync(client, managerId);
        Guid suiteId;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); var suite = new Suite { BuildingId = buildingId, Name = "Invitation delivery" };
            suiteId = suite.Id; db.Suites.Add(suite); await db.SaveChangesAsync();
        }
        using var issued = await AccountTestHost.PostAsync(client, "/Management", "/Management/Invite", ("SuiteId", suiteId.ToString()), ("Email", "invited@example.test"));
        Assert.Equal(HttpStatusCode.Redirect, issued.StatusCode);
        var directory = Path.Combine(Path.GetTempPath(), $"office-invitation-check-{Guid.NewGuid():N}");
        try
        {
            using (var scope = host.Services.CreateScope())
            {
                var queue = scope.ServiceProvider.GetRequiredService<AllenBuilding.Application.Notifications.INotificationQueue>();
                var dispatcher = new AllenBuilding.Application.Notifications.NotificationDispatcher(queue, new AllenBuilding.Infrastructure.Notifications.LocalInboxTransport(directory));
                Assert.True(await dispatcher.ProcessOneAsync());
            }
            var envelope = JsonSerializer.Deserialize<AllenBuilding.Application.Notifications.EmailEnvelope>(await File.ReadAllTextAsync(Directory.GetFiles(directory).Single()))!;
            var link = new Uri(envelope.Text.Split('\n').Last()); Assert.Empty(link.Query); Assert.StartsWith("#token=", link.Fragment);
            var token = Uri.UnescapeDataString(link.Fragment[7..]); var invitationId = Guid.Parse(link.Segments.Last());
            host.Clock.Offset = TimeSpan.FromDays(8);
            using var tenant = host.Browser();
            using var expired = await AccountTestHost.PostAsync(tenant, $"/Invitation/Accept/{invitationId}", "/Invitation/Accept", ("InvitationId", invitationId.ToString()), ("Token", token),
                ("Password", AccountTestHost.Password), ("ConfirmPassword", AccountTestHost.Password));
            Assert.Contains("Unable to activate", await expired.Content.ReadAsStringAsync());
            using var check = host.Services.CreateScope(); var savedDb = check.ServiceProvider.GetRequiredService<OfficeDbContext>();
            Assert.Empty(await savedDb.Memberships.ToListAsync()); Assert.Null((await savedDb.Invitations.SingleAsync()).AcceptedAt);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Password_reset_invalidates_existing_sessions_without_changing_access_grants()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync();
        var (id, _) = await host.SeedManagerAsync(enrolled: false); using var client = host.Browser();
        using var login = await AccountTestHost.PostAsync(client, "/Account/Login", "/Account/Login", ("Email", "manager@example.test"), ("Password", AccountTestHost.Password));
        string token;
        using (var scope = host.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
            token = await users.GeneratePasswordResetTokenAsync((await users.FindByIdAsync(id))!);
        }
        using var reset = await AccountTestHost.PostAsync(client, $"/Invitation/Reset/{id}", "/Invitation/Reset", ("UserId", id), ("Token", token), ("Password", "Different-synthetic-2026!"));
        Assert.Contains("ready to sign in", await reset.Content.ReadAsStringAsync());
        using var protectedRead = await client.GetAsync("/Portal"); Assert.StartsWith("http://localhost/Account/Login", protectedRead.Headers.Location?.OriginalString);
        using var verification = host.Services.CreateScope(); var db = verification.ServiceProvider.GetRequiredService<OfficeDbContext>();
        Assert.Single(await db.BuildingManagers.ToListAsync()); Assert.Empty(await db.Memberships.ToListAsync());
    }

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
        using var forgedGrant = await client.PostAsync("/Management/Grant", new FormUrlEncodedContent(new Dictionary<string, string> { ["OperationId"] = Guid.NewGuid().ToString() }));
        Assert.Equal(HttpStatusCode.BadRequest, forgedGrant.StatusCode);
        using var forgedInvitation = await client.PostAsync("/Management/ChangeInvitation", new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.BadRequest, forgedInvitation.StatusCode);
        host.Clock.Offset = TimeSpan.FromMinutes(6);
        using var read = await client.GetAsync("/Management"); Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var change = await AccountTestHost.PostAsync(client, "/Management", "/Management/AddSuite", ("BuildingId", building.ToString()), ("Name", "Suite A"));
        Assert.StartsWith("http://localhost/Account/Denied", change.Headers.Location?.OriginalString);
        using var staleGrant = await AccountTestHost.PostAsync(client, "/Management", "/Management/Grant", ("Email", "tenant@example.test"));
        Assert.StartsWith("http://localhost/Account/Denied", staleGrant.Headers.Location?.OriginalString);
        using var staleInvitation = await AccountTestHost.PostAsync(client, "/Management", "/Management/ChangeInvitation", ("Reason", "Revoke unused link"));
        Assert.StartsWith("http://localhost/Account/Denied", staleInvitation.Headers.Location?.OriginalString);
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
        Assert.Equal(HttpStatusCode.OK, portal.StatusCode);
        Assert.Contains("Set up account security", await portal.Content.ReadAsStringAsync());
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
    public async Task Optional_first_enrollment_uses_the_verified_password_and_factor_without_repeat_prompts()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync();
        var (id, _) = await host.SeedManagerAsync(enrolled: false);
        using var client = host.Browser();
        using var login = await AccountTestHost.PostAsync(client, "/Account/Login", "/Account/Login", ("Email", "manager@example.test"), ("Password", AccountTestHost.Password));
        using var page = await client.GetAsync("/Account/Enroll");
        using var bad = await AccountTestHost.PostAsync(client, "/Account/Enroll", "/Account/Enroll", ("Code", "wrong"));
        Assert.Contains("Unable to verify", await bad.Content.ReadAsStringAsync());
        using var denied = await client.GetAsync("/Management");
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        using var good = await AccountTestHost.PostAsync(client, "/Account/Enroll", "/Account/Enroll", ("Code", await host.FactorCodeAsync(id)));
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        Assert.Contains("Keep these codes safe", await good.Content.ReadAsStringAsync());
        Assert.DoesNotContain("name=\"Password\"", await good.Content.ReadAsStringAsync());
        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
        var user = (await users.FindByIdAsync(id))!;
        Assert.True(user.TwoFactorEnabled); Assert.False(user.MustReplaceRecoveryCodes);
        Assert.Equal(10, await users.CountRecoveryCodesAsync(user));
        using var manager = await client.GetAsync("/Management"); Assert.Equal(HttpStatusCode.OK, manager.StatusCode);
        using var portal = await client.GetAsync("/Portal"); Assert.Equal(HttpStatusCode.OK, portal.StatusCode);
        using var repeat = await AccountTestHost.PostAsync(client, "/Portal", "/Account/Enroll", ("Code", await host.FactorCodeAsync(id)));
        Assert.DoesNotContain("Keep these codes safe", await repeat.Content.ReadAsStringAsync());
        Assert.Equal(10, await users.CountRecoveryCodesAsync(user));
    }

    [Fact]
    public async Task First_enrollment_cannot_upgrade_an_expired_password_or_revoked_session()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync();
        var (id, _) = await host.SeedManagerAsync(enrolled: false); using var client = host.Browser();
        await AccountTestHost.PostAsync(client, "/Account/Login", "/Account/Login", ("Email", "manager@example.test"), ("Password", AccountTestHost.Password));
        await client.GetAsync("/Account/Enroll");
        host.Clock.Offset = TimeSpan.FromMinutes(11);
        using var expired = await AccountTestHost.PostAsync(client, "/Portal", "/Account/Enroll", ("Code", await host.FactorCodeAsync(id)));
        Assert.Contains("/Account/Login", expired.Headers.Location?.OriginalString);
        using var scope = host.Services.CreateScope(); var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
        var user = (await users.FindByIdAsync(id))!; Assert.False(user.TwoFactorEnabled); Assert.Equal(0, await users.CountRecoveryCodesAsync(user));
        host.Clock.Offset = TimeSpan.Zero;
        await AccountTestHost.PostAsync(client, "/Account/Login", "/Account/Login", ("Email", "manager@example.test"), ("Password", AccountTestHost.Password));
        Assert.True((await users.UpdateSecurityStampAsync(user)).Succeeded);
        using var revoked = await client.PostAsync("/Account/Enroll", new FormUrlEncodedContent(new Dictionary<string, string> { ["Code"] = await host.FactorCodeAsync(id) }));
        Assert.Equal(HttpStatusCode.Redirect, revoked.StatusCode); Assert.Contains("/Account/Login", revoked.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Enrollment_preserves_original_password_time_for_sensitive_actions()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync(); var (id, _) = await host.SeedManagerAsync(enrolled: false);
        using var client = host.Browser();
        await AccountTestHost.PostAsync(client, "/Account/Login", "/Account/Login", ("Email", "manager@example.test"), ("Password", AccountTestHost.Password));
        await client.GetAsync("/Account/Enroll"); host.Clock.Offset = TimeSpan.FromMinutes(6);
        using var enrolled = await AccountTestHost.PostAsync(client, "/Account/Enroll", "/Account/Enroll", ("Code", await host.FactorCodeAsync(id)));
        Assert.Contains("Keep these codes safe", await enrolled.Content.ReadAsStringAsync());
        using var ordinary = await client.GetAsync("/Management"); Assert.Equal(HttpStatusCode.OK, ordinary.StatusCode);
        using var sensitive = await AccountTestHost.PostAsync(client, "/Management", "/Management/AddSuite", ("Name", "Must still require recent password"));
        Assert.Equal(HttpStatusCode.Redirect, sensitive.StatusCode); Assert.Contains("/Account/Denied", sensitive.Headers.Location?.OriginalString);
        using var scope = host.Services.CreateScope(); Assert.Empty(await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().Suites.ToArrayAsync());
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
        using var shortPassword = await AccountTestHost.PostAsync(tenant, $"/Invitation/Accept/{invitationId}", "/Invitation/Accept",
            ("InvitationId", invitationId.ToString()), ("Token", token), ("Password", "short"), ("ConfirmPassword", "short"));
        Assert.Contains("Use a password between 12 and 128 characters", await shortPassword.Content.ReadAsStringAsync());
        Assert.DoesNotContain("Unable to activate", await shortPassword.Content.ReadAsStringAsync());
        using var activated = await AccountTestHost.PostAsync(tenant, $"/Invitation/Accept/{invitationId}", "/Invitation/Accept",
            ("InvitationId", invitationId.ToString()), ("Token", token), ("Password", "long lowercase passphrase"), ("ConfirmPassword", "long lowercase passphrase"), ("SuiteId", Guid.NewGuid().ToString()));
        Assert.Contains("ready to sign in", await activated.Content.ReadAsStringAsync());
        using var replay = await AccountTestHost.PostAsync(tenant, $"/Invitation/Accept/{invitationId}", "/Invitation/Accept",
            ("InvitationId", invitationId.ToString()), ("Token", token), ("Password", AccountTestHost.Password), ("ConfirmPassword", AccountTestHost.Password));
        Assert.Contains("Unable to activate", await replay.Content.ReadAsStringAsync());
        using var verification = host.Services.CreateScope();
        var savedDb = verification.ServiceProvider.GetRequiredService<OfficeDbContext>();
        Assert.Equal(suiteId, (await savedDb.Memberships.SingleAsync()).SuiteId);
        Assert.Single(await savedDb.ActiveGrants.ToListAsync());
        Assert.Equal(2, await savedDb.AccountAudits.CountAsync());
        var userId = (await savedDb.Invitations.SingleAsync()).UserId;
        var users = verification.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
        var resetToken = await users.GeneratePasswordResetTokenAsync((await users.FindByIdAsync(userId))!);
        using var shortReset = await AccountTestHost.PostAsync(tenant, $"/Invitation/Reset/{userId}", "/Invitation/Reset",
            ("UserId", userId), ("Token", resetToken), ("Password", "short"));
        Assert.Contains("Use a password between 12 and 128 characters", await shortReset.Content.ReadAsStringAsync());
        Assert.DoesNotContain("Unable to reset", await shortReset.Content.ReadAsStringAsync());
        using var reset = await AccountTestHost.PostAsync(tenant, $"/Invitation/Reset/{userId}", "/Invitation/Reset",
            ("UserId", userId), ("Token", resetToken), ("Password", "abcdefghijkl"));
        Assert.Contains("ready to sign in", await reset.Content.ReadAsStringAsync());
        using var login = await AccountTestHost.PostAsync(tenant, "/Account/Login", "/Account/Login", ("Email", "tenant@example.test"), ("Password", "abcdefghijkl"));
        Assert.Equal("/Portal", login.Headers.Location?.OriginalString);
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
