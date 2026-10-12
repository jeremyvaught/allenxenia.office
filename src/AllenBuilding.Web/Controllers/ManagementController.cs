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
public sealed class ManagementController(ICurrentActor current, OfficeDbContext db, AccountInvitations invitations, AccessRevocation revocation) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var actor = (await current.GetAsync())!;
        ViewData["Buildings"] = await db.Buildings.AsNoTracking().Where(x => actor.ManagedBuildings.Contains(x.Id)).ToListAsync();
        ViewData["Access"] = await (from membership in db.Memberships.AsNoTracking()
                                    join suite in db.Suites on membership.SuiteId equals suite.Id
                                    join user in db.Users on membership.UserId equals user.Id
                                    where actor.ManagedBuildings.Contains(suite.BuildingId) && membership.RevokedAt == null
                                    select new AccessRow(membership.Id, membership.Version, suite.Name, user.Email!)).ToListAsync();
        return View(await db.Suites.AsNoTracking().Where(x => actor.ManagedBuildings.Contains(x.BuildingId)).OrderBy(x => x.Name).ToListAsync());
    }

    [Authorize(Policy = OfficeSecurity.RecentManagerPolicy), HttpPost]
    public async Task<IActionResult> AddSuite(SuiteForm form)
    {
        var initial = (await current.GetAsync())!;
        await using var transaction = await AuthorityLocks.BeginAsync(db);
        await AuthorityLocks.UserAsync(db, initial.UserId);
        await AuthorityLocks.ManagerAsync(db, initial.UserId, form.BuildingId);
        var actor = await current.GetAsync();
        if (actor is not { RecentMfa: true }) return Forbid();
        if (!ModelState.IsValid || !actor.ManagedBuildings.Contains(form.BuildingId) || string.IsNullOrWhiteSpace(form.Name)) return BadRequest();
        if (await db.Suites.AnyAsync(x => x.BuildingId == form.BuildingId && x.Name == form.Name.Trim())) return Conflict();
        var suite = new Suite { BuildingId = form.BuildingId, Name = form.Name.Trim() };
        db.Suites.Add(suite); invitations.Audit(actor.UserId, "SuiteCreated", suite.Id.ToString());
        await db.SaveChangesAsync(); await transaction.CommitAsync(); return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = OfficeSecurity.RecentManagerPolicy), HttpPost]
    public async Task<IActionResult> Revoke(RevokeAccessForm form)
    {
        if (!ModelState.IsValid) return BadRequest();
        var result = await revocation.RevokeAsync(form.MembershipId, form.Version, form.Reason);
        if (result == AllenBuilding.Application.Maintenance.MaintenanceCode.Unavailable) return NotFound();
        if (result == AllenBuilding.Application.Maintenance.MaintenanceCode.Conflict) return Conflict();
        if (result != AllenBuilding.Application.Maintenance.MaintenanceCode.Success) return BadRequest();
        TempData["Notice"] = "Tenant access revoked. Existing records are retained. Billing arrangements are unchanged.";
        return RedirectToAction(nameof(Index));
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
