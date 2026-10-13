using AllenBuilding.Application.Assistant;
using AllenBuilding.Web.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AllenBuilding.Web.Controllers;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AssistantController(AssistantHelp help, AssistantPolicy policy, AssistantBudget budget) : Controller
{
    [HttpGet]
    public IActionResult Index() { ViewData["Enabled"] = policy.Enabled; return View(); }

    [HttpPost, EnableRateLimiting("AssistantTurns"), RequestSizeLimit(20000)]
    public async Task<IActionResult> Ask(string? question)
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
        try { return Answer(await help.AskAsync(question, deadline.Token)); }
        catch (OperationCanceledException) when (!HttpContext.RequestAborted.IsCancellationRequested) { return Answer(new(AssistantModelCode.Unavailable)); }
        catch (Exception exception) when (exception is System.Data.Common.DbException or Microsoft.EntityFrameworkCore.DbUpdateException)
        { return Answer(new(AssistantModelCode.Unavailable)); }
    }
    private IActionResult Answer(AssistantAnswer result)
    {
        Response.StatusCode = result.Code switch { AssistantModelCode.Success => 200, AssistantModelCode.Invalid => 400, _ => 503 };
        return View("Index", result);
    }
}
