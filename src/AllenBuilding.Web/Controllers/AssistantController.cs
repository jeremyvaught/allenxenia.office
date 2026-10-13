using AllenBuilding.Application.Assistant;
using AllenBuilding.Application.Maintenance;
using AllenBuilding.Domain.Maintenance;
using AllenBuilding.Web.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.RateLimiting;

namespace AllenBuilding.Web.Controllers;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AssistantController(AssistantHelp help, AssistantPolicy policy, AssistantBudget budget, MaintenanceActions maintenance,
    IAssistantTenantAccess access, ICompositeViewEngine views, AssistantMaintenance actions, TimeProvider time) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(Guid? membershipId, Guid? requestId)
    {
        ViewData["Enabled"] = policy.Enabled;
        ViewData["ActionsEnabled"] = policy.ActionsEnabled;
        ViewData["Memberships"] = policy.TenantHelpEnabled ? await maintenance.MembersAsync(false, HttpContext.RequestAborted) : Array.Empty<RequestMemberChoice>();
        ViewData["ConversationId"] = Guid.NewGuid();
        if (requestId is { } request)
        {
            if (!policy.ActionsEnabled || membershipId is null) return NotFound();
            var scope = await access.CaptureAsync(membershipId.Value, HttpContext.RequestAborted);
            if (scope is null) return NotFound();
            var target = await maintenance.ReadForMembershipAsync(request, scope.MembershipId, HttpContext.RequestAborted);
            if (target is null) return NotFound();
            ViewData["SelectedMembership"] = scope.MembershipId;
            ViewData["Memberships"] = new[] { new RequestMemberChoice(scope.MembershipId, scope.Suite) };
            return await ProtectedView("Index", new AssistantAnswer(AssistantModelCode.Success, "Describe the comment you want to add to the selected request. Nothing has been submitted.", Scope: scope, ContextualSources: [], Target: target), scope, []);
        }
        return View();
    }

    [HttpGet, EnableRateLimiting("Readiness")]
    public async Task<IActionResult> Access(Guid membershipId) => policy.TenantHelpEnabled &&
        await access.CaptureAsync(membershipId, HttpContext.RequestAborted) is not null ? NoContent() : NotFound();

    [HttpPost, EnableRateLimiting("AssistantTurns"), RequestSizeLimit(20000)]
    public async Task<IActionResult> Ask(string? question, Guid? membershipId, Guid? requestId, Guid conversationId)
    {
        ViewData["Enabled"] = policy.Enabled;
        ViewData["ActionsEnabled"] = policy.ActionsEnabled;
        ViewData["ConversationId"] = Guid.NewGuid();
        if (!policy.Enabled) return Answer(new(AssistantModelCode.Unavailable));
        // Browser form antiforgery is global; reject an explicit cross-origin request as well.
        var origin = Request.Headers.Origin.ToString();
        if (origin.Length > 0 && origin != Request.Scheme + "://" + Request.Host) return BadRequest();
        if (Request.Headers["Sec-Fetch-Site"] == "cross-site") return BadRequest();
        if (!ModelState.IsValid || string.IsNullOrWhiteSpace(question) || question.Length > 4000) return Answer(new(AssistantModelCode.Invalid));
        if (!budget.TryAdmit(HttpContext.Connection.RemoteIpAddress?.ToString() ?? "local"))
        { Response.StatusCode = 429; ViewData["Limited"] = true; return View("Index"); }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var result = await help.AskAsync(question, deadline.Token, membershipId, requestId, conversationId);
            if (result.Scope is null) return Answer(result);
            // Do not add unrelated memberships to a private response's provenance.
            ViewData["Memberships"] = new[] { new RequestMemberChoice(result.Scope.MembershipId, result.Scope.Suite) };
            ViewData["SelectedMembership"] = result.Scope.MembershipId;
            return await ProtectedView("Index", result, result.Scope, result.ContextualSources!);
        }
        catch (OperationCanceledException) when (!HttpContext.RequestAborted.IsCancellationRequested) { return Answer(new(AssistantModelCode.Unavailable)); }
        catch (Exception exception) when (exception is System.Data.Common.DbException or Microsoft.EntityFrameworkCore.DbUpdateException)
        { return Answer(new(AssistantModelCode.Unavailable)); }
    }
    [Authorize, HttpGet, EnableRateLimiting("Readiness")]
    public async Task<IActionResult> Review(Guid id, Guid conversationId)
    {
        if (!policy.ActionsEnabled) return NotFound();
        var view = await actions.ReadAsync(id, conversationId, HttpContext.RequestAborted); if (view is null) return NotFound();
        ViewData["Ready"] = view.Review.IsReady(time.GetUtcNow());
        return await ProtectedView("Review", view, view.Scope, view.Sources);
    }
    [Authorize, HttpPost, RequestSizeLimit(20000), EnableRateLimiting("PaymentActions")]
    public async Task<IActionResult> Confirm(Guid id, Guid conversationId, Guid version, Guid operationId)
    {
        if (!policy.ActionsEnabled) return NotFound(); if (!ModelState.IsValid || !SameOrigin()) return BadRequest();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var result = await actions.ConfirmAsync(new(id, conversationId, version), operationId, deadline.Token);
            return result.Code == MaintenanceCode.Success ? RedirectToAction(nameof(Review), new { id, conversationId }) : ActionError(result.Code);
        }
        catch (Exception exception) when (exception is System.Data.Common.DbException or Microsoft.EntityFrameworkCore.DbUpdateException or OperationCanceledException)
        { if (HttpContext.RequestAborted.IsCancellationRequested) throw; return StatusCode(503); }
    }
    [Authorize, HttpPost, RequestSizeLimit(20000), EnableRateLimiting("PaymentActions")]
    public async Task<IActionResult> Edit(Guid id, Guid conversationId, Guid version, string? title, string? description, string? location, string? comment, bool cancel)
    {
        if (!policy.ActionsEnabled) return NotFound(); if (!ModelState.IsValid || !SameOrigin()) return BadRequest();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var view = await actions.ReadAsync(id, conversationId, deadline.Token); if (view is null) return NotFound();
            var result = await actions.EditAsync(new(id, conversationId, version), new(view.Review.Kind, title ?? "", description ?? "", location ?? "", comment ?? ""), cancel, deadline.Token);
            return result == MaintenanceCode.Success ? RedirectToAction(nameof(Review), new { id, conversationId }) : ActionError(result);
        }
        catch (Exception exception) when (exception is System.Data.Common.DbException or Microsoft.EntityFrameworkCore.DbUpdateException or OperationCanceledException)
        { if (HttpContext.RequestAborted.IsCancellationRequested) throw; return StatusCode(503); }
    }
    private bool SameOrigin() => (Request.Headers.Origin.Count == 0 || Request.Headers.Origin.ToString() == Request.Scheme + "://" + Request.Host) && Request.Headers["Sec-Fetch-Site"] != "cross-site";
    private IActionResult ActionError(MaintenanceCode code)
    {
        Response.StatusCode = code switch { MaintenanceCode.Unavailable or MaintenanceCode.AuthenticationRequired => 404, MaintenanceCode.Conflict => 409, _ => 400 };
        return View("ReviewError");
    }
    private async Task<IActionResult> ProtectedView(string name, object model, AssistantTenantScope scope, IReadOnlyList<AssistantSource> sources)
    {
        ViewData.Model = model; var found = views.FindView(ControllerContext, name, isMainPage: true);
        if (!found.Success) throw new InvalidOperationException("Assistant view is unavailable.");
        using var writer = new StringWriter();
        await found.View.RenderAsync(new ViewContext(ControllerContext, found.View, ViewData, TempData, writer, new HtmlHelperOptions()));
        return new ProtectedAssistantHtml(writer.ToString(), scope, sources, access);
    }
    private IActionResult Answer(AssistantAnswer result)
    {
        Response.StatusCode = result.Code switch { AssistantModelCode.Success => 200, AssistantModelCode.Invalid => 400, AssistantModelCode.Denied => 404, _ => 503 };
        return View("Index", result);
    }
}
