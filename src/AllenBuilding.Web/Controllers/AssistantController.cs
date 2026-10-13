using AllenBuilding.Application.Assistant;
using AllenBuilding.Application.Maintenance;
using AllenBuilding.Web.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.RateLimiting;

namespace AllenBuilding.Web.Controllers;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AssistantController(AssistantHelp help, AssistantPolicy policy, AssistantBudget budget, MaintenanceActions maintenance,
    IAssistantTenantAccess access, ICompositeViewEngine views) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        ViewData["Enabled"] = policy.Enabled;
        ViewData["Memberships"] = policy.TenantHelpEnabled ? await maintenance.MembersAsync(false, HttpContext.RequestAborted) : Array.Empty<RequestMemberChoice>();
        return View();
    }

    [HttpGet, EnableRateLimiting("Readiness")]
    public async Task<IActionResult> Access(Guid membershipId) => policy.TenantHelpEnabled &&
        await access.CaptureAsync(membershipId, HttpContext.RequestAborted) is not null ? NoContent() : NotFound();

    [HttpPost, EnableRateLimiting("AssistantTurns"), RequestSizeLimit(20000)]
    public async Task<IActionResult> Ask(string? question, Guid? membershipId)
    {
        ViewData["Enabled"] = policy.Enabled;
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
            var result = await help.AskAsync(question, deadline.Token, membershipId);
            if (result.Scope is null) return Answer(result);
            ViewData.Model = result;
            // Do not add unrelated memberships to a private response's provenance.
            ViewData["Memberships"] = new[] { new RequestMemberChoice(result.Scope.MembershipId, result.Scope.Suite) };
            ViewData["SelectedMembership"] = result.Scope.MembershipId;
            var found = views.FindView(ControllerContext, "Index", isMainPage: true);
            if (!found.Success) throw new InvalidOperationException("Assistant view is unavailable.");
            using var writer = new StringWriter();
            var viewContext = new ViewContext(ControllerContext, found.View, ViewData, TempData, writer, new HtmlHelperOptions());
            await found.View.RenderAsync(viewContext);
            return new ProtectedAssistantHtml(writer.ToString(), result.Scope, result.ContextualSources!, access);
        }
        catch (OperationCanceledException) when (!HttpContext.RequestAborted.IsCancellationRequested) { return Answer(new(AssistantModelCode.Unavailable)); }
        catch (Exception exception) when (exception is System.Data.Common.DbException or Microsoft.EntityFrameworkCore.DbUpdateException)
        { return Answer(new(AssistantModelCode.Unavailable)); }
    }
    private IActionResult Answer(AssistantAnswer result)
    {
        Response.StatusCode = result.Code switch { AssistantModelCode.Success => 200, AssistantModelCode.Invalid => 400, AssistantModelCode.Denied => 404, _ => 503 };
        return View("Index", result);
    }
}
