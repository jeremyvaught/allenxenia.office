using System.Net;
using AllenBuilding.Application.Access;
using AllenBuilding.Application.Reporting;
using AllenBuilding.Domain.Maintenance;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AllenBuilding.IntegrationTests;

public sealed class ReportingHttpTests
{
    [Fact]
    public async Task Aging_report_requires_current_challenged_manager_scope_and_rejects_invalid_queries()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync();
        var (managerId, buildingId) = await host.SeedManagerAsync(); Guid foreignId;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
            var tenant = new OfficeUser { Email = "report-tenant@example.test", UserName = "report-tenant@example.test", EmailConfirmed = true };
            Assert.True((await users.CreateAsync(tenant, AccountTestHost.Password)).Succeeded);
            var foreign = new Building { Name = "Foreign private report building" }; foreignId = foreign.Id;
            var suite = new Suite { BuildingId = buildingId, Name = "Suite one" }; var foreignSuite = new Suite { BuildingId = foreign.Id, Name = "Private suite" };
            var membership = Membership.Grant(suite.Id, tenant.Id, host.Clock.GetUtcNow().AddYears(-1)); var outside = Membership.Grant(foreignSuite.Id, tenant.Id, host.Clock.GetUtcNow().AddYears(-1));
            db.AddRange(foreign, suite, foreignSuite, membership, outside, new ActiveGrant { UserId = tenant.Id, SuiteId = suite.Id, MembershipId = membership.Id },
                WorkOrder.Submit(suite.Id, membership.Id, tenant.Id, "<script>privateTitle()</script>", "Hidden private description", "Office", host.Clock.GetUtcNow().AddDays(-35)),
                WorkOrder.Submit(foreignSuite.Id, outside.Id, tenant.Id, "Foreign private report request", "Private", "Office", host.Clock.GetUtcNow().AddDays(-50)));
            await db.SaveChangesAsync();
        }
        using var guest = host.Browser(); using var tenantBrowser = host.Browser(); using var pendingManager = host.Browser(); using var manager = host.Browser();
        using var guestDenied = await guest.GetAsync("/Reports/Maintenance"); Assert.StartsWith("http://localhost/Account/Login", guestDenied.Headers.Location?.OriginalString);
        using var password = await AccountTestHost.PostAsync(pendingManager, "/Account/Login", "/Account/Login", ("Email", "manager@example.test"), ("Password", AccountTestHost.Password));
        using var noFactor = await pendingManager.GetAsync("/Reports/Maintenance"); Assert.NotEqual(HttpStatusCode.OK, noFactor.StatusCode);
        using var login = await AccountTestHost.PostAsync(tenantBrowser, "/Account/Login", "/Account/Login", ("Email", "report-tenant@example.test"), ("Password", AccountTestHost.Password));
        using var tenantDenied = await tenantBrowser.GetAsync("/Reports/Maintenance"); Assert.StartsWith("http://localhost/Account/Denied", tenantDenied.Headers.Location?.OriginalString);
        await host.LoginManagerAsync(manager, managerId);
        using var response = await manager.GetAsync("/Reports/Maintenance?BuildingId=&Bucket=AtLeast30Days"); var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("&lt;script&gt;privateTitle()&lt;/script&gt;", html); Assert.DoesNotContain("<script>privateTitle", html);
        Assert.DoesNotContain("Hidden private description", html); Assert.DoesNotContain("Foreign private", html); Assert.Contains("1 active request", html);
        foreach (var query in new[] { "Page=0", "Page=1001", "Page=invalid", "Bucket=999", "Bucket=unsupported", "BuildingId=invalid", "BuildingId=" + Guid.Empty })
        { using var invalid = await manager.GetAsync("/Reports/Maintenance?" + query); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); }
        using var foreignRead = await manager.GetAsync("/Reports/Maintenance?BuildingId=" + foreignId); Assert.Equal(HttpStatusCode.NotFound, foreignRead.StatusCode);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); db.BuildingManagers.Remove(await db.BuildingManagers.SingleAsync(x => x.UserId == managerId)); await db.SaveChangesAsync();
        }
        using var ended = await manager.GetAsync("/Reports/Maintenance"); Assert.StartsWith("http://localhost/Account/Denied", ended.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Report_provider_failure_returns_a_safe_retry_page_without_diagnostics_or_partial_totals()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync(); var (managerId, _) = await host.SeedManagerAsync();
        await using var failing = host.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.Replace(ServiceDescriptor.Scoped<IManagementReportReader, FailingReader>())));
        using var manager = failing.CreateClient(new() { AllowAutoRedirect = false }); await host.LoginManagerAsync(manager, managerId);
        using var response = await manager.GetAsync("/Reports/Maintenance"); var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.Contains("Report temporarily unavailable", html);
        Assert.DoesNotContain("synthetic-sensitive-diagnostic", html); Assert.DoesNotContain("active requests", html);
    }
    [Fact]
    public async Task A_slow_report_cancels_at_its_deadline_and_preserves_caller_cancellation()
    {
        var actor = new Actor("synthetic-report-manager", new HashSet<Guid> { Guid.NewGuid() }, true, false);
        var slow = new SlowReader(); var queries = new ManagementReportQueries(new FixedActor(actor), slow, TimeProvider.System);
        await Assert.ThrowsAsync<TimeoutException>(() => queries.MaintenanceAgingAsync(new())); Assert.True(slow.Cancelled);
        using var caller = new CancellationTokenSource(); caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queries.MaintenanceAgingAsync(new(), caller.Token));
    }
    private sealed class FixedActor(Actor actor) : ICurrentActor
    { public Task<Actor?> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult<Actor?>(actor); }
    private sealed class SlowReader : IManagementReportReader
    {
        public bool Cancelled { get; private set; }
        public async Task<MaintenanceAgingReport?> MaintenanceAgingAsync(Actor actor, MaintenanceAgingQuery query, DateTimeOffset observedAt, CancellationToken cancellationToken)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return null; }
            catch (OperationCanceledException) { Cancelled = true; throw; }
        }
    }
    private sealed class FailingReader : IManagementReportReader
    {
        public Task<MaintenanceAgingReport?> MaintenanceAgingAsync(Actor actor, MaintenanceAgingQuery query, DateTimeOffset observedAt, CancellationToken cancellationToken) =>
            throw new TimeoutException("synthetic-sensitive-diagnostic");
    }
}
