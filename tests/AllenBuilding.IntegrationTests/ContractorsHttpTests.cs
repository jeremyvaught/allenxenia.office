using System.Net;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AllenBuilding.IntegrationTests;

public sealed class ContractorsHttpTests
{
    [Fact]
    public async Task Contractor_form_lists_only_managed_buildings_in_name_order_and_keeps_them_after_a_rejected_save()
    {
        await using var host = new AccountTestHost();
        await host.InitializeAsync();
        var (managerId, building) = await host.SeedManagerAsync();
        var annex = new Building { Name = "Annex" };
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            db.AddRange(annex, new Building { Name = "Unmanaged building" });
            db.BuildingManagers.Add(new BuildingManager { BuildingId = annex.Id, UserId = managerId });
            await db.SaveChangesAsync();
        }
        using var browser = host.Browser();
        await host.LoginManagerAsync(browser, managerId);

        var form = await browser.GetStringAsync("/Contractors/Create");
        var annexOption = form.IndexOf($"<option value=\"{annex.Id}\">Annex</option>", StringComparison.Ordinal);
        var mainOption = form.IndexOf($"<option value=\"{building}\">Synthetic building</option>", StringComparison.Ordinal);
        Assert.True(annexOption >= 0 && mainOption > annexOption, "Managed buildings are listed in name order.");
        Assert.DoesNotContain("Unmanaged building", form);

        using var rejected = await AccountTestHost.PostAsync(browser, "/Contractors/Create", "/Contractors/Save",
            ("BuildingId", building.ToString()), ("Name", ""), ("Trade", "Plumbing"));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var rejectedPage = await rejected.Content.ReadAsStringAsync();
        Assert.Contains(">Annex</option>", rejectedPage);
        Assert.DoesNotContain("Unmanaged building", rejectedPage);

        using var saved = await AccountTestHost.PostAsync(browser, "/Contractors/Create", "/Contractors/Save",
            ("BuildingId", building.ToString()), ("Name", "Synthetic Plumbing Co"), ("Trade", "Plumbing"));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Contains("Synthetic Plumbing Co", await browser.GetStringAsync("/Contractors"));
    }

    [Fact]
    public async Task Editing_a_contractor_requires_a_known_contact_in_a_managed_building_and_the_current_version()
    {
        await using var host = new AccountTestHost();
        await host.InitializeAsync();
        var (managerId, building) = await host.SeedManagerAsync();
        var unmanaged = await ManagementHttpTests.SeedBuildingAsync(host, "Unmanaged building");
        using var browser = host.Browser();
        await host.LoginManagerAsync(browser, managerId);
        var id = Guid.NewGuid();
        using (var created = await Save(id, building, null, "Synthetic Electric", "true"))
            Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var version = await VersionAsync(host, id);

        using var missing = await browser.GetAsync($"/Contractors/Edit/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var edit = await browser.GetStringAsync($"/Contractors/Edit/{id}");
        Assert.Contains("<h1 class=\"mb-6 text-3xl font-semibold\">Edit contractor</h1>", edit);
        Assert.Contains("The building cannot be changed.", edit);
        Assert.Contains("value=\"Synthetic Electric\"", edit);
        Assert.Contains($"value=\"{version}\"", edit);
        Assert.Contains("Active — available for new assignments", edit);

        using var stale = await Save(id, building, Guid.NewGuid(), "Stale rename", "true");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("This request has changed", await stale.Content.ReadAsStringAsync());
        using var unknown = await Save(Guid.NewGuid(), building, version, "Unknown contact", "true");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var foreign = await Save(Guid.NewGuid(), unmanaged, null, "Foreign contact", "true");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        using var inactiveCreate = await Save(Guid.NewGuid(), building, null, "Inactive on arrival", "false");
        Assert.Equal(HttpStatusCode.BadRequest, inactiveCreate.StatusCode);
        Assert.Contains("Check your information", await inactiveCreate.Content.ReadAsStringAsync());
        using var duplicateCreate = await Save(id, building, null, "Duplicate id", "true");
        Assert.Equal(HttpStatusCode.Conflict, duplicateCreate.StatusCode);

        using var deactivated = await Save(id, building, version, "Renamed Electric", "false");
        Assert.Equal(HttpStatusCode.Redirect, deactivated.StatusCode);
        Assert.Equal("/Contractors", deactivated.Headers.Location?.OriginalString);
        var list = await browser.GetStringAsync("/Contractors");
        Assert.Contains("Renamed Electric", list);
        Assert.Contains("Electrical · Inactive", list);
        Assert.DoesNotContain("Stale rename", list);

        using var check = host.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<OfficeDbContext>();
        var contact = await db.Contractors.AsNoTracking().SingleAsync();
        Assert.Equal(("Renamed Electric", false), (contact.Name, contact.Active));
        Assert.NotEqual(version, contact.Version);
        var actions = await db.AccountAudits.AsNoTracking().Where(x => x.SubjectId == id.ToString("D")).Select(x => x.Action).ToListAsync();
        Assert.Equal(["ContractorCreated", "ContractorDeactivated"], actions.Order());

        Task<HttpResponseMessage> Save(Guid contactId, Guid buildingId, Guid? expected, string name, string active) =>
            AccountTestHost.PostAsync(browser, "/Contractors/Create", "/Contractors/Save", ("Id", contactId.ToString()),
                ("BuildingId", buildingId.ToString()), ("Version", expected?.ToString() ?? ""), ("Name", name), ("Trade", "Electrical"), ("Active", active));
    }

    private static async Task<Guid> VersionAsync(AccountTestHost host, Guid id)
    {
        using var scope = host.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().Contractors.AsNoTracking().SingleAsync(x => x.Id == id)).Version;
    }
}
