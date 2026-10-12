using System.Net;
using System.Text.RegularExpressions;
using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using AllenBuilding.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AllenBuilding.IntegrationTests;

public sealed class BillingHttpTests
{
    [Fact]
    public async Task Reviewed_billing_posts_record_exact_values_once_and_tenant_reads_follow_original_membership()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync();
        var (managerId, building) = await host.SeedManagerAsync(); var membership = await SeedTenant(host, building);
        using var manager = host.Browser(); await host.LoginManagerAsync(manager, managerId);
        var accountId = await Open(host, manager, membership.Id);
        var account = await ReadAccount(host, accountId);
        var terms = await Review(manager, "/Billing/ManageAccount/" + accountId, "/Billing/PrepareTerms",
            ("AccountId", accountId.ToString()), ("Version", account.Version.ToString()), ("Amount", "1000.00"), ("DueDay", "31"), ("StartsOn", "2026-01-01"));
        using (var scope = host.Services.CreateScope()) Assert.Empty(await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().RentAgreements.ToListAsync());
        Assert.Contains("$1,000.00 USD", terms.Html);
        using var termsSaved = await Confirm(manager, terms.Ticket); Assert.Equal(HttpStatusCode.Redirect, termsSaved.StatusCode);
        account = await ReadAccount(host, accountId);
        var rent = await Review(manager, "/Billing/ManageAccount/" + accountId, "/Billing/PrepareRent",
            ("AccountId", accountId.ToString()), ("Version", account.Version.ToString()), ("Year", "2026"), ("Month", "2"));
        Assert.Contains("2026-02-28", rent.Html);
        using (var scope = host.Services.CreateScope()) Assert.Empty(await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().Obligations.ToListAsync());
        using var issued = await Confirm(manager, rent.Ticket); Assert.Equal(HttpStatusCode.Redirect, issued.StatusCode);
        using var replay = await Confirm(manager, rent.Ticket); Assert.Equal(HttpStatusCode.Redirect, replay.StatusCode);
        using var invalidAmount = await AccountTestHost.PostAsync(manager, "/Billing/ManageAccount/" + accountId, "/Billing/PrepareCharge",
            ("AccountId", accountId.ToString()), ("Version", account.Version.ToString()), ("OperationId", Guid.NewGuid().ToString()), ("Amount", "10.001"), ("Description", "Too precise"), ("DueOn", "2026-02-28"));
        Assert.Equal(HttpStatusCode.BadRequest, invalidAmount.StatusCode);
        var chargeOperation = Guid.NewGuid().ToString();
        var charge = await Review(manager, "/Billing/ManageAccount/" + accountId, "/Billing/PrepareCharge",
            ("AccountId", accountId.ToString()), ("Version", account.Version.ToString()), ("OperationId", chargeOperation), ("Amount", "19.75"), ("Description", "Opening <script>alert(1)</script>"), ("DueOn", "2026-02-28"));
        Assert.DoesNotContain("<script>alert(1)</script>", charge.Html); Assert.Contains("&lt;script&gt;", charge.Html);
        using var exact = await AccountTestHost.PostAsync(manager, "/Billing/Manage", "/Billing/Confirm", ("Ticket", charge.Ticket), ("Amount", "10000.00"), ("AccountId", Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.Redirect, exact.StatusCode);
        var sameReview = await Review(manager, "/Billing/ManageAccount/" + accountId, "/Billing/PrepareCharge",
            ("AccountId", accountId.ToString()), ("Version", account.Version.ToString()), ("OperationId", chargeOperation), ("Amount", "19.75"), ("Description", "Opening <script>alert(1)</script>"), ("DueOn", "2026-02-28"));
        using var duplicatedPrepare = await Confirm(manager, sameReview.Ticket); Assert.Equal(HttpStatusCode.Redirect, duplicatedPrepare.StatusCode);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            var amounts = await db.Obligations.OrderBy(x => x.AmountMinorUnits).Select(x => x.AmountMinorUnits).ToListAsync();
            Assert.Equal(new long[] { 1975, 100000 }, amounts); Assert.Equal(4, await db.BillingReceipts.CountAsync());
        }
        using var tenant = host.Browser();
        using var login = await AccountTestHost.PostAsync(tenant, "/Account/Login", "/Account/Login", ("Email", "billing-tenant@example.test"), ("Password", AccountTestHost.Password));
        using var read = await tenant.GetAsync("/Billing/Account/" + accountId);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode); var html = await read.Content.ReadAsStringAsync();
        Assert.Contains("$19.75", html); Assert.Contains("Payment status unavailable", html); Assert.DoesNotContain("Confirm reviewed change", html);
        using var tenantPrepare = await AccountTestHost.PostAsync(tenant, "/Billing", "/Billing/PrepareCharge", ("AccountId", accountId.ToString()));
        Assert.StartsWith("http://localhost/Account/Denied", tenantPrepare.Headers.Location?.OriginalString);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            var original = await db.Memberships.SingleAsync(x => x.Id == membership.Id); original.Revoke(managerId, "Membership ended", DateTimeOffset.UtcNow);
            db.ActiveGrants.Remove(await db.ActiveGrants.SingleAsync(x => x.MembershipId == membership.Id)); await db.SaveChangesAsync();
        }
        using var revokedRead = await tenant.GetAsync("/Billing/Account/" + accountId); Assert.Equal(HttpStatusCode.NotFound, revokedRead.StatusCode);
        using var managementRead = await manager.GetAsync("/Billing/ManageAccount/" + accountId); Assert.Equal(HttpStatusCode.OK, managementRead.StatusCode);
    }

    [Fact]
    public async Task Reviews_reject_forgery_stale_terms_wrong_actor_expiry_and_removed_authority()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync();
        var (managerId, building) = await host.SeedManagerAsync(); var membership = await SeedTenant(host, building);
        using var manager = host.Browser(); await host.LoginManagerAsync(manager, managerId);
        var accountId = await Open(host, manager, membership.Id); var account = await ReadAccount(host, accountId);
        var charge = await Review(manager, "/Billing/ManageAccount/" + accountId, "/Billing/PrepareCharge",
            ("AccountId", accountId.ToString()), ("Version", account.Version.ToString()), ("Amount", "25.00"), ("Description", "Reviewed charge"), ("DueOn", "2026-02-01"));
        using var forgery = await manager.PostAsync("/Billing/Confirm", new FormUrlEncodedContent(new Dictionary<string, string> { ["Ticket"] = charge.Ticket }));
        Assert.Equal(HttpStatusCode.BadRequest, forgery.StatusCode);
        using var altered = await Confirm(manager, charge.Ticket[..^10] + "altered"); Assert.Equal(HttpStatusCode.BadRequest, altered.StatusCode);
        string secondId;
        using (var scope = host.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
            var second = new OfficeUser { Email = "billing-second@example.test", UserName = "billing-second@example.test", EmailConfirmed = true };
            Assert.True((await users.CreateAsync(second, AccountTestHost.Password)).Succeeded);
            await users.ResetAuthenticatorKeyAsync(second); await users.SetTwoFactorEnabledAsync(second, true); secondId = second.Id;
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); db.BuildingManagers.Add(new BuildingManager { UserId = second.Id, BuildingId = building }); await db.SaveChangesAsync();
        }
        using var other = host.Browser();
        using var otherPassword = await AccountTestHost.PostAsync(other, "/Account/Login", "/Account/Login", ("Email", "billing-second@example.test"), ("Password", AccountTestHost.Password));
        using var otherFactor = await AccountTestHost.PostAsync(other, "/Account/Factor", "/Account/Factor", ("Code", await host.FactorCodeAsync(secondId)));
        using var wrongActor = await Confirm(other, charge.Ticket); Assert.Equal(HttpStatusCode.BadRequest, wrongActor.StatusCode);
        using (var scope = host.Services.CreateScope())
        {
            var tickets = scope.ServiceProvider.GetRequiredService<BillingReviewTickets>();
            Assert.Null(tickets.Read(charge.Ticket, "another-manager"));
            var original = tickets.Read(charge.Ticket, managerId)!;
            // Expiry is independent of recent authentication; this ticket was prepared six minutes ago.
            var expired = tickets.Create(original with { PreparedAt = host.Clock.GetUtcNow().AddMinutes(-6) });
            using var expiredPost = await Confirm(manager, expired.Ticket); Assert.Equal(HttpStatusCode.BadRequest, expiredPost.StatusCode);
        }
        var terms = await Review(manager, "/Billing/ManageAccount/" + accountId, "/Billing/PrepareTerms",
            ("AccountId", accountId.ToString()), ("Version", account.Version.ToString()), ("Amount", "1000.00"), ("DueDay", "1"), ("StartsOn", "2026-01-01"));
        using var termsSaved = await Confirm(manager, terms.Ticket); Assert.Equal(HttpStatusCode.Redirect, termsSaved.StatusCode);
        using var stale = await Confirm(manager, charge.Ticket); Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        account = await ReadAccount(host, accountId);
        var pending = await Review(manager, "/Billing/ManageAccount/" + accountId, "/Billing/PrepareCharge",
            ("AccountId", accountId.ToString()), ("Version", account.Version.ToString()), ("Amount", "25.00"), ("Description", "Authority must stay current"), ("DueOn", "2026-02-01"));
        host.Clock.Offset = TimeSpan.FromMinutes(6);
        using var staleMfa = await Confirm(manager, pending.Ticket); Assert.StartsWith("http://localhost/Account/Denied", staleMfa.Headers.Location?.OriginalString);
        host.Clock.Offset = TimeSpan.Zero;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); db.BuildingManagers.Remove(await db.BuildingManagers.SingleAsync(x => x.UserId == managerId)); await db.SaveChangesAsync();
        }
        // Post with a token obtained from the still-authenticated ordinary portal.
        using var noAuthority = await AccountTestHost.PostAsync(manager, "/Portal", "/Billing/Confirm", ("Ticket", pending.Ticket));
        Assert.StartsWith("http://localhost/Account/Denied", noAuthority.Headers.Location?.OriginalString);
        using var check = host.Services.CreateScope(); Assert.Empty(await check.ServiceProvider.GetRequiredService<OfficeDbContext>().Obligations.ToListAsync());
    }

    [Fact]
    public async Task Disabled_planning_blocks_reads_preparation_and_confirmation()
    {
        await using var host = new AccountTestHost { BillingPlanningEnabled = false }; await host.InitializeAsync();
        var (managerId, _) = await host.SeedManagerAsync(); using var manager = host.Browser(); await host.LoginManagerAsync(manager, managerId);
        using var read = await manager.GetAsync("/Billing/Manage"); Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        using var prepare = await AccountTestHost.PostAsync(manager, "/Portal", "/Billing/PrepareOpen", ("MembershipId", Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.NotFound, prepare.StatusCode);
        using var confirm = await AccountTestHost.PostAsync(manager, "/Portal", "/Billing/Confirm", ("Ticket", "anything")); Assert.Equal(HttpStatusCode.NotFound, confirm.StatusCode);
        using var scope = host.Services.CreateScope(); Assert.Empty(await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().BillingReceipts.ToListAsync());
    }

    private static async Task<Membership> SeedTenant(AccountTestHost host, Guid building)
    {
        using var scope = host.Services.CreateScope(); var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
        var user = new OfficeUser { Email = "billing-tenant@example.test", UserName = "billing-tenant@example.test", EmailConfirmed = true };
        Assert.True((await users.CreateAsync(user, AccountTestHost.Password)).Succeeded);
        var suite = new Suite { BuildingId = building, Name = "Billing office" }; var membership = Membership.Grant(suite.Id, user.Id, DateTimeOffset.UtcNow.AddDays(-1));
        var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); db.AddRange(suite, membership, new ActiveGrant { MembershipId = membership.Id, UserId = user.Id, SuiteId = suite.Id }); await db.SaveChangesAsync();
        return membership;
    }
    private static async Task<Guid> Open(AccountTestHost host, HttpClient manager, Guid membership)
    {
        var reviewed = await Review(manager, "/Billing/Manage", "/Billing/PrepareOpen", ("MembershipId", membership.ToString()), ("AccountId", Guid.NewGuid().ToString()));
        using (var scope = host.Services.CreateScope()) Assert.Empty(await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().RentAccounts.ToListAsync());
        using var confirmed = await Confirm(manager, reviewed.Ticket); Assert.Equal(HttpStatusCode.Redirect, confirmed.StatusCode);
        return Guid.Parse(confirmed.Headers.Location!.OriginalString.Split('/').Last());
    }
    private static async Task<AllenBuilding.Domain.Billing.RentAccount> ReadAccount(AccountTestHost host, Guid id)
    {
        using var scope = host.Services.CreateScope(); return await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().RentAccounts.AsNoTracking().SingleAsync(x => x.Id == id);
    }
    private static async Task<(string Ticket, string Html)> Review(HttpClient manager, string page, string action, params (string Key, string Value)[] values)
    {
        if (!values.Any(x => x.Key == "OperationId")) values = [.. values, ("OperationId", Guid.NewGuid().ToString())];
        using var response = await AccountTestHost.PostAsync(manager, page, action, values); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(); var ticket = WebUtility.HtmlDecode(Regex.Match(html, "name=\"Ticket\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        Assert.NotEmpty(ticket); return (ticket, html);
    }
    private static Task<HttpResponseMessage> Confirm(HttpClient manager, string ticket) => AccountTestHost.PostAsync(manager, "/Billing/Manage", "/Billing/Confirm", ("Ticket", ticket));
}
