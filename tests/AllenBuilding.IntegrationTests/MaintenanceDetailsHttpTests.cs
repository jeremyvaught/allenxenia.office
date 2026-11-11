using System.Net;
using AllenBuilding.Domain.Maintenance;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AllenBuilding.IntegrationTests;

public sealed class MaintenanceDetailsHttpTests
{
    [Fact]
    public async Task Tenant_form_errors_keep_the_typed_text_and_save_nothing()
    {
        await using var host = new AccountTestHost();
        await host.InitializeAsync();
        var (_, building) = await host.SeedManagerAsync();
        var (email, _, member) = await MaintenanceHttpTests.SeedTenants(host, building);
        using var tenant = host.Browser();
        await MaintenanceHttpTests.Login(tenant, email);

        using var missingTitle = await AccountTestHost.PostAsync(tenant, "/Maintenance/Create", "/Maintenance/Create",
            ("OperationId", Guid.NewGuid().ToString()), ("MembershipId", member.ToString()), ("Title", ""), ("Description", "Typed description canary"), ("Location", "Office"));
        Assert.Equal(HttpStatusCode.BadRequest, missingTitle.StatusCode);
        var form = await missingTitle.Content.ReadAsStringAsync();
        Assert.Contains("Typed description canary", form);
        Assert.Contains("name=\"Title\"", form);

        using var emptyOperation = await AccountTestHost.PostAsync(tenant, "/Maintenance/Create", "/Maintenance/Create",
            ("OperationId", Guid.Empty.ToString()), ("MembershipId", member.ToString()), ("Title", "No operation"), ("Description", "Fixture"), ("Location", "Office"));
        Assert.Equal(HttpStatusCode.BadRequest, emptyOperation.StatusCode);
        Assert.Contains("Nothing was saved for this action", await emptyOperation.Content.ReadAsStringAsync());

        foreach (var search in new[] { new string('x', 121), "sink\u0001" })
        {
            using var invalidSearch = await tenant.GetAsync("/Maintenance?search=" + Uri.EscapeDataString(search));
            Assert.Equal(HttpStatusCode.BadRequest, invalidSearch.StatusCode);
        }

        var operation = Guid.NewGuid();
        using var submitted = await AccountTestHost.PostAsync(tenant, "/Maintenance/Create", "/Maintenance/Create",
            ("OperationId", operation.ToString()), ("MembershipId", member.ToString()), ("Title", "Original request"), ("Description", "Fixture"), ("Location", "Office"));
        Assert.Equal(HttpStatusCode.Redirect, submitted.StatusCode);
        var path = submitted.Headers.Location!.OriginalString;
        using var reusedOperation = await AccountTestHost.PostAsync(tenant, "/Maintenance/Create", "/Maintenance/Create",
            ("OperationId", operation.ToString()), ("MembershipId", member.ToString()), ("Title", "Different request"), ("Description", "Fixture"), ("Location", "Office"));
        Assert.Equal(HttpStatusCode.Conflict, reusedOperation.StatusCode);
        Assert.Contains("This request has changed", await reusedOperation.Content.ReadAsStringAsync());

        var (id, version) = await Current(host);
        var longNote = "Long tenant note canary " + new string('n', 2000);
        using var tooLong = await AccountTestHost.PostAsync(tenant, path, "/Maintenance/Change", ("OperationId", Guid.NewGuid().ToString()),
            ("WorkOrderId", id.ToString()), ("Version", version.ToString()), ("Action", "Comment"), ("Note", longNote));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        var rerendered = await tooLong.Content.ReadAsStringAsync();
        Assert.Contains("Check your information and try again", rerendered);
        Assert.Contains(longNote, rerendered);
        Assert.Contains("Original request", rerendered);

        using var unknown = await AccountTestHost.PostAsync(tenant, path, "/Maintenance/Change", ("OperationId", Guid.NewGuid().ToString()),
            ("WorkOrderId", Guid.NewGuid().ToString()), ("Version", version.ToString()), ("Action", "Comment"), ("Note", longNote));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var internalNote = await AccountTestHost.PostAsync(tenant, path, "/Maintenance/Change", ("OperationId", Guid.NewGuid().ToString()),
            ("WorkOrderId", id.ToString()), ("Version", version.ToString()), ("Action", "Comment"), ("Internal", "true"), ("Note", "Forged internal note"));
        Assert.Equal(HttpStatusCode.NotFound, internalNote.StatusCode);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
        var order = await db.WorkOrders.AsNoTracking().SingleAsync();
        Assert.Equal("Original request", order.Title);
        Assert.Equal(version, order.Version);
        Assert.Equal("Submitted", Assert.Single(await db.WorkOrderHistory.ToListAsync()).Action);
        Assert.Single(await db.MaintenanceReceipts.ToListAsync());
    }

