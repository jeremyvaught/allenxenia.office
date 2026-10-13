using System.Net;
using AllenBuilding.Application.Access;
using AllenBuilding.Application.Reporting;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AllenBuilding.IntegrationTests;

public sealed class RentReportingHttpTests
{
    [Fact]
    public async Task Rent_report_requires_current_manager_MFA_scope_encodes_private_text_and_has_bounded_filters()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync(); var (managerId, buildingId) = await host.SeedManagerAsync(); Guid foreignId;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
            var tenant = new OfficeUser { UserName = "rent-http@example.test", Email = "rent-http@example.test", EmailConfirmed = true }; Assert.True((await users.CreateAsync(tenant, AccountTestHost.Password)).Succeeded);
            var foreign = new Building { Name = "Foreign private billing report" }; foreignId = foreign.Id;
            var suite = new Suite { BuildingId = buildingId, Name = "Report office" }; var outside = new Suite { BuildingId = foreign.Id, Name = "Foreign private suite" };
            var member = Membership.Grant(suite.Id, tenant.Id, host.Clock.GetUtcNow().AddYears(-1)); var otherMember = Membership.Grant(outside.Id, tenant.Id, host.Clock.GetUtcNow());
            var account = RentAccount.Open(Guid.NewGuid(), member.Id); var otherAccount = RentAccount.Open(Guid.NewGuid(), otherMember.Id);
            db.AddRange(foreign, suite, outside, member, otherMember, account, otherAccount,
                Obligation.Issue(account.Id, new Money(12345), "<script>rent-private()</script>", new DateOnly(2020, 1, 1), managerId, host.Clock.GetUtcNow()),
                Obligation.Issue(otherAccount.Id, new Money(999), "Foreign private charge", new DateOnly(2020, 1, 1), managerId, host.Clock.GetUtcNow())); await db.SaveChangesAsync();
        }
        using var guest = host.Browser(); using var tenantBrowser = host.Browser(); using var passwordOnly = host.Browser(); using var manager = host.Browser();
        using var anonymous = await guest.GetAsync("/Reports/Rent"); Assert.Contains("/Account/Login", anonymous.Headers.Location?.OriginalString);
        using var tenantLogin = await AccountTestHost.PostAsync(tenantBrowser, "/Account/Login", "/Account/Login", ("Email", "rent-http@example.test"), ("Password", AccountTestHost.Password));
        using var denied = await tenantBrowser.GetAsync("/Reports/Rent"); Assert.Contains("/Account/Denied", denied.Headers.Location?.OriginalString);
        using var password = await AccountTestHost.PostAsync(passwordOnly, "/Account/Login", "/Account/Login", ("Email", "manager@example.test"), ("Password", AccountTestHost.Password));
        using var noMfa = await passwordOnly.GetAsync("/Reports/Rent"); Assert.NotEqual(HttpStatusCode.OK, noMfa.StatusCode);
        await host.LoginManagerAsync(manager, managerId); using var response = await manager.GetAsync("/Reports/Rent"); var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Contains("no-store", response.Headers.CacheControl?.ToString()); Assert.Contains("&lt;script&gt;rent-private()&lt;/script&gt;", html);
        Assert.DoesNotContain("Foreign private", html); Assert.DoesNotContain("<script>rent-private()", html); Assert.Contains("$123.45", html); Assert.Contains("America/New_York", html); Assert.Contains("/Billing/ManageAccount/", html);
        foreach (var query in new[] { "Page=0", "Page=1001", "Page=invalid", "Group=999", "Group=invalid", "BuildingId=invalid", "BuildingId=" + Guid.Empty })
        { using var invalid = await manager.GetAsync("/Reports/Rent?" + query); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); }
        using var foreignRead = await manager.GetAsync("/Reports/Rent?BuildingId=" + foreignId); Assert.Equal(HttpStatusCode.NotFound, foreignRead.StatusCode);
        using (var scope = host.Services.CreateScope())
        { var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); db.BuildingManagers.Remove(await db.BuildingManagers.SingleAsync(x => x.UserId == managerId)); await db.SaveChangesAsync(); }
        using var revoked = await manager.GetAsync("/Reports/Rent"); Assert.Contains("/Account/Denied", revoked.Headers.Location?.OriginalString);
    }
    [Fact]
    public async Task Financial_report_failure_has_no_partial_money_or_private_exception_and_retries_the_same_report()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync(); var (managerId, _) = await host.SeedManagerAsync();
        await using var failing = host.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.Replace(ServiceDescriptor.Scoped<IManagementReportReader, FailingReader>())));
        using var manager = failing.CreateClient(new() { AllowAutoRedirect = false }); await host.LoginManagerAsync(manager, managerId);
        using var response = await manager.GetAsync("/Reports/Rent"); var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.Contains("Report temporarily unavailable", html); Assert.DoesNotContain("private-synthetic-database", html); Assert.DoesNotContain("Recorded outstanding", html); Assert.Contains("href=\"/Reports/Rent\"", html);
    }
    [Fact]
    public async Task Authorization_lost_after_a_financial_read_discards_all_results()
    {
        var actor = new Actor("synthetic", new HashSet<Guid> { Guid.NewGuid() }, true, false); var current = new ChangingActor(actor);
        var queries = new ManagementReportQueries(current, new ReleaseReader(actor.ManagedBuildings.Single()), TimeProvider.System);
        Assert.Null(await queries.RentBalancesAsync(new())); Assert.Equal(2, current.Reads);
    }
    private sealed class ChangingActor(Actor actor) : ICurrentActor
    { public int Reads { get; private set; } public Task<Actor?> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(++Reads == 1 ? actor : null); }
    private sealed class ReleaseReader(Guid building) : IManagementReportReader
    {
        public Task<RentBalanceReport?> RentBalancesAsync(Actor actor, RentBalanceQuery query, DateTimeOffset observedAt, DateOnly localDate, CancellationToken cancellationToken) =>
            Task.FromResult<RentBalanceReport?>(new(query, observedAt, localDate, new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), [], false, [new(building, "Protected")]));
        public Task<MaintenanceAgingReport?> MaintenanceAgingAsync(Actor actor, MaintenanceAgingQuery query, DateTimeOffset observedAt, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class FailingReader : IManagementReportReader
    {
        public Task<RentBalanceReport?> RentBalancesAsync(Actor actor, RentBalanceQuery query, DateTimeOffset observedAt, DateOnly localDate, CancellationToken cancellationToken) => throw new TimeoutException("private-synthetic-database");
        public Task<MaintenanceAgingReport?> MaintenanceAgingAsync(Actor actor, MaintenanceAgingQuery query, DateTimeOffset observedAt, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
