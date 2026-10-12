using System.ComponentModel.DataAnnotations;
using AllenBuilding.Application.Access;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Notifications;
using AllenBuilding.Infrastructure.Persistence;
using AllenBuilding.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Web.Controllers;

[Authorize(Policy = OfficeSecurity.ManagerPolicy), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class NotificationsController(ICurrentActor current, OfficeDbContext db, NotificationQueue queue) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var actor = (await current.GetAsync())!;
        var rows = await db.AccountMessages.AsNoTracking().Where(x => x.UserId == actor.UserId ||
            (x.BuildingId != null && actor.ManagedBuildings.Contains(x.BuildingId.Value))).OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Take(100).Select(x => new NotificationRow(x.Id, x.LeaseToken, x.Kind, x.State, x.Attempts, x.LastError, x.CreatedAt)).ToListAsync();
        return View(rows);
    }

    [Authorize(Policy = OfficeSecurity.RecentManagerPolicy), HttpPost]
    public async Task<IActionResult> Retry(RetryNotificationForm form)
    {
        if (!ModelState.IsValid) return BadRequest();
        if (!await queue.RetryAsync(current, form.Id, form.Version, form.Reason)) return Conflict();
        return RedirectToAction(nameof(Index));
    }
}

public sealed record NotificationRow(Guid Id, Guid Version, string Kind, MessageState State, int Attempts, string? Error, DateTimeOffset CreatedAt);
public sealed class RetryNotificationForm
{
    public Guid Id { get; set; }
    public Guid Version { get; set; }
    [Required, StringLength(2000)] public string Reason { get; set; } = "";
}
