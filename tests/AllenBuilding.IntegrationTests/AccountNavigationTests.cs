using System.Net;
using System.Text.RegularExpressions;
using AllenBuilding.Domain.Maintenance;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AllenBuilding.IntegrationTests;

public sealed class AccountNavigationTests
{
    [Fact]
    public async Task Tenant_entry_preserves_destination_through_a_failed_password_then_returns_without_submitting()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync(); await SeedTenant(host); using var browser = host.Browser();
        using var protectedPage = await browser.GetAsync("/Maintenance/Create"); Assert.Equal(HttpStatusCode.Redirect, protectedPage.StatusCode);
        var loginPath = protectedPage.Headers.Location!.PathAndQuery; var html = await browser.GetStringAsync(loginPath);
        Assert.Equal("/Maintenance/Create", ReturnValue(html));
        using var failed = await AccountTestHost.PostAsync(browser, loginPath, "/Account/Login", ("Email", "navigation-tenant@example.test"), ("Password", "Incorrect-synthetic-2026!"), ("ReturnUrl", ReturnValue(html)));
        Assert.Equal(HttpStatusCode.OK, failed.StatusCode); Assert.Equal("/Maintenance/Create", ReturnValue(await failed.Content.ReadAsStringAsync()));
        using var signedIn = await AccountTestHost.PostAsync(browser, loginPath, "/Account/Login", ("Email", "navigation-tenant@example.test"), ("Password", AccountTestHost.Password), ("ReturnUrl", ReturnValue(html)));
        Assert.Equal("/Maintenance/Create", signedIn.Headers.Location?.OriginalString);
        using var returned = await browser.GetAsync(signedIn.Headers.Location); Assert.Equal(HttpStatusCode.OK, returned.StatusCode);
        using var scope = host.Services.CreateScope(); Assert.Empty(await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().WorkOrders.ToListAsync());
    }
    [Theory]
    [InlineData("https://foreign.test")]
    [InlineData("//foreign.test/path")]
    [InlineData("/\\foreign.test/path")]
    [InlineData("/Maintenance\r\nLocation: https://foreign.test")]
    [InlineData("~/Maintenance")]
    [InlineData("oversize")]
    public async Task Unsafe_destinations_are_discarded_on_get_and_post_without_open_redirects(string destination)
    {
        if (destination == "oversize") destination = "/" + new string('x', 2048);
        await using var host = new AccountTestHost(); await host.InitializeAsync(); await SeedTenant(host); using var browser = host.Browser();
        var page = "/Account/Login?ReturnUrl=" + Uri.EscapeDataString(destination);
        Assert.Empty(ReturnValue(await browser.GetStringAsync(page)));
        using var signedIn = await AccountTestHost.PostAsync(browser, page, "/Account/Login", ("Email", "navigation-tenant@example.test"), ("Password", AccountTestHost.Password), ("ReturnUrl", destination));
        Assert.Equal("/Portal", signedIn.Headers.Location?.OriginalString);
    }
    [Fact]
    public async Task Missing_factor_challenge_returns_to_password_with_a_safe_destination_and_no_authority()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync(); using var browser = host.Browser();
        using var restart = await browser.GetAsync("/Account/Factor?ReturnUrl=%2FMaintenance%2FManage");
        Assert.StartsWith("/Account/Login?", restart.Headers.Location?.OriginalString);
        Assert.Equal("/Maintenance/Manage", ReturnValue(await browser.GetStringAsync(restart.Headers.Location!)));
        using var forged = await browser.GetAsync("/Account/Factor?ReturnUrl=https%3A%2F%2Fforeign.test"); Assert.Equal("/Account/Login", forged.Headers.Location?.OriginalString);
        using var protectedPage = await browser.GetAsync("/Maintenance/Manage"); Assert.Equal(HttpStatusCode.Redirect, protectedPage.StatusCode);
    }
    [Fact]
    public async Task Manager_destination_requires_the_real_factor_challenge_and_survives_an_invalid_code()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync(); var (manager, _) = await host.SeedManagerAsync(); using var browser = host.Browser();
        using var password = await AccountTestHost.PostAsync(browser, "/Account/Login", "/Account/Login", ("Email", "manager@example.test"), ("Password", AccountTestHost.Password), ("ReturnUrl", "/Maintenance/Manage"));
        Assert.StartsWith("/Account/Factor?", password.Headers.Location?.OriginalString);
        using var notYetAuthorized = await browser.GetAsync("/Maintenance/Manage"); Assert.Equal(HttpStatusCode.Redirect, notYetAuthorized.StatusCode);
        var factorPath = password.Headers.Location!.OriginalString; Assert.Equal("/Maintenance/Manage", ReturnValue(await browser.GetStringAsync(factorPath)));
        using var failed = await AccountTestHost.PostAsync(browser, factorPath, "/Account/Factor", ("Code", "not-a-factor-code"), ("ReturnUrl", "/Maintenance/Manage"));
        Assert.Equal(HttpStatusCode.OK, failed.StatusCode); Assert.Equal("/Maintenance/Manage", ReturnValue(await failed.Content.ReadAsStringAsync()));
        using var challenged = await AccountTestHost.PostAsync(browser, factorPath, "/Account/Factor", ("Code", await host.FactorCodeAsync(manager)), ("ReturnUrl", "/Maintenance/Manage"));
        Assert.Equal("/Maintenance/Manage", challenged.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(challenged.Headers.Location)).StatusCode);
    }
    [Fact]
    public async Task Forged_factor_destination_falls_back_and_recovery_still_forces_code_replacement()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync(); var (manager, _) = await host.SeedManagerAsync(); using var browser = host.Browser();
        await AccountTestHost.PostAsync(browser, "/Account/Login", "/Account/Login", ("Email", "manager@example.test"), ("Password", AccountTestHost.Password), ("ReturnUrl", "/Management"));
        using var safe = await AccountTestHost.PostAsync(browser, "/Account/Factor", "/Account/Factor", ("Code", await host.FactorCodeAsync(manager)), ("ReturnUrl", "//foreign.test"));
        Assert.Equal("/Portal", safe.Headers.Location?.OriginalString);
        string recovery;
        using (var scope = host.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>(); recovery = (await users.GenerateNewTwoFactorRecoveryCodesAsync((await users.FindByIdAsync(manager))!, 1))!.Single();
        }
        await AccountTestHost.PostAsync(browser, "/Account/Login", "/Account/Login", ("Email", "manager@example.test"), ("Password", AccountTestHost.Password), ("ReturnUrl", "/Management"));
        using var recovered = await AccountTestHost.PostAsync(browser, "/Account/Factor", "/Account/Factor", ("Code", recovery), ("Recovery", "true"), ("ReturnUrl", "/Management"));
        Assert.Equal("/Account/RecoveryCodes", recovered.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/Management")).StatusCode);
    }
    [Fact]
    public async Task New_manager_return_cannot_skip_authenticator_enrollment()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync(); await host.SeedManagerAsync(enrolled: false); using var browser = host.Browser();
        using var signedIn = await AccountTestHost.PostAsync(browser, "/Account/Login", "/Account/Login", ("Email", "manager@example.test"), ("Password", AccountTestHost.Password), ("ReturnUrl", "/Maintenance/Manage"));
        Assert.Equal("/Account/Enroll", signedIn.Headers.Location?.OriginalString);
        Assert.NotEqual(HttpStatusCode.OK, (await browser.GetAsync("/Maintenance/Manage")).StatusCode);
    }
    [Fact]
    public async Task Normal_factor_with_required_recovery_replacement_cannot_skip_that_screen()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync(); var (manager, _) = await host.SeedManagerAsync();
        using (var scope = host.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>(); var user = (await users.FindByIdAsync(manager))!; user.MustReplaceRecoveryCodes = true; Assert.True((await users.UpdateAsync(user)).Succeeded);
        }
        using var browser = host.Browser();
        await AccountTestHost.PostAsync(browser, "/Account/Login", "/Account/Login", ("Email", "manager@example.test"), ("Password", AccountTestHost.Password), ("ReturnUrl", "/Maintenance/Manage"));
        using var challenged = await AccountTestHost.PostAsync(browser, "/Account/Factor", "/Account/Factor", ("Code", await host.FactorCodeAsync(manager)), ("ReturnUrl", "/Maintenance/Manage"));
        Assert.Equal("/Account/RecoveryCodes", challenged.Headers.Location?.OriginalString);
        Assert.NotEqual(HttpStatusCode.OK, (await browser.GetAsync("/Maintenance/Manage")).StatusCode);
    }
    [Fact]
    public async Task Return_navigation_does_not_restore_access_to_a_revoked_request()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync(); var (member, manager) = await SeedTenant(host); Guid id;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); var request = WorkOrder.Submit(member.SuiteId, member.Id, member.UserId, "Revoked-private-canary", "Synthetic description", "Kitchen", DateTimeOffset.UtcNow); db.Add(request); id = request.Id; await db.SaveChangesAsync();
        }
        using var browser = host.Browser(); var path = "/Maintenance/Details/" + id;
        using var first = await browser.GetAsync(path); Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); (await db.Memberships.SingleAsync(x => x.Id == member.Id)).Revoke(manager, "Synthetic access end", DateTimeOffset.UtcNow);
            db.ActiveGrants.Remove(await db.ActiveGrants.SingleAsync(x => x.MembershipId == member.Id)); await db.SaveChangesAsync();
        }
        using var signedIn = await AccountTestHost.PostAsync(browser, "/Account/Login", "/Account/Login", ("Email", "navigation-tenant@example.test"), ("Password", AccountTestHost.Password), ("ReturnUrl", path));
        Assert.Equal(path, signedIn.Headers.Location?.OriginalString);
        using var denied = await browser.GetAsync(path); Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode); Assert.DoesNotContain("Revoked-private-canary", await denied.Content.ReadAsStringAsync());
    }
    private static string ReturnValue(string html) => WebUtility.HtmlDecode(Regex.Match(html, "name=\"ReturnUrl\"[^>]*value=\"([^\"]*)\"").Groups[1].Value);
    private static async Task<(Membership Member, string Manager)> SeedTenant(AccountTestHost host)
    {
        var (manager, building) = await host.SeedManagerAsync(); using var scope = host.Services.CreateScope(); var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
        var tenant = new OfficeUser { Email = "navigation-tenant@example.test", UserName = "navigation-tenant@example.test", EmailConfirmed = true }; Assert.True((await users.CreateAsync(tenant, AccountTestHost.Password)).Succeeded);
        var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); var suite = new Suite { BuildingId = building, Name = "Navigation fixture" }; var member = Membership.Grant(suite.Id, tenant.Id, DateTimeOffset.UtcNow.AddDays(-1));
        db.AddRange(suite, member, new ActiveGrant { MembershipId = member.Id, SuiteId = suite.Id, UserId = tenant.Id }); await db.SaveChangesAsync(); return (member, manager);
    }
}
