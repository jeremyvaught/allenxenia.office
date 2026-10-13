using System.Security.Claims;
using System.Security.Cryptography;
using AllenBuilding.Application.Assistant;
using AllenBuilding.Application.Maintenance;
using AllenBuilding.Domain.Maintenance;
using AllenBuilding.Web.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.RateLimiting;

namespace AllenBuilding.Web.Controllers;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AssistantController(AssistantHelp help, AssistantPolicy policy, AssistantBudget budget, MaintenanceActions maintenance,
    IAssistantTenantAccess access, ICompositeViewEngine views, AssistantMaintenance actions, TimeProvider time,
    AssistantConversations conversations, IDataProtectionProvider protection) : Controller
{
    public const string GuestHintCookie = "office.assistant-draft";
    private bool GuestEnabled => policy.Enabled && policy.GuestDraftsEnabled && policy.FollowUpsEnabled && policy.ActionsEnabled && policy.TenantHelpEnabled;

    [HttpGet]
    public async Task<IActionResult> Index(Guid? membershipId, Guid? requestId)
    {
        ViewData["Enabled"] = policy.Enabled;
        ViewData["ActionsEnabled"] = policy.ActionsEnabled;
        ViewData["Memberships"] = policy.TenantHelpEnabled ? await maintenance.MembersAsync(false, HttpContext.RequestAborted) : Array.Empty<RequestMemberChoice>();
        ViewData["ConversationId"] = Guid.NewGuid();
        ViewData["FollowUpsEnabled"] = policy.FollowUpsEnabled;
        if (policy.FollowUpsEnabled) ConversationOwner();
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

    [HttpGet, EnableRateLimiting("AssistantPages")]
    public async Task<IActionResult> Access(Guid membershipId, Guid? conversationId, Guid? reviewId)
    {
        if (!policy.TenantHelpEnabled) return NotFound();
        if (reviewId is { } review && conversationId is { } reviewConversation)
        {
            var view = policy.ActionsEnabled ? await actions.ReadAsync(review, reviewConversation, HttpContext.RequestAborted) : null;
            return view is not null && view.Scope.MembershipId == membershipId &&
                await access.ReleaseAsync(view.Scope, view.Sources, _ => Task.CompletedTask, HttpContext.RequestAborted) ? NoContent() : NotFound();
        }
        if (conversationId is { } conversation)
        {
            var context = policy.FollowUpsEnabled ? conversations.ReadContext(conversation, ConversationOwner()) : null;
            return context?.Scope is { } scope && scope.MembershipId == membershipId &&
                await access.ReleaseAsync(scope, context.Sources, _ => Task.CompletedTask, HttpContext.RequestAborted) ? NoContent() : NotFound();
        }
        return await access.CaptureAsync(membershipId, HttpContext.RequestAborted) is not null ? NoContent() : NotFound();
    }

    [HttpPost, EnableRateLimiting("AssistantTurns"), RequestSizeLimit(20000)]
    public async Task<IActionResult> Ask(string? question, Guid? membershipId, Guid? requestId, Guid conversationId, int sequence = 0)
    {
        ViewData["Enabled"] = policy.Enabled;
        ViewData["ActionsEnabled"] = policy.ActionsEnabled;
        ViewData["ConversationId"] = Guid.NewGuid();
        ViewData["FollowUpsEnabled"] = policy.FollowUpsEnabled;
        if (!policy.Enabled) return Answer(new(AssistantModelCode.Unavailable));
        // Browser form antiforgery is global; reject an explicit cross-origin request as well.
        var origin = Request.Headers.Origin.ToString();
        if (origin.Length > 0 && origin != Request.Scheme + "://" + Request.Host) return BadRequest();
        if (Request.Headers["Sec-Fetch-Site"] == "cross-site") return BadRequest();
        if (!ModelState.IsValid || string.IsNullOrWhiteSpace(question) || question.Length > 4000) return Answer(new(AssistantModelCode.Invalid));
        if (!budget.TryAdmit(AssistantRequestIdentity.Partition(HttpContext)))
        { Response.StatusCode = 429; ViewData["Limited"] = true; return View("Index"); }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        using var conversation = policy.FollowUpsEnabled ? conversations.Begin(conversationId, sequence, ConversationOwner(), membershipId, requestId) : null;
        if (policy.FollowUpsEnabled && conversation is null)
        { ViewData["ConversationUnavailable"] = true; Response.StatusCode = 409; return View("Index"); }
        try
        {
            var result = await help.AskAsync(question, deadline.Token, membershipId, requestId, conversation?.Id ?? Guid.NewGuid(), conversation?.Previous, guest: User.Identity?.IsAuthenticated != true);
            if (conversation is not null)
            {
                if (result.Code == AssistantModelCode.Success)
                {
                    if (!conversation.Complete(question, result)) return Answer(new(AssistantModelCode.Unavailable));
                    ViewData["ConversationId"] = conversation.Id; ViewData["Sequence"] = conversation.Sequence;
                    ViewData["ConversationClosed"] = conversation.Closed;
                    ViewData["ActiveConversation"] = conversation.Id;
                    if (result.GuestDraft is not null)
                    {
                        ViewData["GuestDraftId"] = conversation.Id;
                        // Only a navigation hint. The protected browser cookie, server entry and
                        // current authorization remain mandatory; this ID grants no access.
                        Response.Cookies.Append(GuestHintCookie, conversation.Id.ToString("D"), new CookieOptions
                        { HttpOnly = true, Secure = Request.IsHttps, SameSite = SameSiteMode.Strict, IsEssential = true, Path = "/", MaxAge = TimeSpan.FromMinutes(10) });
                    }
                }
                else if (result.Code == AssistantModelCode.Denied) conversation.Forget();
                // Failure pages intentionally show no retained private text. Start fresh or retry the
                // original form; an unsuccessful turn does not advance the server's sequence.
            }
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
    private string ConversationOwner() => BrowserOwner() + ":" + (User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "guest");
    private string BrowserOwner()
    {
        if (HttpContext.Items["AssistantBrowser"] is string cached) return cached;
        const string cookie = "office.assistant";
        var protector = protection.CreateProtector("OfficeAssistant.TransientBrowser.v1");
        string? token = null;
        if (Request.Cookies.TryGetValue(cookie, out var protectedToken))
        {
            try { var value = protector.Unprotect(protectedToken); if (Guid.TryParseExact(value, "N", out _)) token = value; }
            catch (CryptographicException) { }
        }
        if (token is null)
        {
            token = Guid.NewGuid().ToString("N");
            Response.Cookies.Append(cookie, protector.Protect(token), new CookieOptions
            { HttpOnly = true, Secure = Request.IsHttps, SameSite = SameSiteMode.Strict, IsEssential = true, Path = "/Assistant", MaxAge = TimeSpan.FromHours(2) });
        }
        HttpContext.Items["AssistantBrowser"] = token; return token;
    }
    [HttpGet, EnableRateLimiting("AssistantPages")]
    public async Task<IActionResult> Guest(Guid id)
    {
        if (!GuestEnabled) return NotFound();
        var draft = conversations.ReadGuest(id, BrowserOwner());
        if (draft is null || !await help.PublicSourcesCurrentAsync(draft.Sources, HttpContext.RequestAborted))
        { Response.Cookies.Delete(GuestHintCookie); return NotFound(); }
        if (draft.ImportedScope is { } original)
        {
            var current = await access.CaptureAsync(original.MembershipId, HttpContext.RequestAborted);
            if (current != original) return NotFound();
            if (draft.ReviewId is { } review) return RedirectToAction(nameof(Review), new { id = review, conversationId = draft.ReviewConversationId });
            ViewData["Memberships"] = new[] { new RequestMemberChoice(original.MembershipId, original.Suite) };
        }
        else ViewData["Memberships"] = User.Identity?.IsAuthenticated == true ? await maintenance.MembersAsync(false, HttpContext.RequestAborted) : Array.Empty<RequestMemberChoice>();
        return draft.ImportedScope is { } bound ? await ProtectedView("Guest", draft, bound, draft.Sources) : View(draft);
    }
    [HttpPost, RequestSizeLimit(20000), EnableRateLimiting("AssistantPages")]
    public async Task<IActionResult> EditGuest(Guid id, Guid version, string? title, string? description, string? location, bool cancel)
    {
        if (!GuestEnabled) return NotFound(); if (!ModelState.IsValid || !SameOrigin()) return BadRequest();
        var browser = BrowserOwner(); var draft = conversations.ReadGuest(id, browser);
        if (draft is null || !await help.PublicSourcesCurrentAsync(draft.Sources, HttpContext.RequestAborted)) return NotFound();
        try
        {
            if (!conversations.EditGuest(id, version, browser, new(AssistantReviewKind.Request, title ?? "", description ?? "", location ?? ""), cancel)) return Conflict();
        }
        catch (ArgumentException) { return BadRequest(); }
        if (cancel) { Response.Cookies.Delete(GuestHintCookie); return RedirectToAction(nameof(Index)); }
        return RedirectToAction(nameof(Guest), new { id });
    }
    [Authorize, HttpPost, RequestSizeLimit(10000), EnableRateLimiting("PaymentActions")]
    public async Task<IActionResult> ImportGuest(Guid id, Guid version, Guid membershipId)
    {
        if (!GuestEnabled) return NotFound(); if (!ModelState.IsValid || !SameOrigin()) return BadRequest();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var scope = await access.CaptureAsync(membershipId, deadline.Token); if (scope is null) return NotFound();
            using var transfer = conversations.BeginTransfer(id, version, BrowserOwner(), scope); if (transfer is null) return Conflict();
            var draft = transfer.Draft;
            if (!await help.PublicSourcesCurrentAsync(draft.Sources, deadline.Token)) return NotFound();
            if (draft.ReviewId is { } prior)
            {
                var review = await actions.ReadAsync(prior, draft.ReviewConversationId, deadline.Token);
                if (review is null || review.Scope != scope) return NotFound();
                return RedirectToAction(nameof(Review), new { id = prior, conversationId = draft.ReviewConversationId });
            }
            // Import only canonical text/public provenance. Never reuse guest history, identity,
            // generated permission claims or browser payload as authenticated authority.
            var prepared = await actions.PrepareAsync(draft.ReviewConversationId, scope, draft.Draft, null, draft.Sources, deadline.Token);
            if (prepared.Code != MaintenanceCode.Success || prepared.View is null) return ActionError(prepared.Code);
            if (!transfer.Complete(prepared.View.Review.Id)) return Conflict();
            Response.Cookies.Delete(GuestHintCookie);
            return RedirectToAction(nameof(Review), new { id = prepared.View.Review.Id, conversationId = draft.ReviewConversationId });
        }
        catch (Exception exception) when (exception is System.Data.Common.DbException or Microsoft.EntityFrameworkCore.DbUpdateException or OperationCanceledException)
        { if (HttpContext.RequestAborted.IsCancellationRequested) throw; return StatusCode(503); }
    }
    [Authorize, HttpPost, RequestSizeLimit(10000), EnableRateLimiting("AssistantPages")]
    public async Task<IActionResult> Find(Guid membershipId, string? search)
    {
        // Read-only search shares portal authorization/SQL. It does not call the model or submit a draft.
        ViewData["Enabled"] = policy.Enabled; ViewData["ActionsEnabled"] = policy.ActionsEnabled; ViewData["FollowUpsEnabled"] = policy.FollowUpsEnabled;
        ViewData["ConversationId"] = Guid.NewGuid();
        if (!ModelState.IsValid || !SameOrigin()) return BadRequest();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var result = await help.FindRequestsAsync(membershipId, search, deadline.Token);
            if (result.Code == AssistantModelCode.Invalid) ViewData["SearchInvalid"] = true;
            if (result.Scope is null) return Answer(result);
            ViewData["SelectedMembership"] = result.Scope.MembershipId;
            ViewData["Memberships"] = new[] { new RequestMemberChoice(result.Scope.MembershipId, result.Scope.Suite) };
            return await ProtectedView("Index", result, result.Scope, []);
        }
        catch (Exception exception) when (exception is System.Data.Common.DbException or OperationCanceledException)
        { if (HttpContext.RequestAborted.IsCancellationRequested) throw; return Answer(new(AssistantModelCode.Unavailable)); }
    }
    [Authorize, HttpGet, EnableRateLimiting("AssistantPages")]
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
