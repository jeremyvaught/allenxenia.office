using System.Net;
using System.Text.RegularExpressions;
using AllenBuilding.Application.Assistant;
using AllenBuilding.Domain.Knowledge;
using AllenBuilding.Domain.Maintenance;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AllenBuilding.IntegrationTests;

public sealed class AssistantGuestHttpTests
{
    [Fact]
    public async Task Guest_text_is_transient_browser_owned_and_never_tenant_authority_or_an_automatic_submission()
    {
        var model = new Model(); await using var host = Host(model); await host.InitializeAsync(); await Seed(host);
        using var browser = host.Browser(); var path = await Stage(browser);
        Assert.True(model.Input!.CanStageGuest); Assert.False(model.Input.Tenant); Assert.False(model.Input.CanPrepare);
        Assert.DoesNotContain("private-canary", string.Join(" ", model.Input.Sources.Select(x => x.Excerpt)));
        var html = await browser.GetStringAsync(path); Assert.Contains("Sign in to continue", html); Assert.Contains("Synthetic sink", html);
        Assert.DoesNotContain("Private suite", html); Assert.Contains("no-store", (await browser.GetAsync(path)).Headers.CacheControl!.ToString());
        using var foreign = host.Browser(); Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await AccountTestHost.PostAsync(browser, path, "/Assistant/ImportGuest", Fields(html))).StatusCode);
        using var scope = host.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
        Assert.Empty(await db.AssistantMaintenanceReviews.ToListAsync()); Assert.Empty(await db.WorkOrders.ToListAsync());
    }
    [Fact]
    public async Task Explicit_selection_imports_only_canonical_payload_then_separate_confirmation_commits_once_with_exact_replays()
    {
        var model = new Model(); await using var host = Host(model); await host.InitializeAsync(); var fixture = await Seed(host);
        using var browser = host.Browser(); var path = await Stage(browser); var guestHtml = await browser.GetStringAsync(path); var fields = Fields(guestHtml);
        await Login(browser); Assert.Contains("Continue your maintenance draft", await browser.GetStringAsync("/Portal"));
        Assert.Contains("Choose your office", await browser.GetStringAsync(path));
        using var foreignScope = await AccountTestHost.PostAsync(browser, "/Assistant", "/Assistant/ImportGuest", fields.Concat(new[] { ("membershipId", fixture.Foreign.Id.ToString()) }).ToArray()); Assert.Equal(HttpStatusCode.NotFound, foreignScope.StatusCode);
        var importFields = fields.Concat(new[] { ("membershipId", fixture.First.Id.ToString()), ("title", "Forged unreviewed title"), ("conversationId", Guid.NewGuid().ToString()) }).ToArray();
        using var imported = await AccountTestHost.PostAsync(browser, path, "/Assistant/ImportGuest", importFields);
        Assert.Equal(HttpStatusCode.Redirect, imported.StatusCode); var reviewPath = imported.Headers.Location!.OriginalString;
        Assert.StartsWith("/Assistant/Review", reviewPath); var reviewHtml = await browser.GetStringAsync(reviewPath);
        Assert.Contains("Synthetic sink", reviewHtml); Assert.DoesNotContain("Forged unreviewed title", reviewHtml);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); Assert.Empty(await db.WorkOrders.ToListAsync());
            var review = Assert.Single(await db.AssistantMaintenanceReviews.ToListAsync()); Assert.Equal(fixture.First.Id, review.MembershipId);
            Assert.Equal("Synthetic sink", review.Title); Assert.Equal(AssistantReviewState.Ready, review.State);
        }
        using var repeated = await AccountTestHost.PostAsync(browser, "/Assistant", "/Assistant/ImportGuest", importFields);
        Assert.Equal(reviewPath, repeated.Headers.Location?.OriginalString);
        using var retargeted = await AccountTestHost.PostAsync(browser, "/Assistant", "/Assistant/ImportGuest", fields.Concat(new[] { ("membershipId", fixture.Second.Id.ToString()) }).ToArray()); Assert.Equal(HttpStatusCode.Conflict, retargeted.StatusCode);
        var confirmation = Fields(reviewHtml, "id", "conversationId", "version", "operationId");
        using var confirmed = await AccountTestHost.PostAsync(browser, reviewPath, "/Assistant/Confirm", confirmation); Assert.Equal(HttpStatusCode.Redirect, confirmed.StatusCode);
        using var repeatedAfterConfirm = await AccountTestHost.PostAsync(browser, "/Assistant", "/Assistant/ImportGuest", importFields); Assert.Equal(reviewPath, repeatedAfterConfirm.Headers.Location?.OriginalString);
        using var confirmedAgain = await AccountTestHost.PostAsync(browser, reviewPath, "/Assistant/Confirm", confirmation); Assert.Equal(HttpStatusCode.Redirect, confirmedAgain.StatusCode);
        Assert.Equal(1, model.Calls);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); Assert.Single(await db.WorkOrders.ToListAsync());
            Assert.Single(await db.MaintenanceReceipts.ToListAsync()); Assert.Equal(AssistantReviewState.Completed, Assert.Single(await db.AssistantMaintenanceReviews.ToListAsync()).State);
        }
        await AccountTestHost.PostAsync(browser, "/Portal", "/Account/Logout");
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(path)).StatusCode);
    }
    [Fact]
    public async Task Guest_edit_invalidates_old_version_cancel_removes_text_and_expiry_prevents_import()
    {
        var model = new Model(); await using var host = Host(model); await host.InitializeAsync(); await Seed(host); using var browser = host.Browser();
        var path = await Stage(browser); var fields = Fields(await browser.GetStringAsync(path));
        using var edited = await AccountTestHost.PostAsync(browser, path, "/Assistant/EditGuest", fields.Concat(new[] { ("title", "Edited sink"), ("description", "Water drips"), ("location", "Kitchen") }).ToArray()); Assert.Equal(HttpStatusCode.Redirect, edited.StatusCode);
        using var stale = await AccountTestHost.PostAsync(browser, path, "/Assistant/EditGuest", fields.Concat(new[] { ("cancel", "true") }).ToArray()); Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var current = await browser.GetStringAsync(path); Assert.Contains("Edited sink", current);
        browser.DefaultRequestHeaders.Add("Origin", "https://foreign.test");
        Assert.Equal(HttpStatusCode.BadRequest, (await AccountTestHost.PostAsync(browser, path, "/Assistant/EditGuest", Fields(current).Concat(new[] { ("cancel", "true") }).ToArray())).StatusCode);
        browser.DefaultRequestHeaders.Remove("Origin");
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsync("/Assistant/EditGuest", new FormUrlEncodedContent(Fields(current).ToDictionary(x => x.Key, x => x.Value)))).StatusCode);
        using var cancelled = await AccountTestHost.PostAsync(browser, path, "/Assistant/EditGuest", Fields(current).Concat(new[] { ("cancel", "true") }).ToArray()); Assert.Equal(HttpStatusCode.Redirect, cancelled.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(path)).StatusCode);
        var expiredPath = await Stage(browser); host.Clock.Offset = TimeSpan.FromMinutes(11);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(expiredPath)).StatusCode);
        using var scope = host.Services.CreateScope(); Assert.Empty(await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().AssistantMaintenanceReviews.ToListAsync());
    }
    [Fact]
    public async Task Withdrawn_public_provenance_and_revoked_original_membership_block_handoff_and_review()
    {
        var model = new Model(); await using var host = Host(model); await host.InitializeAsync(); var fixture = await Seed(host); using var browser = host.Browser();
        var path = await Stage(browser); var fields = Fields(await browser.GetStringAsync(path)); await Login(browser);
        using var imported = await AccountTestHost.PostAsync(browser, path, "/Assistant/ImportGuest", fields.Concat(new[] { ("membershipId", fixture.First.Id.ToString()) }).ToArray()); var reviewPath = imported.Headers.Location!.OriginalString;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            (await db.Memberships.SingleAsync(x => x.Id == fixture.First.Id)).Revoke(fixture.Manager, "Synthetic revocation", DateTimeOffset.UtcNow);
            db.ActiveGrants.Remove(await db.ActiveGrants.SingleAsync(x => x.MembershipId == fixture.First.Id)); await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(path)).StatusCode); Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(reviewPath)).StatusCode);
        await AccountTestHost.PostAsync(browser, "/Portal", "/Account/Logout");
        var secondPath = await Stage(browser); var secondFields = Fields(await browser.GetStringAsync(secondPath)); await Login(browser);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); (await db.HelpArticles.SingleAsync(x => x.Id == fixture.Article)).Withdraw(); await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(secondPath)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AccountTestHost.PostAsync(browser, "/Assistant", "/Assistant/ImportGuest", secondFields.Concat(new[] { ("membershipId", fixture.Second.Id.ToString()) }).ToArray())).StatusCode);
        using var check = host.Services.CreateScope(); var database = check.ServiceProvider.GetRequiredService<OfficeDbContext>(); Assert.Empty(await database.WorkOrders.ToListAsync()); Assert.Single(await database.AssistantMaintenanceReviews.ToListAsync());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disabled_guest_stage_and_authenticated_public_mode_cannot_prepare_an_anonymous_action(bool signedIn)
    {
        var model = new Model(); await using var host = Host(model, guest: signedIn); await host.InitializeAsync(); await Seed(host); using var browser = host.Browser();
        if (signedIn) await Login(browser);
        using var response = await AccountTestHost.PostAsync(browser, "/Assistant", "/Assistant/Ask", ("question", "Prepare a repair"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); Assert.False(model.Input!.CanStageGuest);
        using var scope = host.Services.CreateScope(); Assert.Empty(await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().AssistantMaintenanceReviews.ToListAsync());
    }
    private static AccountTestHost Host(Model model, bool guest = true) => new() { AssistantModel = model, AssistantTenantHelpEnabled = true, AssistantActionsEnabled = true, AssistantFollowUpsEnabled = true, AssistantGuestDraftsEnabled = guest };
    private static async Task<string> Stage(HttpClient browser)
    {
        using var result = await AccountTestHost.PostAsync(browser, "/Assistant", "/Assistant/Ask", ("question", "repair entry guidance: Synthetic sink drips in Kitchen"));
        Assert.Equal(HttpStatusCode.OK, result.StatusCode); var html = await result.Content.ReadAsStringAsync();
        var path = Regex.Match(html, "href=\"(/Assistant/Guest/[^\"]+)\"").Groups[1].Value; Assert.NotEmpty(path); return path;
    }
    private static (string Key, string Value)[] Fields(string html, params string[] names) => (names.Length == 0 ? new[] { "id", "version" } : names).Select(name =>
        (name, WebUtility.HtmlDecode(Regex.Match(html, "name=\"" + name + "\"[^>]*value=\"([^\"]+)\"").Groups[1].Value))).ToArray();
    private static async Task Login(HttpClient browser)
    {
        using var result = await AccountTestHost.PostAsync(browser, "/Account/Login", "/Account/Login", ("Email", "guest-tenant@example.test"), ("Password", AccountTestHost.Password)); Assert.Equal("/Portal", result.Headers.Location?.OriginalString);
    }
    private sealed record Fixture(Membership First, Membership Second, Membership Foreign, Guid Article, string Manager);
    private static async Task<Fixture> Seed(AccountTestHost host)
    {
        var (manager, building) = await host.SeedManagerAsync(); using var scope = host.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
        var tenant = new OfficeUser { Email = "guest-tenant@example.test", UserName = "guest-tenant@example.test", EmailConfirmed = true }; Assert.True((await users.CreateAsync(tenant, AccountTestHost.Password)).Succeeded);
        var outsider = new OfficeUser { Email = "guest-outsider@example.test", UserName = "guest-outsider@example.test", EmailConfirmed = true }; Assert.True((await users.CreateAsync(outsider, AccountTestHost.Password)).Succeeded);
        var suite = new Suite { BuildingId = building, Name = "Private suite" }; var other = new Suite { BuildingId = building, Name = "Second private suite" };
        var first = Membership.Grant(suite.Id, tenant.Id, DateTimeOffset.UtcNow.AddDays(-1)); var second = Membership.Grant(other.Id, tenant.Id, DateTimeOffset.UtcNow.AddDays(-1)); var foreign = Membership.Grant(suite.Id, outsider.Id, DateTimeOffset.UtcNow.AddDays(-1));
        db.AddRange(suite, other, first, second, foreign, new ActiveGrant { MembershipId = first.Id, SuiteId = suite.Id, UserId = tenant.Id }, new ActiveGrant { MembershipId = second.Id, SuiteId = other.Id, UserId = tenant.Id }, new ActiveGrant { MembershipId = foreign.Id, SuiteId = suite.Id, UserId = outsider.Id });
        var publicId = Guid.NewGuid();
        foreach (var audience in new[] { ArticleAudience.Public, ArticleAudience.ActiveTenant })
        {
            var id = audience == ArticleAudience.Public ? publicId : Guid.NewGuid(); var revision = HelpRevision.Create(id, "Repair entry guidance", audience == ArticleAudience.Public ? "Report a repair through your office" : "private-canary", audience, manager, new(2027, 1, 1), DateTimeOffset.UtcNow); var article = HelpArticle.Create(id, building, revision.Id); article.Publish(revision.Id); db.AddRange(article, revision);
        }
        await db.SaveChangesAsync(); return new(first, second, foreign, publicId, manager);
    }
    private sealed class Model : IAssistantModel
    {
        public int Calls { get; private set; }
        public AssistantModelInput? Input { get; private set; }
        public Task<AssistantModelReply> AnswerAsync(AssistantModelInput input, CancellationToken cancellationToken)
        {
            Calls++; Input = input;
            return Task.FromResult(new AssistantModelReply(AssistantModelCode.Success, "Check the temporary draft and sign in. Nothing was submitted.", input.Sources.Select(x => x.ArticleId).ToArray(), "new_request", Draft: new(AssistantReviewKind.Request, " Synthetic sink ", " Water drips ", " Kitchen ")));
        }
    }
}
