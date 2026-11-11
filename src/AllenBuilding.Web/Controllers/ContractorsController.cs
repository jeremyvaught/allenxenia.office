using AllenBuilding.Application.Access;
using AllenBuilding.Application.Maintenance;
using AllenBuilding.Web.Models;
using AllenBuilding.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace AllenBuilding.Web.Controllers;

[Authorize(Policy = OfficeSecurity.ManagerPolicy), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ContractorsController(ContractorActions actions, BuildingAccessActions buildingAccess) : Controller
{
    [HttpGet] public async Task<IActionResult> Index() => View(await actions.ListAsync(HttpContext.RequestAborted));
    [HttpGet] public async Task<IActionResult> Create() { await Buildings(); return View("Edit", new ContractorForm()); }
    [HttpGet]
    public async Task<IActionResult> Edit(Guid id)
    {
        var contact = (await actions.ListAsync(HttpContext.RequestAborted)).SingleOrDefault(x => x.Id == id);
        if (contact is null)
            return NotFound();
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
        if (!ModelState.IsValid)
        { await Buildings(); Response.StatusCode = 400; return View("Edit", form); }
        var result = await actions.SaveAsync(form.Command(), HttpContext.RequestAborted);
        if (result == MaintenanceCode.Success)
            return RedirectToAction(nameof(Index));
        if (result == MaintenanceCode.Unavailable)
            return NotFound();
        Response.StatusCode = result == MaintenanceCode.Conflict ? 409 : 400;
        return View("~/Views/Maintenance/ActionError.cshtml", result);
    }
    private async Task Buildings() => ViewData["Buildings"] = (await buildingAccess.ManagedBuildingsAsync(HttpContext.RequestAborted))
        .Select(x => new SelectListItem(x.Name, x.Id.ToString())).ToList();
}
