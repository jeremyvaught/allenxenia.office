using AllenBuilding.Application.Access;
using AllenBuilding.Application.Maintenance;
using AllenBuilding.Infrastructure.Persistence;
using AllenBuilding.Web.Models;
using AllenBuilding.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Web.Controllers;

[Authorize(Policy = OfficeSecurity.ManagerPolicy), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ContractorsController(ContractorActions actions, ICurrentActor current, OfficeDbContext db) : Controller
{
    [HttpGet] public async Task<IActionResult> Index() => View(await actions.ListAsync());
    [HttpGet] public async Task<IActionResult> Create() { await Buildings(); return View("Edit", new ContractorForm()); }
    [HttpGet]
    public async Task<IActionResult> Edit(Guid id)
    {
        var contact = (await actions.ListAsync()).SingleOrDefault(x => x.Id == id);
        if (contact is null) return NotFound();
        await Buildings();
        return View(new ContractorForm
        {
            Id = contact.Id,
            BuildingId = contact.BuildingId,
            Version = contact.Version,
            Name = contact.Name,
            Trade = contact.Trade,
            Email = contact.Email,
            Phone = contact.Phone,
            Active = contact.Active
        });
    }
    [HttpPost]
    public async Task<IActionResult> Save(ContractorForm form)
    {
        if (!ModelState.IsValid) { await Buildings(); Response.StatusCode = 400; return View("Edit", form); }
        var result = await actions.SaveAsync(form.Command());
        if (result == MaintenanceCode.Success) return RedirectToAction(nameof(Index));
        if (result == MaintenanceCode.Unavailable) return NotFound();
        Response.StatusCode = result == MaintenanceCode.Conflict ? 409 : 400;
        return View("~/Views/Maintenance/ActionError.cshtml", result);
    }
    private async Task Buildings()
    {
        var actor = (await current.GetAsync())!;
        ViewData["Buildings"] = await db.Buildings.AsNoTracking().Where(x => actor.ManagedBuildings.Contains(x.Id))
            .OrderBy(x => x.Name).Select(x => new SelectListItem(x.Name, x.Id.ToString())).ToListAsync();
    }
}
