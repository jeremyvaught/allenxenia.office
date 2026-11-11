using AllenBuilding.Application.Access;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Web.Models;
using AllenBuilding.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AllenBuilding.Web.Controllers;

[Authorize(Policy = OfficeSecurity.ManagerPolicy), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ManagementController(
    BuildingAccessActions buildingAccess, AccountInvitations invitations, AccessRevocation revocation, AccountAccess access) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var overview = await buildingAccess.OverviewAsync(HttpContext.RequestAborted);
        return overview is null ? Challenge() : View(overview);
    }

    [Authorize(Policy = OfficeSecurity.RecentManagerPolicy), HttpPost]
    public async Task<IActionResult> AddSuite(SuiteForm form)
    {
        if (!ModelState.IsValid)
            return BadRequest();
        var result = await buildingAccess.AddSuiteAsync(form.BuildingId, form.Name, HttpContext.RequestAborted);
        if (result.Code == SuiteCode.Forbidden)
            return Forbid();
        if (result.Code == SuiteCode.Conflict)
            return Conflict();
        if (result.Code != SuiteCode.Success)
            return BadRequest();
        TempData["Notice"] = $"Suite {result.Name} added. You can now invite a tenant or grant access to this suite.";
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = OfficeSecurity.RecentManagerPolicy), HttpPost]
    public async Task<IActionResult> Revoke(RevokeAccessForm form)
    {
        if (!ModelState.IsValid)
            return BadRequest();
        var result = await revocation.RevokeAsync(form.MembershipId, form.Version, form.Reason, HttpContext.RequestAborted);
        if (result == AllenBuilding.Application.Maintenance.MaintenanceCode.Unavailable)
            return NotFound();
        if (result == AllenBuilding.Application.Maintenance.MaintenanceCode.Conflict)
            return Conflict();
        if (result != AllenBuilding.Application.Maintenance.MaintenanceCode.Success)
            return BadRequest();
        TempData["Notice"] = "Tenant access revoked. Existing records are retained. Billing arrangements are unchanged.";
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = OfficeSecurity.RecentManagerPolicy), HttpPost]
    public async Task<IActionResult> Grant(ExistingAccountForm form)
    {
        if (!ModelState.IsValid)
            return BadRequest();
        var result = await access.GrantAsync(new(form.OperationId, form.SuiteId, form.Email, form.Reason), HttpContext.RequestAborted);
        if (result.Code == AllenBuilding.Application.Access.AccessCode.Unavailable)
            return NotFound();
        if (result.Code == AllenBuilding.Application.Access.AccessCode.Conflict)
            return Conflict();
        if (result.Code != AllenBuilding.Application.Access.AccessCode.Success)
            return BadRequest();
        TempData["Notice"] = "Suite access granted to the existing account. Previous memberships and passwords are unchanged.";
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = OfficeSecurity.RecentManagerPolicy), HttpPost]
    public async Task<IActionResult> ChangeInvitation(InvitationChangeForm form)
    {
        if (!ModelState.IsValid)
            return BadRequest();
        var code = await invitations.ChangeAsync(form.InvitationId, form.Version, form.Revoke, form.Reason, HttpContext.RequestAborted);
        if (code == AllenBuilding.Application.Access.AccessCode.Unavailable)
            return NotFound();
        if (code == AllenBuilding.Application.Access.AccessCode.Conflict)
            return Conflict();
        if (code != AllenBuilding.Application.Access.AccessCode.Success)
            return BadRequest();
        TempData["Notice"] = form.Revoke
            ? "Invitation revoked. Its link cannot activate access."
            : "Invitation reissued for seven days. Previous links are invalid; the new link is queued for delivery.";
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = OfficeSecurity.RecentManagerPolicy), HttpPost]
    public async Task<IActionResult> Invite(InviteForm form)
    {
        if (!ModelState.IsValid)
            return BadRequest();
        var id = await invitations.IssueAsync(form.SuiteId, form.Email, HttpContext.RequestAborted);
        if (id is null)
            return Conflict("Unable to create this invitation. Check the suite and account, then try again.");
        TempData["Notice"] = "Invitation recorded and queued for delivery. Check Notification delivery for its status.";
        return RedirectToAction(nameof(Index));
    }
}
