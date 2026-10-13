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
    public async Task Verified_operator_recovery_revokes_old_sessions_and_requires_new_enrollment_and_recovery_codes()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync(); var (id, _) = await host.SeedManagerAsync();
        using var old = host.Browser(); await host.LoginManagerAsync(old, id);
        using (var scope = host.Services.CreateScope())
        {
            var services = scope.ServiceProvider; Assert.Null(services.GetService<OperatorMfaRecovery>());
            var users = services.GetRequiredService<UserManager<OfficeUser>>(); var target = (await users.FindByIdAsync(id))!;
            Assert.Equal(AccessCode.Success, await new OperatorMfaRecovery(services.GetRequiredService<OfficeDbContext>(), users,
                services.GetRequiredService<AccountInvitations>(), new(true, Environment.UserName)).ExecuteAsync(new(target.Id, target.SecurityStamp!, "independent-owner-verification-7", "Verified lost device")));
        }
        using var revoked = await old.GetAsync("/Management"); Assert.Equal(HttpStatusCode.Redirect, revoked.StatusCode); Assert.Contains("/Account/Login", revoked.Headers.Location!.OriginalString);
        using var fresh = host.Browser();
        using var password = await AccountTestHost.PostAsync(fresh, "/Account/Login", "/Account/Login", ("Email", "manager@example.test"), ("Password", AccountTestHost.Password));
        Assert.Equal("/Portal", password.Headers.Location!.OriginalString);
        using var portal = await fresh.GetAsync("/Portal"); Assert.Equal("/Account/Enroll", portal.Headers.Location!.OriginalString);
        using var blocked = await fresh.GetAsync("/Management"); Assert.Equal(HttpStatusCode.Redirect, blocked.StatusCode);
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
