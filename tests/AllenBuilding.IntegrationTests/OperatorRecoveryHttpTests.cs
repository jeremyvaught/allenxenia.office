using System.Net;
using AllenBuilding.Application.Access;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AllenBuilding.IntegrationTests;

public sealed class OperatorRecoveryHttpTests
{
    [Fact]
    public async Task Mailbox_password_reset_cannot_reveal_or_enroll_a_factor_after_operator_recovery()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync(); var (id, _) = await host.SeedManagerAsync();
        string setupKey; string resetToken;
        using (var scope = host.Services.CreateScope())
        {
            var services = scope.ServiceProvider; var users = services.GetRequiredService<UserManager<OfficeUser>>(); var user = (await users.FindByIdAsync(id))!;
            Assert.Equal(AccessCode.Success, await new OperatorMfaRecovery(services.GetRequiredService<OfficeDbContext>(), users,
                services.GetRequiredService<AccountInvitations>(), new(true, Environment.UserName)).ExecuteAsync(new(id, user.SecurityStamp!, "independent-verification", "Lost device", new string('A', 32))));
            setupKey = (await users.GetAuthenticatorKeyAsync(user))!; resetToken = await users.GeneratePasswordResetTokenAsync(user);
        }
        using var attacker = host.Browser();
        using var reset = await AccountTestHost.PostAsync(attacker, $"/Invitation/Reset/{id}", "/Invitation/Reset",
            ("UserId", id), ("Token", resetToken), ("Password", "Mailbox-attacker-2026!"));
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        using var login = await AccountTestHost.PostAsync(attacker, "/Account/Login", "/Account/Login",
            ("Email", "manager@example.test"), ("Password", "Mailbox-attacker-2026!"));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var page = await attacker.GetStringAsync("/Account/Enroll");
        Assert.DoesNotContain(setupKey, page); Assert.Contains("code from the verified operator", page);
        using var bypass = await AccountTestHost.PostAsync(attacker, "/Account/Enroll", "/Account/Enroll", ("Code", await host.FactorCodeAsync(id)));
        Assert.Equal(HttpStatusCode.Forbidden, bypass.StatusCode);
        using var final = host.Services.CreateScope();
        Assert.False((await final.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>().FindByIdAsync(id))!.TwoFactorEnabled);
    }

    [Fact]
    public async Task Verified_operator_recovery_revokes_old_sessions_and_requires_new_enrollment_and_recovery_codes()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync(); var (id, _) = await host.SeedManagerAsync();
        using var old = host.Browser(); await host.LoginManagerAsync(old, id);
        using (var scope = host.Services.CreateScope())
        {
            var services = scope.ServiceProvider; Assert.Null(services.GetService<OperatorMfaRecovery>());
            var users = services.GetRequiredService<UserManager<OfficeUser>>(); var target = (await users.FindByIdAsync(id))!;
            Assert.Equal(AccessCode.Success, await new OperatorMfaRecovery(services.GetRequiredService<OfficeDbContext>(), users,
                services.GetRequiredService<AccountInvitations>(), new(true, Environment.UserName)).ExecuteAsync(new(target.Id, target.SecurityStamp!, "independent-owner-verification-7", "Verified lost device", new string('B', 32))));
        }
        using var revoked = await old.GetAsync("/Management"); Assert.Equal(HttpStatusCode.Redirect, revoked.StatusCode); Assert.Contains("/Account/Login", revoked.Headers.Location!.OriginalString);
        using var fresh = host.Browser();
        using var password = await AccountTestHost.PostAsync(fresh, "/Account/Login", "/Account/Login", ("Email", "manager@example.test"), ("Password", AccountTestHost.Password));
        Assert.Equal("/Portal", password.Headers.Location!.OriginalString);
        using var portal = await fresh.GetAsync("/Portal"); Assert.Equal("/Account/Enroll", portal.Headers.Location!.OriginalString);
        using var blocked = await fresh.GetAsync("/Management"); Assert.Equal(HttpStatusCode.Redirect, blocked.StatusCode);
        using var operatorCode = await AccountTestHost.PostAsync(fresh, "/Account/Enroll", "/Account/AuthorizeEnrollment", ("Code", new string('B', 32)));
        Assert.Equal("/Account/Enroll", operatorCode.Headers.Location!.OriginalString);
        using var enrollment = await AccountTestHost.PostAsync(fresh, "/Account/Enroll", "/Account/Enroll", ("Code", await host.FactorCodeAsync(id)));
        Assert.Equal("/Account/Login", enrollment.Headers.Location!.OriginalString);
        using var reenter = await AccountTestHost.PostAsync(fresh, "/Account/Login", "/Account/Login", ("Email", "manager@example.test"), ("Password", AccountTestHost.Password));
        Assert.Equal("/Account/Factor", reenter.Headers.Location!.OriginalString);
        using var challenge = await AccountTestHost.PostAsync(fresh, "/Account/Factor", "/Account/Factor", ("Code", await host.FactorCodeAsync(id)));
        Assert.Equal("/Portal", challenge.Headers.Location!.OriginalString);
        using var codesRequired = await fresh.GetAsync("/Portal"); Assert.Equal("/Account/RecoveryCodes", codesRequired.Headers.Location!.OriginalString);
        using var stillBlocked = await fresh.GetAsync("/Management"); Assert.Equal(HttpStatusCode.Redirect, stillBlocked.StatusCode);
        using var codes = await AccountTestHost.PostAsync(fresh, "/Account/RecoveryCodes", "/Account/RecoveryCodes", ("Password", AccountTestHost.Password), ("Code", await host.FactorCodeAsync(id)));
        Assert.Equal(HttpStatusCode.OK, codes.StatusCode);
        using var restored = await fresh.GetAsync("/Management"); Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        using var final = host.Services.CreateScope(); var db = final.ServiceProvider.GetRequiredService<OfficeDbContext>();
        Assert.Single(await db.AccountAudits.Where(x => x.Action == "OperatorMfaRecovered" && x.SubjectId == id).ToArrayAsync());
        Assert.Single(await db.BuildingManagers.Where(x => x.UserId == id).ToArrayAsync());
    }
}
