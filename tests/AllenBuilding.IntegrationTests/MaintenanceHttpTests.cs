using System.Net;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AllenBuilding.IntegrationTests;

public sealed class MaintenanceHttpTests
{
    [Fact]
    public async Task Tenant_http_requests_are_private_and_retries_preserve_a_single_commit()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync();
        var (_, buildingId) = await host.SeedManagerAsync();
        var (first, second, membership) = await SeedTenants(host, buildingId);
        using var a = host.Browser(); using var b = host.Browser();
        await Login(a, first); await Login(b, second);
        var operation = Guid.NewGuid();
        var values = new[] { ("OperationId", operation.ToString()), ("MembershipId", membership.ToString()),
            ("Title", "Private leaking sink"), ("Description", "<script>alert('unsafe')</script>"), ("Location", "Kitchen") };
        using var submitted = await AccountTestHost.PostAsync(a, "/Maintenance/Create", "/Maintenance/Create", values);
        Assert.Equal(HttpStatusCode.Redirect, submitted.StatusCode);
        var path = submitted.Headers.Location!.OriginalString;
        using var retried = await AccountTestHost.PostAsync(a, "/Maintenance/Create", "/Maintenance/Create", values);
        Assert.Equal(path, retried.Headers.Location!.OriginalString);
        using var privateRead = await b.GetAsync(path); Assert.Equal(HttpStatusCode.NotFound, privateRead.StatusCode);
        var body = await a.GetStringAsync(path);
        Assert.Contains("&lt;script&gt;", body); Assert.DoesNotContain("<script>alert", body);
        var secondList = await b.GetStringAsync("/Maintenance"); Assert.DoesNotContain("Private leaking sink", secondList);
        using var scope = host.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
        var order = await db.WorkOrders.SingleAsync();
        using var forbidden = await AccountTestHost.PostAsync(a, path, "/Maintenance/Change", ("OperationId", Guid.NewGuid().ToString()),
            ("WorkOrderId", order.Id.ToString()), ("Version", order.Version.ToString()), ("Action", "Close"));
        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
        Assert.Single(await db.WorkOrderHistory.ToListAsync()); Assert.Single(await db.MaintenanceReceipts.ToListAsync());
    }

    [Fact]
    public async Task Revocation_blocks_a_still_signed_in_tenant_without_waiting_for_external_cleanup()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync();
        var (managerId, buildingId) = await host.SeedManagerAsync();
        var (first, _, membershipId) = await SeedTenants(host, buildingId);
        using var tenant = host.Browser(); await Login(tenant, first);
        using var submitted = await AccountTestHost.PostAsync(tenant, "/Maintenance/Create", "/Maintenance/Create",
            ("OperationId", Guid.NewGuid().ToString()), ("MembershipId", membershipId.ToString()), ("Title", "Private request"), ("Description", "Description"), ("Location", "Kitchen"));
        var path = submitted.Headers.Location!.OriginalString;
        Guid version;
        using (var scope = host.Services.CreateScope()) version = (await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().Memberships.SingleAsync(x => x.Id == membershipId)).Version;
        using var manager = host.Browser(); await host.LoginManagerAsync(manager, managerId);
        using var revoked = await AccountTestHost.PostAsync(manager, "/Management", "/Management/Revoke",
            ("MembershipId", membershipId.ToString()), ("Version", version.ToString()), ("Reason", "Membership ended"));
        Assert.Equal(HttpStatusCode.Redirect, revoked.StatusCode);
        using var read = await tenant.GetAsync(path); Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        var index = await tenant.GetStringAsync("/Maintenance"); Assert.DoesNotContain("Private request", index);
        using var verification = host.Services.CreateScope(); var db = verification.ServiceProvider.GetRequiredService<OfficeDbContext>();
        Assert.Single(await db.WorkOrders.ToListAsync()); Assert.DoesNotContain(await db.ActiveGrants.ToListAsync(), x => x.MembershipId == membershipId);
        Assert.True(await db.AccountAudits.AnyAsync(x => x.Action == "AccessRevoked"));
    }

    [Fact]
    public async Task Manager_submission_keeps_tenant_ownership_and_requires_challenged_authority_and_antiforgery()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync();
        var (managerId, buildingId) = await host.SeedManagerAsync();
        var (first, second, membership) = await SeedTenants(host, buildingId);
        using var manager = host.Browser(); await host.LoginManagerAsync(manager, managerId);
        using var forged = await manager.PostAsync("/Maintenance/CreateOnBehalf", new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        var values = new[] { ("OperationId", Guid.NewGuid().ToString()), ("MembershipId", membership.ToString()),
            ("Title", "Telephone report"), ("Description", "Tenant reported a stuck door"), ("Location", "Entrance"), ("RequesterUserId", managerId), ("SuiteId", Guid.NewGuid().ToString()) };
        using var recorded = await AccountTestHost.PostAsync(manager, "/Maintenance/CreateOnBehalf", "/Maintenance/CreateOnBehalf", values);
        Assert.Equal(HttpStatusCode.Redirect, recorded.StatusCode);
        var managerPath = recorded.Headers.Location!.OriginalString;
        using var replay = await AccountTestHost.PostAsync(manager, "/Maintenance/CreateOnBehalf", "/Maintenance/CreateOnBehalf", values);
        Assert.Equal(managerPath, replay.Headers.Location!.OriginalString);
        using var tenant = host.Browser(); using var peer = host.Browser(); await Login(tenant, first); await Login(peer, second);
        var tenantPath = managerPath.Replace("ManageDetails", "Details", StringComparison.Ordinal);
        var html = await tenant.GetStringAsync(tenantPath); Assert.Contains("Request recorded by manager for tenant", html);
        using var deniedRead = await peer.GetAsync(tenantPath); Assert.Equal(HttpStatusCode.NotFound, deniedRead.StatusCode);
        using var deniedCreate = await tenant.GetAsync("/Maintenance/CreateOnBehalf"); Assert.StartsWith("http://localhost/Account/Denied", deniedCreate.Headers.Location?.OriginalString);
        using var wrongRoute = await AccountTestHost.PostAsync(manager, managerPath, "/Maintenance/Create", values);
        Assert.Equal(HttpStatusCode.NotFound, wrongRoute.StatusCode);
        using var scope = host.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
        var order = await db.WorkOrders.SingleAsync(); Assert.Equal(membership, order.RequesterMembershipId); Assert.NotEqual(managerId, order.RequesterUserId);
        Assert.Equal(managerId, (await db.WorkOrderHistory.SingleAsync()).ActorId); Assert.Single(await db.MaintenanceReceipts.ToListAsync());
    }

    private static async Task Login(HttpClient client, string email)
    {
        using var result = await AccountTestHost.PostAsync(client, "/Account/Login", "/Account/Login", ("Email", email), ("Password", AccountTestHost.Password));
        Assert.Equal(HttpStatusCode.Redirect, result.StatusCode);
    }

    private static async Task<(string First, string Second, Guid Membership)> SeedTenants(AccountTestHost host, Guid building)
    {
        using var scope = host.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
        var suite = new Suite { BuildingId = building, Name = "Shared office" }; db.Suites.Add(suite); await db.SaveChangesAsync();
        Guid membershipId = Guid.Empty;
        foreach (var email in new[] { "first@example.test", "second@example.test" })
        {
            var user = new OfficeUser { Email = email, UserName = email, EmailConfirmed = true };
            Assert.True((await users.CreateAsync(user, AccountTestHost.Password)).Succeeded);
            var membership = Membership.Grant(suite.Id, user.Id, DateTimeOffset.UtcNow.AddDays(-1));
            if (membershipId == Guid.Empty) membershipId = membership.Id;
            db.AddRange(membership, new ActiveGrant { UserId = user.Id, SuiteId = suite.Id, MembershipId = membership.Id }); await db.SaveChangesAsync();
        }
        return ("first@example.test", "second@example.test", membershipId);
    }
}
