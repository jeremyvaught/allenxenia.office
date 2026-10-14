using System.ComponentModel.DataAnnotations;
using AllenBuilding.Application.Access;
using AllenBuilding.Infrastructure.Notifications;
using AllenBuilding.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AllenBuilding.Web.Controllers;

[Authorize(Policy = OfficeSecurity.ManagerPolicy), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class NotificationsController(ICurrentActor current, NotificationInbox inbox, NotificationQueue queue) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(int reviewPage = 1)
    {
        if (reviewPage is < 1 or > 1000000) return BadRequest();
        var actor = (await current.GetAsync())!;
        return View(await inbox.ReadAsync(actor, reviewPage, HttpContext.RequestAborted));
    }

    [Authorize(Policy = OfficeSecurity.RecentManagerPolicy), HttpPost]
    public async Task<IActionResult> Retry(RetryNotificationForm form)
    {
        if (!ModelState.IsValid) return BadRequest();
        if (!await queue.RetryAsync(current, form.Id, form.Version, form.Reason)) return Conflict();
        return RedirectToAction(nameof(Index));
    }
}

public sealed class RetryNotificationForm
{
    public Guid Id { get; set; }
    public Guid Version { get; set; }
    [Required, StringLength(2000)] public string Reason { get; set; } = "";
}
