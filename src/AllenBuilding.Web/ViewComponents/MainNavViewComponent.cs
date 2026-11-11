using AllenBuilding.Application.Access;
using AllenBuilding.Application.Assistant;
using Microsoft.AspNetCore.Mvc;

namespace AllenBuilding.Web.ViewComponents;

public sealed record MainNav(bool SignedIn, bool Manager, bool Tenant, bool AssistantEnabled);

public sealed class MainNavViewComponent(
    ICurrentActor current, BuildingAccessActions buildingAccess, AssistantPolicy assistant, ILogger<MainNavViewComponent> logger) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var signedIn = UserClaimsPrincipal.Identity?.IsAuthenticated == true;
        var manager = false;
        var tenant = false;
        if (signedIn)
        {
            // Error and status pages render this too, so a failed lookup falls back to the plain links.
            try
            {
                var actor = await current.GetAsync(HttpContext.RequestAborted);
                manager = actor is { ChallengedMfa: true } && actor.ManagedBuildings.Count > 0;
                tenant = actor is not null && (await buildingAccess.ActiveMembershipsAsync(actor, HttpContext.RequestAborted)).Count > 0;
            }
            catch (Exception exception)
            {
                // Type name only, as elsewhere: messages can carry connection details.
                logger.LogWarning("Navigation could not read the signed-in role; showing the basic links. Failure type: {FailureType}",
                    exception.GetType().FullName);
                manager = false;
                tenant = false;
            }
        }
        return View(new MainNav(signedIn, manager, tenant, assistant.Enabled));
    }
}
