using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace AllenBuilding.Web.Controllers;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("Accounts")]
public sealed class InvitationController(AccountInvitations invitations) : Controller
{
    [HttpGet] public IActionResult Accept(Guid id) => View(new ActivationForm { InvitationId = id });
    [HttpPost]
    public async Task<IActionResult> Accept(ActivationForm form)
    {
        if (ModelState.IsValid && await invitations.AcceptAsync(form.InvitationId, form.Token, form.Password))
            return View("Accepted");
        ModelState.AddModelError("", "Unable to activate this invitation. Check the link and password, or ask your manager for help.");
        return View(form);
    }
    [HttpGet] public IActionResult Forgot() => View(new ResetRequestForm());
    [HttpPost]
    public async Task<IActionResult> Forgot(ResetRequestForm form)
    {
        if (!ModelState.IsValid) return View(form);
        await invitations.QueueResetAsync(form.Email);
        return View("ResetRequested");
    }
    [HttpGet] public IActionResult Reset(string id) => View(new ResetForm { UserId = id });
    [HttpPost]
    public async Task<IActionResult> Reset(ResetForm form)
    {
        if (ModelState.IsValid && await invitations.ResetAsync(form.UserId, form.Token, form.Password)) return View("Accepted");
        ModelState.AddModelError("", "Unable to reset the password. Check the link and password, or request a new link.");
        return View(form);
    }
}
