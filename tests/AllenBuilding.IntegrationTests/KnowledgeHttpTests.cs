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
        await using var host = new AccountTestHost(); await host.InitializeAsync(); var (managerId, building) = await host.SeedManagerAsync();
        using var manager = host.Browser(); await host.LoginManagerAsync(manager, managerId);
        var id = Guid.NewGuid();
        async Task<HttpResponseMessage> Save(string title, string body, Guid? version) => await AccountTestHost.PostAsync(manager,
            version is null ? "/Knowledge/Create" : $"/Knowledge/Edit/{id}", "/Knowledge/Save",
            ("Id", id.ToString()), ("BuildingId", building.ToString()), ("Version", version?.ToString() ?? ""),
            ("Title", title), ("Body", body), ("Audience", "ActiveTenant"), ("ReviewOn", "2027-01-01"));
        using var created = await Save("Original title", "Original body", null); Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        Guid oldVersion;
        using (var scope = host.Services.CreateScope()) oldVersion = (await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().HelpArticles.SingleAsync()).Version;
        using var first = await Save("Current saved title", "First tab saved body", oldVersion); Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        using var stale = await Save("Second tab title", "Preserve <script>second tab draft</script>", oldVersion);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var html = await stale.Content.ReadAsStringAsync();
        Assert.Contains("Second tab title", html); Assert.Contains("Preserve &lt;script&gt;second tab draft&lt;/script&gt;", html);
        Assert.Contains("First tab saved body", html); Assert.Contains("Your draft has been preserved", html);
        Assert.DoesNotContain("action=\"/Knowledge/Publish\"", html);
        Guid currentVersion;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); var article = await db.HelpArticles.SingleAsync();
            currentVersion = article.Version; Assert.NotEqual(oldVersion, currentVersion);
            Assert.Equal("First tab saved body", (await db.HelpRevisions.SingleAsync(x => x.Id == article.DraftRevisionId)).Body);
            Assert.Equal(2, await db.HelpRevisions.CountAsync()); Assert.Null(article.PublishedRevisionId);
        }
        Assert.Contains($"value=\"{currentVersion}\"", html); Assert.DoesNotContain($"value=\"{oldVersion}\"", html);
        using var retried = await Save("Second tab title", "Preserve <script>second tab draft</script>", currentVersion);
        Assert.Equal(HttpStatusCode.Redirect, retried.StatusCode);
        using var final = host.Services.CreateScope(); var saved = final.ServiceProvider.GetRequiredService<OfficeDbContext>();
        var latest = await saved.HelpArticles.SingleAsync(); Assert.Null(latest.PublishedRevisionId); Assert.Equal(3, await saved.HelpRevisions.CountAsync());
        Assert.Equal("Preserve <script>second tab draft</script>", (await saved.HelpRevisions.SingleAsync(x => x.Id == latest.DraftRevisionId)).Body);
    }
}
