using AllenBuilding.Application.Access;
using AllenBuilding.Application.Maintenance;
using AllenBuilding.Domain.Maintenance;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Notifications;
using AllenBuilding.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace AllenBuilding.Web.Controllers;

[Authorize, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PortalController(
    ICurrentActor current, BuildingAccessActions buildingAccess, MaintenanceActions maintenance, NotificationInbox notifications,
    UserManager<OfficeUser> users, IConfiguration configuration) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var actor = await current.GetAsync(HttpContext.RequestAborted);
        if (actor is null)
            return Challenge();
        var user = (await users.FindByIdAsync(actor.UserId))!;
        if (actor.ManagedBuildings.Count > 0)
        {
            if (!user.TwoFactorEnabled)
            {
                // Recovery cannot be mistaken for a new-account welcome.
                if (user.MustReplaceRecoveryCodes)
                    return RedirectToAction("Enroll", "Account");
                ViewData["SecuritySetupAvailable"] = true;
            }
            if (User.HasClaim("amr", "mfa") && user.MustReplaceRecoveryCodes)
                return RedirectToAction("RecoveryCodes", "Account");
            if (user.TwoFactorEnabled && !actor.ChallengedMfa)
                return RedirectToAction("Login", "Account");
        }
        var aborted = HttpContext.RequestAborted;
        var memberships = await buildingAccess.ActiveMembershipsAsync(actor, aborted);
        var manager = actor.ChallengedMfa && actor.ManagedBuildings.Count > 0;
        ManagerDashboard? managing = null;
        if (manager && await maintenance.QueueAsync(new(Sort: QueueSort.Newest), aborted) is { } queue)
        {
            var delivery = await notifications.ReadAsync(actor, 1, aborted, describe: false);
            managing = new(await buildingAccess.ManagerSummaryAsync(actor, aborted), delivery.NeedsAttention.Count, delivery.HasNextPage,
                queue.Items.Take(5).ToArray(), queue.ObservedAt);
        }
        IReadOnlyList<RequestSummary> open = memberships.Count == 0 ? [] : (await maintenance.ListAsync(false, aborted))
            .Where(x => x.Status is not (WorkOrderStatus.Closed or WorkOrderStatus.Cancelled)).Take(5).ToArray();
        return View(new PortalDashboard(memberships.Count > 0, open, managing,
            ViewData["SecuritySetupAvailable"] is true, configuration.GetValue<bool>("Billing:PlanningEnabled")));
    }
}
