using System.ComponentModel.DataAnnotations;
using AllenBuilding.Application.Access;
using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Web.Models;
using AllenBuilding.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AllenBuilding.Web.Controllers;

[Authorize(Policy = OfficeSecurity.ManagerPolicy), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class FinancialExceptionsController(FinancialExceptionActions actions, RefundDispositionActions refunds, RefundDispositionPolicy refundPolicy,
    ICurrentActor current, BillingReviewTickets tickets, TimeProvider time) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(bool includeResolved = false)
    {
        ViewData["IncludeResolved"] = includeResolved;
        ViewData["RefundsEnabled"] = refundPolicy.Enabled;
        try { return View(await actions.ReadAsync(includeResolved, HttpContext.RequestAborted)); }
        catch (OperationCanceledException) { return StatusCode(StatusCodes.Status503ServiceUnavailable, "Financial cases could not be loaded. Retry the queue."); }
        catch (System.Data.Common.DbException) { return StatusCode(StatusCodes.Status503ServiceUnavailable, "Financial cases could not be loaded. Retry the queue."); }
    }
    [Authorize(Policy = OfficeSecurity.RecentManagerPolicy), HttpGet]
    public async Task<IActionResult> Refund(Guid id)
    {
        var review = await refunds.ReadAsync(id, HttpContext.RequestAborted);
        return review is null ? NotFound() : View(review);
    }
    [Authorize(Policy = OfficeSecurity.RecentManagerPolicy), HttpPost, RequestSizeLimit(131072)]
    public async Task<IActionResult> PrepareRefund(RefundDispositionForm form)
    {
        if (!refundPolicy.Enabled) return NotFound();
        if (!ModelState.IsValid || form.OperationId == Guid.Empty || form.Disposition is null || !Enum.IsDefined(form.Disposition.Value) || form.Items is not { Count: >= 1 and <= 25 } || form.Items.Any(x => x is null) ||
            form.Items.Select(x => x.ObligationId).Distinct().Count() != form.Items.Count) return BadRequest();
        var actor = await current.GetAsync(HttpContext.RequestAborted); if (actor is not { ChallengedMfa: true, RecentMfa: true }) return Forbid();
        var snapshot = await refunds.ReadAsync(form.MovementId, HttpContext.RequestAborted); if (snapshot is null) return NotFound();
        if (snapshot.ReviewVersion != form.ReviewVersion) return RefundConflict();
        var selected = new List<ReviewedRefundItem>(); var lines = new List<BillingReviewLine>
        {
            new("Verified refund", BillingDisplay.Usd(snapshot.AmountMinorUnits) + " USD"), new("Provider source", snapshot.SourceId),
            new("Debt disposition", form.Disposition == RefundDisposition.CreditDebt ? "Reduce debt by the returned amount" : "Retain the debt represented by returned money")
        };
        foreach (var item in form.Items)
        {
            var original = snapshot.Items.SingleOrDefault(x => x.ObligationId == item.ObligationId);
            if (original is null) return NotFound(); if (original.Version != item.Version) return RefundConflict();
            if (item.Amount == 0) continue;
            long amount; try { amount = BillingDisplay.MinorUnits(item.Amount!.Value, 1); } catch (ArgumentException) { return BadRequest(); }
            if (amount > original.AvailableCapturedMinorUnits || (form.Disposition == RefundDisposition.CreditDebt && amount > original.UncreditedChargeMinorUnits)) return BadRequest();
            selected.Add(new(original.ObligationId, original.Version, amount));
            lines.Add(new(original.Description, BillingDisplay.Usd(amount) + " USD returned from obligation " + original.ObligationId.ToString("D")));
        }
        if (selected.Count == 0 || selected.Sum(x => x.AmountMinorUnits) != snapshot.AmountMinorUnits) return BadRequest();
        var fresh = await current.GetAsync(HttpContext.RequestAborted);
        if (fresh is not { ChallengedMfa: true, RecentMfa: true } || fresh.UserId != actor.UserId || !fresh.ManagedBuildings.Contains(snapshot.BuildingId)) return Forbid();
        var command = new ApplyRefundDisposition(form.OperationId, snapshot.AccountId, snapshot.MovementId, snapshot.ReviewVersion, form.Disposition.Value, selected, form.Reason.Trim());
        return View("RefundReview", tickets.Create(new(actor.UserId, time.GetUtcNow(), snapshot.Suite + " · account " + snapshot.AccountId.ToString("D"), "Review refund disposition", lines, Refund: command)));
    }
    [Authorize(Policy = OfficeSecurity.RecentManagerPolicy), HttpPost, RequestSizeLimit(131072)]
    public async Task<IActionResult> ConfirmRefund(string? ticket)
    {
        if (!refundPolicy.Enabled) return NotFound();
        var actor = await current.GetAsync(HttpContext.RequestAborted); if (actor is not { ChallengedMfa: true, RecentMfa: true }) return Forbid();
        var review = tickets.Read(ticket, actor.UserId); if (review?.Refund is not { } command) return BadRequest();
        var result = await refunds.ApplyAsync(command, HttpContext.RequestAborted);
        if (result.Code == BillingCode.Unavailable) return NotFound(); if (result.Code == BillingCode.Conflict) return RefundConflict();
        if (result.Code != BillingCode.Success) return BadRequest();
        TempData["Notice"] = "Refund allocation and reviewed debt disposition recorded. Collection stays on hold pending provider agreement.";
        return RedirectToAction(nameof(Index));
    }
    private IActionResult RefundConflict() => StatusCode(StatusCodes.Status409Conflict, "This refund or obligation changed. Reload and review it again.");
    [Authorize(Policy = OfficeSecurity.RecentManagerPolicy), HttpPost]
    public Task<IActionResult> Assign(FinancialCaseForm form) => Change(form, FinancialCaseAction.Assign);
    [Authorize(Policy = OfficeSecurity.RecentManagerPolicy), HttpPost]
    public Task<IActionResult> Escalate(FinancialCaseForm form) => Change(form, FinancialCaseAction.Escalate);
    [Authorize(Policy = OfficeSecurity.RecentManagerPolicy), HttpPost]
    public Task<IActionResult> Reconcile(FinancialCaseForm form) => Change(form, FinancialCaseAction.Reconcile);
    private async Task<IActionResult> Change(FinancialCaseForm form, FinancialCaseAction action)
    {
        if (!ModelState.IsValid) return BadRequest();
        var result = await actions.ChangeAsync(new(form.OperationId, form.Id, form.Version, action, form.Reason, form.OwnerUserId), HttpContext.RequestAborted);
        return result.Code switch
        {
            BillingCode.Success => RedirectToAction(nameof(Index)),
            BillingCode.Unavailable => NotFound(),
            BillingCode.Conflict => StatusCode(StatusCodes.Status409Conflict, "This case changed. Reload the queue before continuing."),
            _ => BadRequest()
        };
    }
}
public sealed class FinancialCaseForm
{
    public Guid Id { get; set; }
    public Guid Version { get; set; }
    public Guid OperationId { get; set; }
    [Required, StringLength(2000)] public string Reason { get; set; } = "";
    [StringLength(450)] public string? OwnerUserId { get; set; }
}
