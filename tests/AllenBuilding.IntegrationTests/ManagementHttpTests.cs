using System.Net;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AllenBuilding.IntegrationTests;

public sealed class ManagementHttpTests
{
    [Fact]
    public async Task Adding_a_suite_is_audited_unique_per_building_and_limited_to_managed_buildings()
    {
        await using var host = new AccountTestHost();
        await host.InitializeAsync();
        var (managerId, building) = await host.SeedManagerAsync();
        var foreign = new Building { Name = "Unmanaged building" };
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            db.Add(foreign);
            await db.SaveChangesAsync();
        }
        using var browser = host.Browser();
        await host.LoginManagerAsync(browser, managerId);

        using var created = await AccountTestHost.PostAsync(browser, "/Management", "/Management/AddSuite",
            ("BuildingId", building.ToString()), ("Name", "  Suite 210 "));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        Assert.Equal("/Management", created.Headers.Location?.OriginalString);
        var page = await browser.GetStringAsync("/Management");
        Assert.Contains("Suite Suite 210 added. You can now invite a tenant or grant access to this suite.", page);
        Assert.Contains("<option value=\"", page);
        Assert.Contains(">Suite 210</option>", page);
        Assert.Contains(">Synthetic building</option>", page);
        Assert.DoesNotContain("Unmanaged building", page);

        using var duplicate = await AccountTestHost.PostAsync(browser, "/Management", "/Management/AddSuite",
            ("BuildingId", building.ToString()), ("Name", "Suite 210"));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var unmanaged = await AccountTestHost.PostAsync(browser, "/Management", "/Management/AddSuite",
            ("BuildingId", foreign.Id.ToString()), ("Name", "Suite 999"));
        Assert.Equal(HttpStatusCode.BadRequest, unmanaged.StatusCode);
        using var blank = await AccountTestHost.PostAsync(browser, "/Management", "/Management/AddSuite",
            ("BuildingId", building.ToString()), ("Name", "   "));
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);

        using var check = host.Services.CreateScope();
        var saved = check.ServiceProvider.GetRequiredService<OfficeDbContext>();
        var suite = await saved.Suites.AsNoTracking().SingleAsync(x => x.BuildingId == building);
        Assert.Equal("Suite 210", suite.Name);
        Assert.False(await saved.Suites.AnyAsync(x => x.BuildingId == foreign.Id));
        var audit = await saved.AccountAudits.AsNoTracking().SingleAsync(x => x.Action == "SuiteCreated");
        Assert.Equal(managerId, audit.ActorId);
        Assert.Equal(suite.Id.ToString(), audit.SubjectId);
    }
}
