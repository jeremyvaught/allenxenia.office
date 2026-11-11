using AllenBuilding.Application.Access;
using AllenBuilding.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace AllenBuilding.Web.Controllers;

[Authorize, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PortalController(
    ICurrentActor current, BuildingAccessActions buildingAccess, UserManager<OfficeUser> users, IConfiguration configuration) : Controller
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
        var memberships = await buildingAccess.ActiveMembershipsAsync(actor, HttpContext.RequestAborted);
        ViewData["BillingEnabled"] = configuration.GetValue<bool>("Billing:PlanningEnabled");
        ViewData["Manager"] = actor.ChallengedMfa && actor.ManagedBuildings.Count > 0;
        return View(memberships);
    }
}
