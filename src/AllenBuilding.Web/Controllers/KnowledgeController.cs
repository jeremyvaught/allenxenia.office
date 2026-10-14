using AllenBuilding.Application.Access;
using AllenBuilding.Application.Knowledge;
using AllenBuilding.Infrastructure.Persistence;
using AllenBuilding.Web.Models;
using AllenBuilding.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Web.Controllers;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class KnowledgeController(KnowledgeActions actions, ICurrentActor current, OfficeDbContext db) : Controller
{
    [HttpGet] public async Task<IActionResult> Index(string? search) => await List(KnowledgeScope.Public, search);
    [Authorize, HttpGet] public async Task<IActionResult> Tenant(string? search) => await List(KnowledgeScope.Tenant, search);
    [HttpGet] public async Task<IActionResult> Article(Guid id) => await Read(id, KnowledgeScope.Public);
    [Authorize, HttpGet] public async Task<IActionResult> TenantArticle(Guid id) => await Read(id, KnowledgeScope.Tenant);

    [Authorize(Policy = OfficeSecurity.ManagerPolicy), HttpGet]
    public async Task<IActionResult> Manage() => View(await actions.ManageAsync());
    [Authorize(Policy = OfficeSecurity.ManagerPolicy), HttpGet]
    public async Task<IActionResult> Create() { await Buildings(); return View("Edit", new ArticleForm()); }
    [Authorize(Policy = OfficeSecurity.ManagerPolicy), HttpGet]
    public async Task<IActionResult> Edit(Guid id)
    {
        var article = (await actions.ManageAsync(id)).SingleOrDefault();
        if (article is null) return NotFound();
        ViewData["Article"] = article;
        return View(new ArticleForm
        {
            Id = article.Id,
            BuildingId = article.BuildingId,
            Version = article.Version,
            Title = article.Draft.Title,
            Body = article.Draft.Body,
            Audience = article.Draft.Audience,
            ReviewOn = article.Draft.ReviewOn
        });
    }
    [Authorize(Policy = OfficeSecurity.ManagerPolicy), HttpPost]
    public async Task<IActionResult> Save(ArticleForm form)
    {
        if (!ModelState.IsValid)
        {
            await Buildings(); Response.StatusCode = 400;
            // Do not attach publish controls to invalid/unsaved form values.
            return View("Edit", form);
        }
        var result = await actions.SaveAsync(form.Command());
        if (result is ContentCode.Success or ContentCode.Unavailable) return Outcome(result, form.Id);
        var saved = (await actions.ManageAsync(form.Id)).SingleOrDefault();
        if (result == ContentCode.Conflict && saved is null) return NotFound();
        if (saved is not null)
        {
            form.Version = saved.Version; form.BuildingId = saved.BuildingId;
            ModelState.Remove(nameof(form.Version)); ModelState.Remove(nameof(form.BuildingId));
            ViewData["Article"] = saved;
        }
        await Buildings();
        ViewData["UnsavedDraft"] = true;
        ModelState.AddModelError("", result == ContentCode.Conflict
            ? "The article changed in another tab. Your draft has been preserved. Compare it with the current saved revision, then save explicitly."
            : "Your draft has been preserved. Review the article values before saving again.");
        Response.StatusCode = result == ContentCode.Conflict ? 409 : 400;
        return View("Edit", form);
    }
    [Authorize(Policy = OfficeSecurity.ManagerPolicy), HttpPost]
    public async Task<IActionResult> Publish(PublishArticle command) => Outcome(await actions.PublishAsync(command), command.Id);

    private async Task<IActionResult> List(KnowledgeScope scope, string? search)
    {
        if (search?.Length > 120) return BadRequest();
        ViewData["Tenant"] = scope == KnowledgeScope.Tenant; ViewData["Search"] = search;
        return View("Index", await actions.FindAsync(scope, search));
    }
    private async Task<IActionResult> Read(Guid id, KnowledgeScope scope)
    {
        var source = (await actions.FindAsync(scope, id: id)).SingleOrDefault();
        if (source is null) return NotFound();
        ViewData["Tenant"] = scope == KnowledgeScope.Tenant;
        return View("Article", source);
    }
    private IActionResult Outcome(ContentCode code, Guid id)
    {
        if (code == ContentCode.Success) return RedirectToAction(nameof(Edit), new { id });
        if (code == ContentCode.Unavailable) return NotFound();
        Response.StatusCode = code == ContentCode.Conflict ? 409 : 400;
        return View("ContentError", code);
    }
    private async Task Buildings()
    {
        var actor = (await current.GetAsync())!;
        ViewData["Buildings"] = await db.Buildings.AsNoTracking().Where(x => actor.ManagedBuildings.Contains(x.Id))
            .OrderBy(x => x.Name).Select(x => new SelectListItem(x.Name, x.Id.ToString())).ToListAsync();
    }
}
