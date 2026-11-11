using AllenBuilding.Application.Access;
using AllenBuilding.Application.Assistant;
using Microsoft.AspNetCore.Mvc;

namespace AllenBuilding.Web.ViewComponents;

public sealed record MainNav(bool SignedIn, bool Manager, bool AssistantEnabled);

public sealed class MainNavViewComponent(ICurrentActor current, AssistantPolicy assistant) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var signedIn = UserClaimsPrincipal.Identity?.IsAuthenticated == true;
        var manager = false;
        if (signedIn)
        {
            // Error and status pages render this too, so a failed lookup falls back to the plain links.
            try
            {
                var actor = await current.GetAsync(HttpContext.RequestAborted);
                manager = actor is { ChallengedMfa: true } && actor.ManagedBuildings.Count > 0;
            }
            catch (Exception) { manager = false; }
        }
        return View(new MainNav(signedIn, manager, assistant.Enabled));
    }
}
