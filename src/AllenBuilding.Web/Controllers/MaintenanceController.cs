using AllenBuilding.Application.Access;
using AllenBuilding.Application.Maintenance;
using AllenBuilding.Web.Models;
using AllenBuilding.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AllenBuilding.Web.Controllers;

[Authorize, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class MaintenanceController(MaintenanceActions actions) : Controller
{
    [HttpGet] public async Task<IActionResult> Index() => View(await actions.ListAsync(false));

    [Authorize(Policy = OfficeSecurity.ManagerPolicy), HttpGet]
    public async Task<IActionResult> Manage()
    {
        ViewData["Manager"] = true;
        return View("Index", await actions.ListAsync(true));
    }

    [HttpGet] public async Task<IActionResult> Details(Guid id) => await DetailsView(id, false);
    [Authorize(Policy = OfficeSecurity.ManagerPolicy), HttpGet]
    public async Task<IActionResult> ManageDetails(Guid id) => await DetailsView(id, true);

    [HttpGet]
    public async Task<IActionResult> Create()
    {
        if (!await Choices()) return RedirectToAction("Index", "Portal");
        return View(new NewRequestForm());
    }

    [HttpPost]
    public async Task<IActionResult> Create(NewRequestForm form)
    {
        if (!ModelState.IsValid)
        {
            if (!await Choices()) return NotFound();
            Response.StatusCode = 400; return View(form);
        }
        var result = await actions.SubmitAsync(new(form.OperationId, form.MembershipId, form.Title, form.Description, form.Location));
        return Outcome(result, false);
    }

    [Authorize(Policy = OfficeSecurity.ManagerPolicy), HttpGet]
    public async Task<IActionResult> CreateOnBehalf()
    {
        if (!await Choices(true)) return NotFound();
        ViewData["Manager"] = true;
        return View("Create", new NewRequestForm());
    }
    [Authorize(Policy = OfficeSecurity.ManagerPolicy), HttpPost]
    public async Task<IActionResult> CreateOnBehalf(NewRequestForm form)
    {
        if (!ModelState.IsValid)
        {
            if (!await Choices(true)) return NotFound();
            ViewData["Manager"] = true; Response.StatusCode = 400; return View("Create", form);
        }
        return Outcome(await actions.SubmitOnBehalfAsync(new(form.OperationId, form.MembershipId, form.Title, form.Description, form.Location)), true);
    }

    [HttpPost]
    public async Task<IActionResult> Change(ChangeRequestForm form) => ModelState.IsValid
        ? Outcome(await actions.ChangeAsync(form.Command(), false), false) : Error(MaintenanceCode.ValidationFailed);

    [Authorize(Policy = OfficeSecurity.ManagerPolicy), HttpPost]
    public async Task<IActionResult> ManageChange(ChangeRequestForm form) => ModelState.IsValid
        ? Outcome(await actions.ChangeAsync(form.Command(), true), true) : Error(MaintenanceCode.ValidationFailed);

    private async Task<IActionResult> DetailsView(Guid id, bool management)
    {
        var details = await actions.ReadAsync(id, management);
        if (details is null) return NotFound();
        ViewData["Manager"] = management;
        return View("Details", details);
    }

    private async Task<bool> Choices(bool management = false)
    {
        var choices = await actions.MembersAsync(management);
        ViewData["Memberships"] = choices;
        return choices.Count > 0;
    }

    private IActionResult Outcome(MaintenanceResult result, bool management) => result.Code == MaintenanceCode.Success
        ? RedirectToAction(management ? nameof(ManageDetails) : nameof(Details), new { id = result.WorkOrderId }) : Error(result.Code);

    private IActionResult Error(MaintenanceCode code)
    {
        if (code == MaintenanceCode.Unavailable) return NotFound();
        if (code == MaintenanceCode.AuthenticationRequired) return Challenge();
        Response.StatusCode = code == MaintenanceCode.Conflict ? 409 : 400;
        return View("ActionError", code);
    }
}