    [Fact]
    public async Task Manager_without_completed_mfa_cannot_read_or_change_requests_through_either_route()
    {
        await using var host = new AccountTestHost();
        await host.InitializeAsync();
        var (managerId, building) = await host.SeedManagerAsync(enrolled: false);
        var (email, _, member) = await MaintenanceHttpTests.SeedTenants(host, building);
        using var tenant = host.Browser();
        await MaintenanceHttpTests.Login(tenant, email);
        using var submitted = await AccountTestHost.PostAsync(tenant, "/Maintenance/Create", "/Maintenance/Create",
            ("OperationId", Guid.NewGuid().ToString()), ("MembershipId", member.ToString()), ("Title", "Unverified manager target"), ("Description", "Fixture"), ("Location", "Office"));
        var (id, version) = await Current(host);

        using var manager = host.Browser();
        using (var login = await AccountTestHost.PostAsync(manager, "/Account/Login", "/Account/Login", ("Email", "manager@example.test"), ("Password", AccountTestHost.Password)))
            Assert.Equal("/Portal", login.Headers.Location?.OriginalString);
        using var details = await manager.GetAsync($"/Maintenance/ManageDetails/{id}");
        Assert.StartsWith("http://localhost/Account/Denied", details.Headers.Location?.OriginalString);
        using var change = await AccountTestHost.PostAsync(manager, "/Portal", "/Maintenance/ManageChange", ("OperationId", Guid.NewGuid().ToString()),
            ("WorkOrderId", id.ToString()), ("Version", version.ToString()), ("Action", "Cancel"), ("Note", "Unverified cancel"));
        Assert.StartsWith("http://localhost/Account/Denied", change.Headers.Location?.OriginalString);
        using var tenantRoute = await AccountTestHost.PostAsync(manager, "/Portal", "/Maintenance/Change", ("OperationId", Guid.NewGuid().ToString()),
            ("WorkOrderId", id.ToString()), ("Version", version.ToString()), ("Action", "Comment"), ("Note", "Unverified comment"));
        Assert.Equal(HttpStatusCode.NotFound, tenantRoute.StatusCode);
        using var tenantRead = await manager.GetAsync($"/Maintenance/Details/{id}");
        Assert.Equal(HttpStatusCode.NotFound, tenantRead.StatusCode);
        Assert.DoesNotContain("Unverified manager target", await manager.GetStringAsync("/Maintenance"));
        using var create = await manager.GetAsync("/Maintenance/Create");
        Assert.Equal("/Portal", create.Headers.Location?.OriginalString);
        using var invalidCreate = await AccountTestHost.PostAsync(manager, "/Portal", "/Maintenance/Create",
            ("OperationId", Guid.NewGuid().ToString()), ("MembershipId", member.ToString()), ("Title", ""), ("Description", "Fixture"), ("Location", "Office"));
        Assert.Equal(HttpStatusCode.NotFound, invalidCreate.StatusCode);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
        var order = await db.WorkOrders.AsNoTracking().SingleAsync();
        Assert.Equal(WorkOrderStatus.Submitted, order.Status);
        Assert.Equal(version, order.Version);
        Assert.Single(await db.WorkOrderHistory.ToListAsync());
        Assert.DoesNotContain(await db.WorkOrderHistory.ToListAsync(), x => x.ActorId == managerId);
    }

