using System.Net;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Persistence;
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
}
