using System.Net;
using System.Text.RegularExpressions;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AllenBuilding.IntegrationTests;

public sealed class TenancyClosureHttpTests
{
    [Fact]
    public async Task Ending_tenancy_requires_a_separate_review_csrf_current_MFA_and_keeps_provider_cleanup_pending()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync(); var (managerId, buildingId) = await host.SeedManagerAsync();
        Guid accountId, memberId, retainedId; Guid version, membershipVersion;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
            var tenant = new OfficeUser { UserName = "closure-http@example.test", Email = "closure-http@example.test", EmailConfirmed = true }; Assert.True((await users.CreateAsync(tenant, AccountTestHost.Password)).Succeeded);
            var suite = new Suite { BuildingId = buildingId, Name = "Closure HTTP suite" }; var second = new Suite { BuildingId = buildingId, Name = "Other HTTP membership" };
            var member = Membership.Grant(suite.Id, tenant.Id, host.Clock.GetUtcNow().AddYears(-1)); var retained = Membership.Grant(second.Id, tenant.Id, host.Clock.GetUtcNow().AddYears(-1));
            var account = RentAccount.Open(Guid.NewGuid(), member.Id); var other = RentAccount.Open(Guid.NewGuid(), retained.Id);
            accountId = account.Id; memberId = member.Id; retainedId = other.Id; version = account.Version; membershipVersion = member.Version;
            db.AddRange(suite, second, member, retained, account, other, new ActiveGrant { UserId = tenant.Id, SuiteId = suite.Id, MembershipId = member.Id },
                new ActiveGrant { UserId = tenant.Id, SuiteId = second.Id, MembershipId = retained.Id }, Obligation.Issue(account.Id, new Money(1000), "Existing debt", new DateOnly(2026, 1, 1), managerId, host.Clock.GetUtcNow())); await db.SaveChangesAsync();
        }
        using var tenantBrowser = host.Browser(); using var manager = host.Browser(); using var guest = host.Browser();
        using var login = await AccountTestHost.PostAsync(tenantBrowser, "/Account/Login", "/Account/Login", ("Email", "closure-http@example.test"), ("Password", AccountTestHost.Password));
        using var before = await tenantBrowser.GetAsync("/Billing/Account/" + accountId); Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        await host.LoginManagerAsync(manager, managerId); var original = Guid.NewGuid();
        var values = new[] { ("OperationId", original.ToString()), ("AccountId", accountId.ToString()), ("Version", version.ToString()), ("MembershipVersion", membershipVersion.ToString()), ("Reason", "<script>end-reason()</script>") };
        using var noCsrf = await manager.PostAsync("/Billing/PrepareEnd", new FormUrlEncodedContent(values.Select(x => new KeyValuePair<string, string>(x.Item1, x.Item2)))); Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        using var tenantDenied = await AccountTestHost.PostAsync(tenantBrowser, "/Billing/Account/" + accountId, "/Billing/PrepareEnd", values); Assert.NotEqual(HttpStatusCode.OK, tenantDenied.StatusCode);
        using var review = await AccountTestHost.PostAsync(manager, "/Billing/ManageAccount/" + accountId, "/Billing/PrepareEnd", values); var html = await review.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, review.StatusCode); Assert.Contains("End tenancy and revoke access", html); Assert.Contains("&lt;script&gt;end-reason()&lt;/script&gt;", html);
        Assert.Contains("does not verify a Stripe cancellation", html);
        using (var scope = host.Services.CreateScope()) { var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); Assert.Null((await db.Memberships.SingleAsync(x => x.Id == memberId)).RevokedAt); Assert.False(await db.TenancyClosures.AnyAsync()); }
        var ticket = WebUtility.HtmlDecode(Regex.Match(html, "name=\"Ticket\"[^>]*value=\"([^\"]+)\"").Groups[1].Value); Assert.NotEmpty(ticket);
        using var tampered = await AccountTestHost.PostAsync(manager, "/Billing/ManageAccount/" + accountId, "/Billing/Confirm", ("Ticket", ticket + "tampered")); Assert.Equal(HttpStatusCode.BadRequest, tampered.StatusCode);
        using var confirm = await AccountTestHost.PostAsync(manager, "/Billing/ManageAccount/" + accountId, "/Billing/Confirm", ("Ticket", ticket)); Assert.Equal(HttpStatusCode.Redirect, confirm.StatusCode);
        using var replay = await AccountTestHost.PostAsync(manager, "/Billing/ManageAccount/" + accountId, "/Billing/Confirm", ("Ticket", ticket)); Assert.Equal(HttpStatusCode.Redirect, replay.StatusCode);
        using var ended = await tenantBrowser.GetAsync("/Billing/Account/" + accountId); Assert.Equal(HttpStatusCode.NotFound, ended.StatusCode);
        using var retainedRead = await tenantBrowser.GetAsync("/Billing/Account/" + retainedId); Assert.Equal(HttpStatusCode.OK, retainedRead.StatusCode);
        var managerHtml = await manager.GetStringAsync("/Billing/ManageAccount/" + accountId); Assert.Contains("Billing closure pending", managerHtml); Assert.DoesNotContain("Review tenancy end", managerHtml); Assert.DoesNotContain("Review calendar rent", managerHtml); Assert.Contains("Existing debt", managerHtml);
        var heldReport = await manager.GetStringAsync("/Reports/Rent?Group=Held"); Assert.Contains("Existing debt", heldReport); Assert.Contains("Held for reconciliation", heldReport);
        using var pending = await manager.GetAsync("/Billing/Closures"); var queue = await pending.Content.ReadAsStringAsync(); Assert.Equal(HttpStatusCode.OK, pending.StatusCode); Assert.Contains("no-store", pending.Headers.CacheControl?.ToString());
        Assert.Contains("Closure HTTP suite", queue); Assert.Contains("&lt;script&gt;end-reason()&lt;/script&gt;", queue); Assert.DoesNotContain("<script>end-reason()", queue);
        using var guestDenied = await guest.GetAsync("/Billing/Closures"); Assert.Contains("/Account/Login", guestDenied.Headers.Location?.OriginalString);
        using var privateDenied = await tenantBrowser.GetAsync("/Billing/Closures"); Assert.Contains("/Account/Denied", privateDenied.Headers.Location?.OriginalString);
        using (var scope = host.Services.CreateScope()) { var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); Assert.Equal(1, await db.TenancyClosures.CountAsync()); Assert.Equal(BillingClosureState.Pending, (await db.TenancyClosures.SingleAsync()).State); }
    }
}