    [Fact]
    public async Task Manager_on_behalf_and_assignment_errors_rerender_without_saving()
    {
        await using var host = new AccountTestHost();
        await host.InitializeAsync();
        var (managerId, building) = await host.SeedManagerAsync();
        var (_, _, member) = await MaintenanceHttpTests.SeedTenants(host, building);
        using var manager = host.Browser();
        await host.LoginManagerAsync(manager, managerId);
        using var invalid = await AccountTestHost.PostAsync(manager, "/Maintenance/CreateOnBehalf", "/Maintenance/CreateOnBehalf",
            ("OperationId", Guid.NewGuid().ToString()), ("MembershipId", member.ToString()), ("Title", "Phoned in"), ("Description", ""), ("Location", "Lobby canary"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var form = await invalid.Content.ReadAsStringAsync();
        Assert.Contains("Lobby canary", form);
        Assert.Contains("Shared office", form);
        using (var scope = host.Services.CreateScope())
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().WorkOrders.ToListAsync());

        using var recorded = await AccountTestHost.PostAsync(manager, "/Maintenance/CreateOnBehalf", "/Maintenance/CreateOnBehalf",
            ("OperationId", Guid.NewGuid().ToString()), ("MembershipId", member.ToString()), ("Title", "Phoned in"), ("Description", "Door sticks"), ("Location", "Lobby"));
        var path = recorded.Headers.Location!.OriginalString;
        var (id, version) = await Current(host);
        using var triage = await AccountTestHost.PostAsync(manager, path, "/Maintenance/ManageChange", ("OperationId", Guid.NewGuid().ToString()),
            ("WorkOrderId", id.ToString()), ("Version", version.ToString()), ("Action", "Triage"), ("Priority", "High"), ("Trade", "General"));
        Assert.Equal(HttpStatusCode.Redirect, triage.StatusCode);
        (_, version) = await Current(host);
        using var noAssignee = await AccountTestHost.PostAsync(manager, path, "/Maintenance/ManageChange", ("OperationId", Guid.NewGuid().ToString()),
            ("WorkOrderId", id.ToString()), ("Version", version.ToString()), ("Action", "Assign"));
        Assert.Equal(HttpStatusCode.BadRequest, noAssignee.StatusCode);
        var html = await noAssignee.Content.ReadAsStringAsync();
        Assert.Contains("Check your information and try again", html);
        Assert.Contains("Next step", html);
        using var unknownTransition = await AccountTestHost.PostAsync(manager, path, "/Maintenance/ManageChange", ("OperationId", Guid.NewGuid().ToString()),
            ("WorkOrderId", id.ToString()), ("Version", version.ToString()), ("Action", "999"));
        Assert.Equal(HttpStatusCode.BadRequest, unknownTransition.StatusCode);

        using var check = host.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<OfficeDbContext>();
        var order = await db.WorkOrders.AsNoTracking().SingleAsync();
        Assert.Equal(WorkOrderStatus.Triaged, order.Status);
        Assert.Null(order.AssignedManagerId);
        Assert.Equal(2, await db.WorkOrderHistory.CountAsync());
    }

    [Fact]
    public async Task Details_show_each_status_next_step_and_keep_internal_notes_for_managers()
    {
        await using var host = new AccountTestHost();
        await host.InitializeAsync();
        var (managerId, building) = await host.SeedManagerAsync();
        var (email, _, member) = await MaintenanceHttpTests.SeedTenants(host, building);
        using var tenant = host.Browser();
        await MaintenanceHttpTests.Login(tenant, email);
        using var submitted = await AccountTestHost.PostAsync(tenant, "/Maintenance/Create", "/Maintenance/Create",
            ("OperationId", Guid.NewGuid().ToString()), ("MembershipId", member.ToString()), ("Title", "Lifecycle request"), ("Description", "Fixture"), ("Location", "Office"));
        var tenantPath = submitted.Headers.Location!.OriginalString;
        var (id, _) = await Current(host);
        var managerPath = $"/Maintenance/ManageDetails/{id}";
        using var manager = host.Browser();
        await host.LoginManagerAsync(manager, managerId);

        var page = await manager.GetStringAsync(managerPath);
        Assert.Contains("Submitted", page);
        Assert.Contains("name=\"Priority\"", page);
        Assert.DoesNotContain("Assign to me", page);
        Assert.Contains("Internal note — visible only to management", page);
        // The page names the requester, makes the forward step the one solid button and tucks cancelling away.
        Assert.Contains("Submitted by " + email, page);
        Assert.Contains("class=\"button w-full\">Triage</button>", page);
        Assert.Contains("class=\"quiet-button\">Add comment</button>", page);
        Assert.Matches("<details class=[^>]*><summary[^>]*>Cancel this request</summary>", page);

        async Task<string> Step(string action, params (string Key, string Value)[] extra)
        {
            var (_, version) = await Current(host);
            using var response = await AccountTestHost.PostAsync(manager, managerPath, "/Maintenance/ManageChange", new[]
            { ("OperationId", Guid.NewGuid().ToString()), ("WorkOrderId", id.ToString()), ("Version", version.ToString()), ("Action", action) }.Concat(extra).ToArray());
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal(managerPath, response.Headers.Location?.OriginalString);
            return await manager.GetStringAsync(managerPath);
        }

        page = await Step("Triage", ("Priority", "Urgent"), ("Trade", "HeatingCooling"));
        Assert.Contains("Priority and trade set", page);
        Assert.Contains("Trade: Heating and cooling", page);
        Assert.Contains("Assign to me", page);
        Assert.Contains("Save assignment", page);
        Assert.Contains($"value=\"manager:{managerId}\"", page);
        Assert.DoesNotContain(">Start</button>", page);

        page = await Step("Assign", ("Assignee", "manager:" + managerId));
        Assert.Contains("Assignment updated", page);
        Assert.Contains("Assigned to:", page);
        Assert.Contains(">Start</button>", page);

        page = await Step("AssignSelf");
        Assert.Contains("Assigned to manager", page);
        page = await Step("Comment", ("Internal", "true"), ("Note", "Manager-only internal canary"));
        Assert.Contains("Manager-only internal canary", page);
        Assert.Contains("Your internal note", page);
        Assert.Contains("by you", page);

        page = await Step("Start");
        Assert.Contains("Work started", page);
        Assert.Contains(">Complete</button>", page);
        Assert.DoesNotContain(">Start</button>", page);

        page = await Step("Complete", ("Note", "Replaced the thermostat"));
        Assert.Contains("Work completed", page);
        Assert.Contains("Check it, then close the request.", page);
        Assert.Contains("Verify work and close", page);
        Assert.Contains("Reason for reopening the request", page);
        Assert.Contains("Replaced the thermostat", page);

        var tenantPage = await tenant.GetStringAsync(tenantPath);
        Assert.Contains("Work completed", tenantPage);
        Assert.Contains("Replaced the thermostat", tenantPage);
        Assert.DoesNotContain("Manager-only internal canary", tenantPage);
        Assert.DoesNotContain("internal note", tenantPage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Next step", tenantPage);
        Assert.DoesNotContain("ManageChange", tenantPage);
        Assert.DoesNotContain("Submitted by", tenantPage);
        Assert.Contains("by Building manager", tenantPage);
        Assert.Contains("class=\"button\">Add comment</button>", tenantPage);

        // Each side sees who wrote a comment; the tenant's email is shown only to managers.
        await Step("Comment", ("Note", "Manager reply canary"));
        using (var commented = await AccountTestHost.PostAsync(tenant, tenantPath, "/Maintenance/Change",
            ("OperationId", Guid.NewGuid().ToString()), ("WorkOrderId", id.ToString()), ("Version", (await Current(host)).Item2.ToString()),
            ("Action", "Comment"), ("Note", "Tenant comment canary")))
            Assert.Equal(HttpStatusCode.Redirect, commented.StatusCode);
        tenantPage = await tenant.GetStringAsync(tenantPath);
        Assert.Contains("Your comment", tenantPage);
        Assert.Contains("Comment from Building manager", tenantPage);
        Assert.DoesNotContain("(tenant)", tenantPage);
        page = await manager.GetStringAsync(managerPath);
        Assert.Contains($"Comment from {email} (tenant)", page);
        Assert.Contains("Your comment", page);

        // The delivery page says which request each email was about, never the hidden note.
        var delivery = await manager.GetStringAsync("/Notifications");
        Assert.Contains($"href=\"/Maintenance/ManageDetails/{id}\"", delivery);
        Assert.Contains("Lifecycle request", delivery);
        Assert.Contains("Work completed", delivery);
        Assert.Contains("sent to the tenant", delivery);
        Assert.Contains("Request submitted", delivery);
        Assert.Contains("sent to a manager", delivery);
        Assert.DoesNotContain("canary", delivery);

        page = await Step("Close");
        Assert.Contains("Work verified", page);
        Assert.Contains("Reason for reopening the request", page);
        Assert.DoesNotContain("Cancel request", page);
        Assert.DoesNotContain("Verify work and close", page);

        page = await Step("Reopen", ("Note", "Still too warm"));
        Assert.Contains("Request reopened", page);
        Assert.Contains("Cancel request", page);

        page = await Step("Cancel", ("Note", "Duplicate of another request"));
        Assert.Contains("Request cancelled", page);
        Assert.Contains("Reason for reopening the request", page);
        Assert.DoesNotContain("Cancel request", page);
        Assert.DoesNotContain("Assign to me", page);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
        Assert.Equal(WorkOrderStatus.Cancelled, (await db.WorkOrders.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(HistoryVisibility.Internal, (await db.WorkOrderHistory.SingleAsync(x => x.Note == "Manager-only internal canary")).Visibility);
    }

    [Fact]
    public async Task Long_history_shows_the_most_recent_hundred_and_labels_unknown_entries_generically()
    {
        await using var host = new AccountTestHost();
        await host.InitializeAsync();
        var (_, building) = await host.SeedManagerAsync();
        var (email, _, member) = await MaintenanceHttpTests.SeedTenants(host, building);
        using var tenant = host.Browser();
        await MaintenanceHttpTests.Login(tenant, email);
        using var submitted = await AccountTestHost.PostAsync(tenant, "/Maintenance/Create", "/Maintenance/Create",
            ("OperationId", Guid.NewGuid().ToString()), ("MembershipId", member.ToString()), ("Title", "Long history"), ("Description", "Fixture"), ("Location", "Office"));
        var path = submitted.Headers.Location!.OriginalString;
        var (id, _) = await Current(host);
        var html = await tenant.GetStringAsync(path);
        Assert.DoesNotContain("Showing the 100 most recent history entries", html);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            var now = DateTimeOffset.UtcNow;
            for (var index = 0; index < 105; index++)
                db.WorkOrderHistory.Add(new WorkOrderHistory
                {
                    WorkOrderId = id,
                    ActorId = "synthetic-import",
                    Action = "LegacyImport",
                    Status = WorkOrderStatus.Submitted,
                    Note = "Imported entry " + index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture),
                    Visibility = HistoryVisibility.TenantVisible,
                    OccurredAt = now.AddMinutes(index + 1)
                });
            await db.SaveChangesAsync();
        }
        html = await tenant.GetStringAsync(path);
        Assert.Contains("Showing the 100 most recent history entries", html);
        Assert.Contains("Request updated", html);
        Assert.Contains("Imported entry 104", html);
        Assert.DoesNotContain("Imported entry 004", html);
        Assert.DoesNotContain("Request submitted", html);
    }

    private static async Task<(Guid Id, Guid Version)> Current(AccountTestHost host)
    {
        using var scope = host.Services.CreateScope();
        var order = await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().WorkOrders.AsNoTracking().SingleAsync();
        return (order.Id, order.Version);
    }
}
