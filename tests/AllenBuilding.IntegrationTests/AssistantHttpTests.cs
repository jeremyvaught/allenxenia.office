using System.Net;
using AllenBuilding.Application.Assistant;
using AllenBuilding.Domain.Knowledge;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AllenBuilding.IntegrationTests;

public sealed class AssistantHttpTests
{
    [Fact]
    public async Task Public_generation_sees_only_published_public_excerpts_and_renders_escaped_citations_and_fixed_links()
    {
        var model = new Model(); await using var host = new AccountTestHost { AssistantModel = model }; await host.InitializeAsync();
        var publicId = await Seed(host); using var browser = host.Browser();
        model.Answer = "<script>private-output</script>"; model.Route = "billing";
        using var response = await AccountTestHost.PostAsync(browser, "/Assistant", "/Assistant/Ask", ("question", "entry guidance"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal(publicId, Assert.Single(model.Input!.Sources).ArticleId); Assert.False(model.Input.Tenant);
        Assert.DoesNotContain("tenant-canary", model.Input.Sources[0].Excerpt); Assert.DoesNotContain("draft-canary", model.Input.Sources[0].Excerpt);
        var html = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain(model.Answer, html); Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("/Knowledge/Article/" + publicId, html); Assert.Contains("/Account/Login", html);
        using var scope = host.Services.CreateScope(); Assert.Empty(await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().WorkOrders.ToListAsync());
    }
    [Fact]
    public async Task Article_withdrawal_during_generation_suppresses_the_entire_answer()
    {
        var model = new Model(); await using var host = new AccountTestHost { AssistantModel = model }; await host.InitializeAsync();
        var id = await Seed(host); using var browser = host.Browser();
        model.Answer = "generated-canary";
        model.BeforeAnswer = async () =>
        {
            using var scope = host.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            (await db.HelpArticles.SingleAsync(x => x.Id == id)).Withdraw(); await db.SaveChangesAsync();
        };
        using var response = await AccountTestHost.PostAsync(browser, "/Assistant", "/Assistant/Ask", ("question", "entry"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.DoesNotContain("generated-canary", await response.Content.ReadAsStringAsync());
    }
    [Fact]
    public async Task Forged_and_cross_origin_browser_posts_never_reach_the_model()
    {
        var model = new Model(); await using var host = new AccountTestHost { AssistantModel = model }; await host.InitializeAsync(); using var browser = host.Browser();
        using var forged = await browser.PostAsync("/Assistant/Ask", new FormUrlEncodedContent(new Dictionary<string, string> { ["question"] = "entry" }));
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        browser.DefaultRequestHeaders.Add("Origin", "https://untrusted.test");
        using var foreign = await AccountTestHost.PostAsync(browser, "/Assistant", "/Assistant/Ask", ("question", "entry"));
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode); Assert.Equal(0, model.Calls);
    }
    [Theory]
    [InlineData("failure")]
    [InlineData("foreign-citation")]
    [InlineData("unsafe-route")]
    public async Task Failed_or_unverified_output_has_a_safe_page_fallback(string failure)
    {
        var model = new Model
        {
            Answer = "generated-canary",
            Code = failure == "failure" ? AssistantModelCode.Unavailable : AssistantModelCode.Success,
            ForeignCitation = failure == "foreign-citation",
            Route = failure == "unsafe-route" ? "https://untrusted.test" : "help"
        };
        await using var host = new AccountTestHost { AssistantModel = model }; await host.InitializeAsync(); using var browser = host.Browser();
        using var response = await AccountTestHost.PostAsync(browser, "/Assistant", "/Assistant/Ask", ("question", "entry"));
        Assert.True((int)response.StatusCode >= 400); Assert.DoesNotContain("generated-canary", await response.Content.ReadAsStringAsync());
    }
    [Fact]
    public async Task Disabled_assistant_has_no_form_or_provider_dependency()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync(); using var browser = host.Browser();
        var html = await browser.GetStringAsync("/Assistant"); Assert.Contains("unavailable", html); Assert.DoesNotContain("data-assistant-form", html);
    }
    private static async Task<Guid> Seed(AccountTestHost host)
    {
        var (editor, building) = await host.SeedManagerAsync(); using var scope = host.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
        var publicId = Guid.NewGuid();
        foreach (var kind in new[] { "public", "tenant", "draft" })
        {
            var id = kind == "public" ? publicId : Guid.NewGuid();
            var revision = HelpRevision.Create(id, "Entry guidance " + kind, kind + "-canary entry directions", kind == "tenant" ? ArticleAudience.ActiveTenant : ArticleAudience.Public,
                editor, new(2027, 1, 1), DateTimeOffset.UtcNow);
            var article = HelpArticle.Create(id, building, revision.Id); if (kind != "draft") article.Publish(revision.Id); db.AddRange(article, revision);
        }
        await db.SaveChangesAsync(); return publicId;
    }
    private sealed class Model : IAssistantModel
    {
        public AssistantModelInput? Input { get; private set; }
        public int Calls { get; private set; }
        public string Answer { get; set; } = "Published guidance";
        public string Route { get; set; } = "help";
        public AssistantModelCode Code { get; set; } = AssistantModelCode.Success;
        public bool ForeignCitation { get; init; }
        public Func<Task>? BeforeAnswer { get; set; }
        public async Task<AssistantModelReply> AnswerAsync(AssistantModelInput input, CancellationToken cancellationToken)
        {
            Calls++; Input = input; if (BeforeAnswer is not null) await BeforeAnswer();
            return new(Code, Answer, ForeignCitation ? [Guid.NewGuid()] : input.Sources.Select(x => x.ArticleId).ToArray(), Route);
        }
    }
}
