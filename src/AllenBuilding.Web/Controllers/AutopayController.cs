using AllenBuilding.Application.Access;
using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AllenBuilding.Web.Controllers;

[Authorize, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None), EnableRateLimiting("PaymentActions")]
public sealed class AutopayController(AutopayPlanning actions, BillingActions billing, AutopayPolicy policy, ICurrentActor current, AutopayReviewTickets tickets, TimeProvider time) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(Guid accountId)
    {
        if (!policy.Enabled) return NotFound();
        var account = (await billing.ReadAsync(false, accountId: accountId)).SingleOrDefault(); if (account is null || account.EndedAt is not null) return NotFound();
        ViewData["Account"] = account;
        return View(await actions.ReadAsync(accountId));
    }
    [HttpPost]
    public async Task<IActionResult> Prepare(Guid accountId, Guid agreementId)
    {
        if (!policy.Enabled) return NotFound();
        if (!ModelState.IsValid || accountId == Guid.Empty || agreementId == Guid.Empty) return Failure(BillingCode.ValidationFailed);
        var review = await actions.ReviewAsync(accountId, agreementId); if (review is null) return Failure(BillingCode.Unavailable);
        var actor = await current.GetAsync(); if (actor is null) return NotFound();
        return View("Review", tickets.Create(new(actor.UserId, time.GetUtcNow(), review.Suite,
            new(review.OperationId, review.AccountId, review.AccountVersion, review.Schedule, false))));
    }
    [HttpPost]
    public async Task<IActionResult> PrepareStop(Guid id, AutopayStopTiming timing)
    {
        if (!policy.Enabled) return NotFound();
        if (!ModelState.IsValid || id == Guid.Empty || !Enum.IsDefined(timing)) return Failure(BillingCode.ValidationFailed);
        var original = (await actions.ReadAsync()).SingleOrDefault(x => x.Id == id); if (original is null) return NotFound();
        if (original.State is AutopayState.Stopped or AutopayState.StopPending) return Failure(BillingCode.Conflict);
        var actor = await current.GetAsync(); if (actor is null) return NotFound();
        return View("Review", tickets.Create(new(actor.UserId, time.GetUtcNow(), original.Suite, Stop: new(Guid.NewGuid(), original.Id, original.Version, timing),
            Stopping: original, StopEffectiveAt: AutopaySchedule.StopAt(timing, time.GetUtcNow()))));
    }
    [HttpPost]
    public async Task<IActionResult> Confirm(string? ticket, bool accepted)
    {
        if (!policy.Enabled) return NotFound();
        var actor = await current.GetAsync(); if (actor is null) return NotFound();
        var review = tickets.Read(ticket, actor.UserId); if (review is null || !ModelState.IsValid || !accepted) return Failure(BillingCode.ValidationFailed);
        if (review.Stop is { Timing: AutopayStopTiming.EndOfCurrentRentMonth } stop && AutopaySchedule.StopAt(stop.Timing, time.GetUtcNow()) != review.StopEffectiveAt) return Failure(BillingCode.Conflict);
        // The separate browser checkbox is required for both financial instructions. Form amounts/dates/IDs are ignored.
        var result = review.Enrollment is { } enrollment ? await actions.ConfirmAsync(enrollment with { ExplicitlyAccepted = true }) : await actions.StopAsync(review.Stop!);
        if (result.Code != BillingCode.Success) return Failure(result.Code);
        var accountId = review.Enrollment?.AccountId ?? review.Stopping!.AccountId;
        TempData["Notice"] = review.Enrollment is not null ? "Consent recorded. Payment-method setup and provider verification are still pending." : "Stop requested. Provider cancellation and existing payment instructions still require verification.";
        return RedirectToAction(nameof(Index), new { accountId });
    }
    private IActionResult Failure(BillingCode code)
    {
        if (code == BillingCode.Unavailable) return NotFound();
        Response.StatusCode = code == BillingCode.Conflict ? 409 : 400;
        return View("Error", code);
    }
}
