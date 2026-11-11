using System.Net;

namespace AllenBuilding.IntegrationTests;

public sealed class PortalHttpTests
{
    [Fact]
    public async Task Tenant_and_manager_each_see_their_own_navigation_and_what_needs_attention()
    {
        await using var host = new AccountTestHost { BillingPlanningEnabled = false };
        await host.InitializeAsync();
        var (managerId, building) = await host.SeedManagerAsync();
        var (email, _, member) = await MaintenanceHttpTests.SeedTenants(host, building);

        using var tenant = host.Browser();
        await MaintenanceHttpTests.Login(tenant, email);
        var empty = await tenant.GetStringAsync("/Portal");
        Assert.Contains("Nothing open right now.", empty);
        Assert.Contains(">Requests</a>", empty);
        Assert.DoesNotContain("Needs attention", empty);
        Assert.DoesNotContain(">Access</a>", empty);
        Assert.DoesNotContain("billing", empty, StringComparison.OrdinalIgnoreCase);
        // The assistant is off in this host, so its link is not offered.
        Assert.DoesNotContain("href=\"/Assistant\"", empty);

        using var submitted = await AccountTestHost.PostAsync(tenant, "/Maintenance/Create", "/Maintenance/Create",
            ("OperationId", Guid.NewGuid().ToString()), ("MembershipId", member.ToString()),
            ("Title", "Flickering hallway light"), ("Description", "Synthetic"), ("Location", "Hallway"));
        Assert.Equal(HttpStatusCode.Redirect, submitted.StatusCode);
        var withRequest = await tenant.GetStringAsync("/Portal");
        Assert.Contains("Flickering hallway light", withRequest);
        Assert.Contains("href=\"/Maintenance/Details/", withRequest);
        Assert.DoesNotContain("Nothing open right now.", withRequest);

        using var manager = host.Browser();
        await host.LoginManagerAsync(manager, managerId);
        var dashboard = await manager.GetStringAsync("/Portal");
        Assert.Contains("Needs attention", dashboard);
        Assert.Matches(@">1</span><span[^>]*>New requests to triage<", dashboard);
        Assert.Matches(@">0</span><span[^>]*>Requests in progress<", dashboard);
        Assert.Matches(@">0</span><span[^>]*>Finished, waiting for you to verify<", dashboard);
        Assert.Matches(@">0</span><span[^>]*>Invitations not yet accepted<", dashboard);
        Assert.Contains("href=\"/Maintenance/Manage?Status=Submitted\"", dashboard);
        Assert.Contains("Flickering hallway light", dashboard);
        Assert.Contains("href=\"/Maintenance/ManageDetails/", dashboard);
        foreach (var link in new[] { "Maintenance", "Access", "Help articles", "Contractors", "Reports", "Notifications" })
            Assert.Contains($">{link}</a>", dashboard);
        Assert.DoesNotContain("Financial exceptions", dashboard);
        Assert.DoesNotContain("Rent balance report", dashboard);

        // Dormant billing pages are not reachable while billing is off.
        foreach (var path in new[] { "/FinancialExceptions", "/Reports/Rent", "/Billing/Manage" })
        {
            using var hidden = await manager.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        }
    }

    [Fact]
    public async Task Billing_links_and_pages_appear_only_when_billing_is_enabled()
    {
        await using var host = new AccountTestHost();
        await host.InitializeAsync();
        var (managerId, _) = await host.SeedManagerAsync();
        using var manager = host.Browser();
        await host.LoginManagerAsync(manager, managerId);
        var dashboard = await manager.GetStringAsync("/Portal");
        Assert.Contains("Financial exceptions", dashboard);
        Assert.Contains("Rent balance report", dashboard);
        using var page = await manager.GetAsync("/FinancialExceptions");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
    }
}
