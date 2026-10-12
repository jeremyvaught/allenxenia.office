using AllenBuilding.Application.Access;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using AllenBuilding.Web.Models;
using AllenBuilding.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Web.Controllers;

[Authorize(Policy = OfficeSecurity.ManagerPolicy), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ManagementController(ICurrentActor current, OfficeDbContext db, AccountInvitations invitations) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var actor = (await current.GetAsync())!;
        ViewData["Buildings"] = await db.Buildings.AsNoTracking().Where(x => actor.ManagedBuildings.Contains(x.Id)).ToListAsync();
        return View(await db.Suites.AsNoTracking().Where(x => actor.ManagedBuildings.Contains(x.BuildingId)).OrderBy(x => x.Name).ToListAsync());
    }

    [Authorize(Policy = OfficeSecurity.RecentManagerPolicy), HttpPost]
    public async Task<IActionResult> AddSuite(SuiteForm form)
    {
        var actor = (await current.GetAsync())!;
        if (!ModelState.IsValid || !actor.ManagedBuildings.Contains(form.BuildingId) || string.IsNullOrWhiteSpace(form.Name)) return BadRequest();
        if (await db.Suites.AnyAsync(x => x.BuildingId == form.BuildingId && x.Name == form.Name.Trim())) return Conflict();
        var suite = new Suite { BuildingId = form.BuildingId, Name = form.Name.Trim() };
        db.Suites.Add(suite); invitations.Audit(actor.UserId, "SuiteCreated", suite.Id.ToString());
        await db.SaveChangesAsync(); return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = OfficeSecurity.RecentManagerPolicy), HttpPost]
    public async Task<IActionResult> Invite(InviteForm form)
    {
        if (!ModelState.IsValid) return BadRequest();
        var id = await invitations.IssueAsync(form.SuiteId, form.Email);
        if (id is null) return Conflict("Unable to create this invitation. Check the suite and account, then try again.");
        TempData["Notice"] = "Invitation recorded. Email is queued; delivery must be configured before tenants can receive it.";
        return RedirectToAction(nameof(Index));
    }
}
