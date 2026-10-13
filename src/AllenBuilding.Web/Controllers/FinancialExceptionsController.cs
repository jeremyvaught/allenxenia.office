using System.ComponentModel.DataAnnotations;
using AllenBuilding.Application.Billing;
using AllenBuilding.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AllenBuilding.Web.Controllers;

[Authorize(Policy = OfficeSecurity.ManagerPolicy), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class FinancialExceptionsController(FinancialExceptionActions actions) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(bool includeResolved = false)
    {
        ViewData["IncludeResolved"] = includeResolved;
        try { return View(await actions.ReadAsync(includeResolved, HttpContext.RequestAborted)); }
        catch (OperationCanceledException) { return StatusCode(StatusCodes.Status503ServiceUnavailable, "Financial cases could not be loaded. Retry the queue."); }
        catch (System.Data.Common.DbException) { return StatusCode(StatusCodes.Status503ServiceUnavailable, "Financial cases could not be loaded. Retry the queue."); }
    }
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
