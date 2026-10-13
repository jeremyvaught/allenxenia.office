using System.Net;
using AllenBuilding.Application.Assistant;
using AllenBuilding.Domain.Knowledge;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AllenBuilding.IntegrationTests;

public sealed class AssistantTenantHttpTests
{
    [Fact]
    public async Task A_selected_original_membership_filters_before_generation_and_uses_private_citations_and_secure_routes()
    {
        var model = new Model(); await using var host = new AccountTestHost { AssistantModel = model, AssistantTenantHelpEnabled = true }; await host.InitializeAsync();
        var fixture = await Seed(host); using var browser = host.Browser(); await Login(browser);
        var form = await browser.GetStringAsync("/Assistant"); Assert.Contains(fixture.First.Id.ToString(), form); Assert.Contains(fixture.Second.Id.ToString(), form);
        using var response = await AccountTestHost.PostAsync(browser, "/Assistant", "/Assistant/Ask", ("question", "entry"), ("membershipId", fixture.First.Id.ToString()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); var html = await response.Content.ReadAsStringAsync();
        Assert.True(model.Input!.Tenant); Assert.Contains(model.Input.Sources, x => x.ArticleId == fixture.FirstArticle);
        Assert.DoesNotContain(model.Input.Sources, x => x.ArticleId == fixture.SecondArticle);
        Assert.Contains("/Knowledge/TenantArticle/" + fixture.FirstArticle, html); Assert.Contains("/Maintenance/Create", html);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString()); Assert.DoesNotContain("Second confidential suite", html);
        Assert.Contains("name=\"returnUrl\" value=\"/Assistant\"", html);
        using var scope = host.Services.CreateScope(); Assert.Empty(await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().WorkOrders.ToListAsync());
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("stamp")]
    [InlineData("withdraw")]
    [InlineData("expire")]
    public async Task Revocation_or_source_change_after_html_render_discards_protected_output_even_with_another_active_membership(string change)
    {
        var model = new Model(); AccountTestHost host = null!; Fixture fixture = null!;
        host = new AccountTestHost
        {
            AssistantModel = model,
            AssistantTenantHelpEnabled = true,
            BeforeAssistantRelease = async () =>
            {
                using var scope = host.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
                if (change == "revoke")
                {
                    (await db.Memberships.SingleAsync(x => x.Id == fixture.First.Id)).Revoke(fixture.EditorId, "Synthetic access ended", DateTimeOffset.UtcNow);
                    db.ActiveGrants.Remove(await db.ActiveGrants.SingleAsync(x => x.MembershipId == fixture.First.Id));
                }
                if (change == "stamp") (await db.Users.SingleAsync(x => x.Id == fixture.First.UserId)).SecurityStamp = Guid.NewGuid().ToString();
                if (change == "withdraw") (await db.HelpArticles.SingleAsync(x => x.Id == fixture.FirstArticle)).Withdraw();
                if (change == "expire") host.Clock.Offset = TimeSpan.FromHours(2);
                await db.SaveChangesAsync();
            }
        };
        await using (host)
        {
            await host.InitializeAsync(); fixture = await Seed(host); using var browser = host.Browser(); await Login(browser);
            using var response = await AccountTestHost.PostAsync(browser, "/Assistant", "/Assistant/Ask", ("question", "entry"), ("membershipId", fixture.First.Id.ToString()));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); Assert.Equal(1, model.Calls);
            var html = await response.Content.ReadAsStringAsync(); Assert.Contains("Access changed", html);
            Assert.DoesNotContain("generated-private-canary", html); Assert.DoesNotContain("First confidential suite", html); Assert.DoesNotContain(fixture.FirstArticle.ToString(), html);
            using var scope = host.Services.CreateScope(); Assert.True(await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().ActiveGrants.AnyAsync(x => x.MembershipId == fixture.Second.Id));
        }
    }

    [Theory]
    [InlineData("guest")]
    [InlineData("foreign")]
    [InlineData("revoked")]
    [InlineData("disabled")]
    public async Task Invalid_scope_is_rejected_before_provider_input(string mode)
    {
        var model = new Model(); await using var host = new AccountTestHost { AssistantModel = model, AssistantTenantHelpEnabled = mode != "disabled" }; await host.InitializeAsync();
        var fixture = await Seed(host); using var browser = host.Browser();
        if (mode != "guest") await Login(browser);
        if (mode == "revoked")
        {
            using var scope = host.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            (await db.Memberships.SingleAsync(x => x.Id == fixture.First.Id)).Revoke(fixture.EditorId, "Access ended", DateTimeOffset.UtcNow);
            db.ActiveGrants.Remove(await db.ActiveGrants.SingleAsync(x => x.MembershipId == fixture.First.Id)); await db.SaveChangesAsync();
        }
        using var response = await AccountTestHost.PostAsync(browser, "/Assistant", "/Assistant/Ask", ("question", "entry"),
            ("membershipId", (mode == "foreign" ? fixture.Foreign.Id : fixture.First.Id).ToString()));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task Current_access_probe_ends_old_output_and_never_returns_private_content()
    {
        var model = new Model(); await using var host = new AccountTestHost { AssistantModel = model, AssistantTenantHelpEnabled = true }; await host.InitializeAsync();
        var fixture = await Seed(host); using var browser = host.Browser(); await Login(browser);
        using var accessible = await browser.GetAsync("/Assistant/Access?membershipId=" + fixture.First.Id); Assert.Equal(HttpStatusCode.NoContent, accessible.StatusCode);
        using var foreign = await browser.GetAsync("/Assistant/Access?membershipId=" + fixture.Foreign.Id); Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.DoesNotContain("confidential", await foreign.Content.ReadAsStringAsync()); Assert.Equal(0, model.Calls);
    }

    private static async Task Login(HttpClient browser)
    {
        using var response = await AccountTestHost.PostAsync(browser, "/Account/Login", "/Account/Login", ("Email", "assistant-tenant@example.test"), ("Password", AccountTestHost.Password));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }
    private static async Task<Fixture> Seed(AccountTestHost host)
    {
        var (editor, firstBuilding) = await host.SeedManagerAsync(); using var scope = host.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
        var tenant = new OfficeUser { UserName = "assistant-tenant@example.test", Email = "assistant-tenant@example.test", EmailConfirmed = true };
        var outsider = new OfficeUser { UserName = "assistant-outsider@example.test", Email = "assistant-outsider@example.test", EmailConfirmed = true };
        Assert.True((await users.CreateAsync(tenant, AccountTestHost.Password)).Succeeded); Assert.True((await users.CreateAsync(outsider, AccountTestHost.Password)).Succeeded);
        var secondBuilding = new Building { Name = "Second building" };
        var firstSuite = new Suite { BuildingId = firstBuilding, Name = "First confidential suite" }; var secondSuite = new Suite { BuildingId = secondBuilding.Id, Name = "Second confidential suite" };
        var first = Membership.Grant(firstSuite.Id, tenant.Id, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddHours(1));
        var second = Membership.Grant(secondSuite.Id, tenant.Id, DateTimeOffset.UtcNow.AddDays(-1));
        var foreign = Membership.Grant(firstSuite.Id, outsider.Id, DateTimeOffset.UtcNow.AddDays(-1));
        db.AddRange(secondBuilding, firstSuite, secondSuite, first, second, foreign,
            new ActiveGrant { MembershipId = first.Id, SuiteId = firstSuite.Id, UserId = tenant.Id },
            new ActiveGrant { MembershipId = second.Id, SuiteId = secondSuite.Id, UserId = tenant.Id },
            new ActiveGrant { MembershipId = foreign.Id, SuiteId = firstSuite.Id, UserId = outsider.Id });
        var ids = new List<Guid>();
        foreach (var building in new[] { firstBuilding, secondBuilding.Id })
        {
            var id = Guid.NewGuid(); var revision = HelpRevision.Create(id, "Entry guidance", "Private entry instructions for " + building, ArticleAudience.ActiveTenant, editor, new(2027, 1, 1), DateTimeOffset.UtcNow);
            var article = HelpArticle.Create(id, building, revision.Id); article.Publish(revision.Id); db.AddRange(article, revision); ids.Add(id);
        }
        await db.SaveChangesAsync(); return new(first, second, foreign, ids[0], ids[1], editor);
    }
    private sealed record Fixture(Membership First, Membership Second, Membership Foreign, Guid FirstArticle, Guid SecondArticle, string EditorId);
    private sealed class Model : IAssistantModel
    {
        public AssistantModelInput? Input { get; private set; }
        public int Calls { get; private set; }
        public Task<AssistantModelReply> AnswerAsync(AssistantModelInput input, CancellationToken cancellationToken)
        {
            Input = input; Calls++;
            return Task.FromResult(new AssistantModelReply(AssistantModelCode.Success, "generated-private-canary", input.Sources.Select(x => x.ArticleId).ToArray(), "new_request"));
        }
    }
}
