using System.Net;
using AllenBuilding.Application.Knowledge;
using AllenBuilding.Domain.Knowledge;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AllenBuilding.IntegrationTests;

public sealed class KnowledgeHttpTests
{
    [Fact]
    public async Task Conflicting_article_edit_preserves_the_draft_and_requires_an_explicit_save_before_publication()
    {
        await using var host = new AccountTestHost();
        await host.InitializeAsync();
        var (managerId, building) = await host.SeedManagerAsync();
        using var manager = host.Browser();
        await host.LoginManagerAsync(manager, managerId);
        var id = Guid.NewGuid();
        async Task<HttpResponseMessage> Save(string title, string body, Guid? version) => await AccountTestHost.PostAsync(manager,
            version is null ? "/Knowledge/Create" : $"/Knowledge/Edit/{id}", "/Knowledge/Save",
            ("Id", id.ToString()), ("BuildingId", building.ToString()), ("Version", version?.ToString() ?? ""),
            ("Title", title), ("Body", body), ("Audience", "ActiveTenant"), ("ReviewOn", "2027-01-01"));
        using var created = await Save("Original title", "Original body", null);
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        Guid oldVersion;
        using (var scope = host.Services.CreateScope())
            oldVersion = (await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().HelpArticles.SingleAsync()).Version;
        using var first = await Save("Current saved title", "First tab saved body", oldVersion);
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        using var stale = await Save("Second tab title", "Preserve <script>second tab draft</script>", oldVersion);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var html = await stale.Content.ReadAsStringAsync();
        Assert.Contains("Second tab title", html);
        Assert.Contains("Preserve &lt;script&gt;second tab draft&lt;/script&gt;", html);
        Assert.Contains("First tab saved body", html);
        Assert.Contains("Your draft has been preserved", html);
        Assert.DoesNotContain("action=\"/Knowledge/Publish\"", html);
        Guid currentVersion;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            var article = await db.HelpArticles.SingleAsync();
            currentVersion = article.Version;
            Assert.NotEqual(oldVersion, currentVersion);
            Assert.Equal("First tab saved body", (await db.HelpRevisions.SingleAsync(x => x.Id == article.DraftRevisionId)).Body);
            Assert.Equal(2, await db.HelpRevisions.CountAsync());
            Assert.Null(article.PublishedRevisionId);
        }
        Assert.Contains($"value=\"{currentVersion}\"", html);
        Assert.DoesNotContain($"value=\"{oldVersion}\"", html);
        using var retried = await Save("Second tab title", "Preserve <script>second tab draft</script>", currentVersion);
        Assert.Equal(HttpStatusCode.Redirect, retried.StatusCode);
        using var final = host.Services.CreateScope();
        var saved = final.ServiceProvider.GetRequiredService<OfficeDbContext>();
        var latest = await saved.HelpArticles.SingleAsync();
        Assert.Null(latest.PublishedRevisionId);
        Assert.Equal(3, await saved.HelpRevisions.CountAsync());
        Assert.Equal("Preserve <script>second tab draft</script>", (await saved.HelpRevisions.SingleAsync(x => x.Id == latest.DraftRevisionId)).Body);
    }

    [Fact]
    public async Task Rejected_saves_keep_the_draft_and_return_the_matching_status()
    {
        await using var host = new AccountTestHost();
        await host.InitializeAsync();
        var (managerId, building) = await host.SeedManagerAsync();
        var unmanaged = await ManagementHttpTests.SeedBuildingAsync(host, "Unmanaged building");
        using var manager = host.Browser();
        await host.LoginManagerAsync(manager, managerId);

        using var missing = await manager.GetAsync($"/Knowledge/Edit/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var id = Guid.NewGuid();
        using var invalid = await Save(manager, id, building, null, "", "Body without a title", "2027-01-01");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var invalidHtml = await invalid.Content.ReadAsStringAsync();
        Assert.Contains("Body without a title", invalidHtml);
        Assert.Matches($"<option [^>]*value=\"{building}\">Synthetic building</option>", invalidHtml);
        Assert.DoesNotContain("Unmanaged building", invalidHtml);
        Assert.DoesNotContain("action=\"/Knowledge/Publish\"", invalidHtml);

        using var noReviewDate = await Save(manager, id, building, null, "Lobby hours", "Open 7 to 7", "0001-01-01");
        Assert.Equal(HttpStatusCode.BadRequest, noReviewDate.StatusCode);
        var noReviewHtml = await noReviewDate.Content.ReadAsStringAsync();
        Assert.Contains("Your draft has been preserved. Review the article values before saving again.", noReviewHtml);
        Assert.Contains("Open 7 to 7", noReviewHtml);
        Assert.DoesNotContain("Review the saved revision", noReviewHtml);

        using var unknownVersion = await Save(manager, id, building, Guid.NewGuid(), "Lobby hours", "Open 7 to 7", "2027-01-01");
        Assert.Equal(HttpStatusCode.NotFound, unknownVersion.StatusCode);
        using var foreign = await Save(manager, id, unmanaged, null, "Lobby hours", "Open 7 to 7", "2027-01-01");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        using (var scope = host.Services.CreateScope())
            Assert.False(await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().HelpArticles.AnyAsync());

        using var created = await Save(manager, id, building, null, "Lobby hours", "Open 7 to 7", "2027-01-01");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        Assert.Equal($"/Knowledge/Edit/{id}", created.Headers.Location?.OriginalString);
        var (version, _) = await ArticleAsync(host, id);
        using var invalidEdit = await Save(manager, id, building, version, "Lobby hours", "Unsaved new body", "0001-01-01");
        Assert.Equal(HttpStatusCode.BadRequest, invalidEdit.StatusCode);
        var invalidEditHtml = await invalidEdit.Content.ReadAsStringAsync();
        Assert.Contains("Unsaved new body", invalidEditHtml);
        Assert.Contains("Review the saved revision", invalidEditHtml);
        Assert.Contains("Open 7 to 7", invalidEditHtml);
        Assert.Contains($"value=\"{version}\"", invalidEditHtml);
        Assert.DoesNotContain("action=\"/Knowledge/Publish\"", invalidEditHtml);

        using var check = host.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<OfficeDbContext>();
        Assert.Equal("Open 7 to 7", (await db.HelpRevisions.AsNoTracking().SingleAsync()).Body);
        Assert.Equal(version, (await db.HelpArticles.AsNoTracking().SingleAsync()).Version);
    }

    [Fact]
    public async Task Publication_controls_what_visitors_and_tenants_can_find_and_rejects_stale_reviews()
    {
        await using var host = new AccountTestHost();
        await host.InitializeAsync();
        var (managerId, building) = await host.SeedManagerAsync();
        var suite = await ManagementHttpTests.SeedSuiteAsync(host, building, "Shared office");
        await ManagementHttpTests.SeedTenantAsync(host, "tenant@example.test", suite);
        using var visitor = host.Browser();
        using var manager = host.Browser();
        await host.LoginManagerAsync(manager, managerId);

        Assert.Contains("No articles yet.", await manager.GetStringAsync("/Knowledge/Manage"));
        Assert.Contains("No help articles found.", await visitor.GetStringAsync("/Knowledge"));
        using var longSearch = await visitor.GetAsync("/Knowledge?search=" + new string('a', 121));
        Assert.Equal(HttpStatusCode.BadRequest, longSearch.StatusCode);
        using var anonymousTenant = await visitor.GetAsync("/Knowledge/Tenant");
        Assert.StartsWith("http://localhost/Account/Login", anonymousTenant.Headers.Location?.OriginalString);

        var lobby = Guid.NewGuid();
        var dock = Guid.NewGuid();
        using (var saved = await Save(manager, lobby, building, null, "Lobby hours", "Open seven to seven", "2027-01-01", "Public"))
            Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        using (var saved = await Save(manager, dock, building, null, "Loading dock code", "Ask the office for the dock code", "2027-02-01"))
            Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        var manage = await manager.GetStringAsync("/Knowledge/Manage");
        Assert.Contains("Draft · Public", manage);
        Assert.Contains("Draft · Active tenants", manage);
        Assert.Contains($"href=\"/Knowledge/Edit/{dock}\"", manage);
        Assert.Contains("No help articles found.", await visitor.GetStringAsync("/Knowledge"));

        var (lobbyVersion, lobbyDraft) = await ArticleAsync(host, lobby);
        using var stale = await Publish(lobby, Guid.NewGuid(), lobbyDraft);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("This article changed while you were editing.", await stale.Content.ReadAsStringAsync());
        using var wrongRevision = await Publish(lobby, lobbyVersion, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Conflict, wrongRevision.StatusCode);
        using var unknown = await Publish(Guid.NewGuid(), lobbyVersion, lobbyDraft);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var unreviewed = await Publish(lobby, lobbyVersion, Guid.Empty);
        Assert.Equal(HttpStatusCode.BadRequest, unreviewed.StatusCode);
        Assert.Contains("Check the article fields and try again.", await unreviewed.Content.ReadAsStringAsync());
        Assert.Contains("No help articles found.", await visitor.GetStringAsync("/Knowledge"));

        using (var published = await Publish(lobby, lobbyVersion, lobbyDraft))
        {
            Assert.Equal(HttpStatusCode.Redirect, published.StatusCode);
            Assert.Equal($"/Knowledge/Edit/{lobby}", published.Headers.Location?.OriginalString);
        }
        var (dockVersion, dockDraft) = await ArticleAsync(host, dock);
        using (var published = await Publish(dock, dockVersion, dockDraft))
            Assert.Equal(HttpStatusCode.Redirect, published.StatusCode);

        var index = await visitor.GetStringAsync("/Knowledge");
        Assert.Contains($"href=\"/Knowledge/Article/{lobby}\"", index);
        Assert.DoesNotContain("Loading dock code", index);
        Assert.Contains("Lobby hours", await visitor.GetStringAsync("/Knowledge?search=SEVEN"));
        Assert.Contains("No help articles found.", await visitor.GetStringAsync("/Knowledge?search=elevator"));
        Assert.Contains("Open seven to seven", await visitor.GetStringAsync($"/Knowledge/Article/{lobby}"));
        using var hidden = await visitor.GetAsync($"/Knowledge/Article/{dock}");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);

        using var tenant = host.Browser();
        using (var login = await AccountTestHost.PostAsync(tenant, "/Account/Login", "/Account/Login",
            ("Email", "tenant@example.test"), ("Password", AccountTestHost.Password)))
            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var tenantIndex = await tenant.GetStringAsync("/Knowledge/Tenant");
        Assert.Contains($"href=\"/Knowledge/TenantArticle/{dock}\"", tenantIndex);
        Assert.Contains($"href=\"/Knowledge/TenantArticle/{lobby}\"", tenantIndex);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(tenantIndex, "Tenant guidance"));
        Assert.Contains("action=\"/Knowledge/Tenant\"", tenantIndex);
        var tenantArticle = await tenant.GetStringAsync($"/Knowledge/TenantArticle/{dock}");
        Assert.Contains("Ask the office for the dock code", tenantArticle);
        Assert.Contains("href=\"/Knowledge/Tenant\"", tenantArticle);
        using var tenantMissing = await tenant.GetAsync($"/Knowledge/TenantArticle/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, tenantMissing.StatusCode);
        using var tenantLongSearch = await tenant.GetAsync("/Knowledge/Tenant?search=" + new string('a', 121));
        Assert.Equal(HttpStatusCode.BadRequest, tenantLongSearch.StatusCode);

        var (editedVersion, _) = await ArticleAsync(host, lobby);
        using (var edited = await Save(manager, lobby, building, editedVersion, "Lobby hours (winter)", "Open eight to six", "2027-03-01", "Public"))
            Assert.Equal(HttpStatusCode.Redirect, edited.StatusCode);
        manage = await manager.GetStringAsync("/Knowledge/Manage");
        Assert.Contains("Has unpublished edits. Readers still see the last published version.", manage);
        Assert.Contains("Published · Public", manage);
        var editPage = await manager.GetStringAsync($"/Knowledge/Edit/{lobby}");
        Assert.Contains("A previous approved revision remains visible until you publish these edits.", editPage);
        Assert.Contains("Withdraw article", editPage);
        var publicArticle = await visitor.GetStringAsync($"/Knowledge/Article/{lobby}");
        Assert.Contains("Open seven to seven", publicArticle);
        Assert.DoesNotContain("Open eight to six", publicArticle);

        Task<HttpResponseMessage> Publish(Guid articleId, Guid version, Guid revision) => AccountTestHost.PostAsync(manager, "/Knowledge/Manage",
            "/Knowledge/Publish", ("Id", articleId.ToString()), ("Version", version.ToString()), ("ReviewedRevisionId", revision.ToString()));
    }

    private static Task<HttpResponseMessage> Save(HttpClient manager, Guid id, Guid building, Guid? version, string title, string body,
        string reviewOn, string audience = "ActiveTenant") => AccountTestHost.PostAsync(manager, "/Knowledge/Create", "/Knowledge/Save",
        ("Id", id.ToString()), ("BuildingId", building.ToString()), ("Version", version?.ToString() ?? ""),
        ("Title", title), ("Body", body), ("Audience", audience), ("ReviewOn", reviewOn));

    private static async Task<(Guid Version, Guid Draft)> ArticleAsync(AccountTestHost host, Guid id)
    {
        using var scope = host.Services.CreateScope();
        var article = await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().HelpArticles.AsNoTracking().SingleAsync(x => x.Id == id);
        return (article.Version, article.DraftRevisionId);
    }
}
