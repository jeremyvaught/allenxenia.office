using System.Net;
using AllenBuilding.Application.Assistant;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Domain.Knowledge;
using AllenBuilding.Domain.Maintenance;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AllenBuilding.IntegrationTests;

public sealed class AssistantTenantHttpTests
{
    [Theory]
    [InlineData("guest")]
    [InlineData("disabled")]
    public async Task Unregistered_model_drafts_never_create_pending_or_business_records(string mode)
    {
        var model = new Model { Draft = new(AssistantReviewKind.Request, "Unregistered draft", "Synthetic leak", "Hall") };
        await using var host = new AccountTestHost { AssistantModel = model, AssistantTenantHelpEnabled = true }; await host.InitializeAsync();
        var fixture = await Seed(host); using var browser = host.Browser(); if (mode == "disabled") await Login(browser);
        using var response = await AccountTestHost.PostAsync(browser, "/Assistant", "/Assistant/Ask", ("question", "A leak in the kitchen"),
            ("membershipId", mode == "guest" ? "" : fixture.First.Id.ToString()), ("conversationId", Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); Assert.False(model.Input!.CanPrepare);
        using var scope = host.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
        Assert.Empty(await db.WorkOrders.ToListAsync()); Assert.Empty(await db.AssistantMaintenanceReviews.ToListAsync());
    }

    [Fact]
    public async Task Model_request_draft_requires_separate_confirmation_and_returns_a_deterministic_receipt_without_another_model_call()
    {
        var model = new Model { Draft = new(AssistantReviewKind.Request, "Draft sink <script>", "Synthetic leak", "Kitchen") };
        await using var host = new AccountTestHost { AssistantModel = model, AssistantTenantHelpEnabled = true, AssistantActionsEnabled = true }; await host.InitializeAsync();
        var fixture = await Seed(host); using var browser = host.Browser(); await Login(browser);
        var path = await Prepare(browser, fixture.First.Id);
        using (var scope = host.Services.CreateScope())
        { var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); Assert.Empty(await db.WorkOrders.ToListAsync()); Assert.Single(await db.AssistantMaintenanceReviews.ToListAsync()); }
        var html = await browser.GetStringAsync(path); Assert.Contains("Draft sink &lt;script&gt;", html); Assert.Contains("Confirm and submit request", html);
        var values = ReviewFields(html); var calls = model.Calls;
        using var confirmed = await AccountTestHost.PostAsync(browser, path, "/Assistant/Confirm", values.Append(("title", "Tampered unreviewed title")).ToArray());
        Assert.Equal(HttpStatusCode.Redirect, confirmed.StatusCode);
        using var replay = await AccountTestHost.PostAsync(browser, path, "/Assistant/Confirm", values); Assert.Equal(HttpStatusCode.Redirect, replay.StatusCode);
        var receipt = await browser.GetStringAsync(path); Assert.Contains("Request submitted", receipt); Assert.DoesNotContain("Confirm and submit request", receipt);
        Assert.Equal(calls, model.Calls);
        using var check = host.Services.CreateScope(); var saved = check.ServiceProvider.GetRequiredService<OfficeDbContext>();
        Assert.Equal("Draft sink <script>", Assert.Single(await saved.WorkOrders.ToListAsync()).Title);
        Assert.Single(await saved.WorkOrderHistory.ToListAsync()); Assert.Single(await saved.MaintenanceReceipts.ToListAsync());
        Assert.Equal(AssistantReviewState.Completed, Assert.Single(await saved.AssistantMaintenanceReviews.ToListAsync()).State);
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("cancel")]
    [InlineData("expire")]
    [InlineData("origin")]
    public async Task Superseded_cancelled_expired_or_foreign_origin_reviews_do_not_submit(string mode)
    {
        var model = new Model { Draft = new(AssistantReviewKind.Request, "Draft leak", "Synthetic water", "Hall") };
        await using var host = new AccountTestHost { AssistantModel = model, AssistantTenantHelpEnabled = true, AssistantActionsEnabled = true }; await host.InitializeAsync();
        var fixture = await Seed(host); using var browser = host.Browser(); await Login(browser); var path = await Prepare(browser, fixture.First.Id);
        var values = ReviewFields(await browser.GetStringAsync(path));
        if (mode is "edit" or "cancel")
        {
            using var changed = await AccountTestHost.PostAsync(browser, path, "/Assistant/Edit", values.Where(x => x.Key != "operationId").Concat(new[]
            { ("title", "Revised issue"), ("description", "Revised details"), ("location", "Kitchen"), ("cancel", (mode == "cancel").ToString()) }).ToArray());
            Assert.Equal(HttpStatusCode.Redirect, changed.StatusCode);
            var revised = await browser.GetStringAsync(path); Assert.Contains(mode == "edit" ? "Revised issue" : "Draft cancelled", revised);
        }
        if (mode == "expire") { host.Clock.Offset = TimeSpan.FromMinutes(11); Assert.DoesNotContain("Confirm and submit request", await browser.GetStringAsync(path)); }
        if (mode == "origin") browser.DefaultRequestHeaders.Add("Origin", "https://foreign.test");
        using var response = await AccountTestHost.PostAsync(browser, path, "/Assistant/Confirm", values);
        Assert.Equal(mode == "origin" ? HttpStatusCode.BadRequest : HttpStatusCode.Conflict, response.StatusCode);
        using var scope = host.Services.CreateScope(); Assert.Empty(await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().WorkOrders.ToListAsync());
    }

    [Fact]
    public async Task Selected_request_comments_have_no_model_record_context_and_cannot_target_another_original_membership()
    {
        var model = new Model { Route = "requests", Draft = new(AssistantReviewKind.Comment, Comment: "Please call before arriving") };
        await using var host = new AccountTestHost { AssistantModel = model, AssistantTenantHelpEnabled = true, AssistantActionsEnabled = true }; await host.InitializeAsync();
        var fixture = await Seed(host); var records = await SeedRecords(host, fixture); using var browser = host.Browser(); await Login(browser);
        var page = $"/Assistant?membershipId={fixture.First.Id}&requestId={records.Request}";
        var form = await browser.GetStringAsync(page); Assert.Contains("Selected dripping tap", form); Assert.DoesNotContain("Other-membership-canary", form);
        using var response = await AccountTestHost.PostAsync(browser, page, "/Assistant/Ask", ("question", "Add a comment asking to call first"), ("membershipId", fixture.First.Id.ToString()), ("requestId", records.Request.ToString()), ("conversationId", Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); var path = ReviewPath(await response.Content.ReadAsStringAsync());
        Assert.True(model.Input!.CommentTarget); Assert.True(model.Input.CanPrepare);
        Assert.DoesNotContain(records.Request.ToString(), System.Text.Json.JsonSerializer.Serialize(model.Input));
        Assert.DoesNotContain("Selected dripping tap", System.Text.Json.JsonSerializer.Serialize(model.Input));
        var fields = ReviewFields(await browser.GetStringAsync(path)); using var confirm = await AccountTestHost.PostAsync(browser, path, "/Assistant/Confirm", fields); Assert.Equal(HttpStatusCode.Redirect, confirm.StatusCode);
        Assert.Contains("Comment added", await browser.GetStringAsync(path));
        using var foreign = await browser.GetAsync($"/Assistant?membershipId={fixture.Second.Id}&requestId={records.Request}"); Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        using var scope = host.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
        var history = Assert.Single(await db.WorkOrderHistory.ToListAsync()); Assert.Equal("Comment", history.Action); Assert.Equal(HistoryVisibility.TenantVisible, history.Visibility);
        Assert.Equal(WorkOrderStatus.Triaged, (await db.WorkOrders.SingleAsync(x => x.Id == records.Request)).Status);
    }

    [Fact]
    public async Task Review_routes_require_authentication_ownership_and_antiforgery_and_revoke_draft_before_output()
    {
        var model = new Model { Draft = new(AssistantReviewKind.Request, "Private draft", "Synthetic water", "Hall") }; AccountTestHost host = null!; Fixture fixture = null!;
        var revoke = false;
        host = new AccountTestHost
        {
            AssistantModel = model,
            AssistantTenantHelpEnabled = true,
            AssistantActionsEnabled = true,
            BeforeAssistantRelease = async () =>
        {
            if (!revoke) return;
            using var scope = host.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            (await db.Memberships.SingleAsync(x => x.Id == fixture.First.Id)).Revoke(fixture.EditorId, "Access ended", DateTimeOffset.UtcNow);
            db.ActiveGrants.Remove(await db.ActiveGrants.SingleAsync(x => x.MembershipId == fixture.First.Id)); await db.SaveChangesAsync();
        }
        };
        await using (host)
        {
            await host.InitializeAsync(); fixture = await Seed(host); using var browser = host.Browser(); await Login(browser);
            var path = await Prepare(browser, fixture.First.Id); var fields = ReviewFields(await browser.GetStringAsync(path));
            using var forged = await browser.PostAsync("/Assistant/Confirm", new FormUrlEncodedContent(fields.Select(x => new KeyValuePair<string, string>(x.Key, x.Value)))); Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
            using var wrongConversation = await browser.GetAsync(path.Replace(fields.Single(x => x.Key == "conversationId").Value, Guid.NewGuid().ToString())); Assert.Equal(HttpStatusCode.NotFound, wrongConversation.StatusCode);
            using var guest = host.Browser(); using var denied = await guest.GetAsync(path); Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
            revoke = true; using var changed = await browser.GetAsync(path); Assert.Equal(HttpStatusCode.NotFound, changed.StatusCode); Assert.DoesNotContain("Private draft", await changed.Content.ReadAsStringAsync());
            using var scope = host.Services.CreateScope(); Assert.Empty(await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().WorkOrders.ToListAsync());
        }
    }

    private static async Task<string> Prepare(HttpClient browser, Guid membership)
    {
        using var response = await AccountTestHost.PostAsync(browser, "/Assistant", "/Assistant/Ask", ("question", "A sink leaks in the kitchen. Water under the basin."),
            ("membershipId", membership.ToString()), ("conversationId", Guid.NewGuid().ToString())); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return ReviewPath(await response.Content.ReadAsStringAsync());
    }
    private static string ReviewPath(string html) => System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(html, "href=\"(/Assistant/Review/[^\"]+)\"").Groups[1].Value);
    private static (string Key, string Value)[] ReviewFields(string html) => new[] { "id", "conversationId", "version", "operationId" }.Select(name =>
        (name, WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(html, "name=\"" + name + "\"[^>]*value=\"([^\"]+)\"").Groups[1].Value))).ToArray();
    [Theory]
    [InlineData("requests")]
    [InlineData("billing")]
    public async Task Record_cards_use_the_selected_membership_and_never_send_record_values_to_the_model(string route)
    {
        var model = new Model { Route = route }; await using var host = new AccountTestHost { AssistantModel = model, AssistantTenantHelpEnabled = true }; await host.InitializeAsync();
        var fixture = await Seed(host); var records = await SeedRecords(host, fixture); using var browser = host.Browser(); await Login(browser);
        using var response = await AccountTestHost.PostAsync(browser, "/Assistant", "/Assistant/Ask", ("question", "Show my records"), ("membershipId", fixture.First.Id.ToString()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); var html = await response.Content.ReadAsStringAsync();
        Assert.Contains(route == "requests" ? "Selected dripping tap" : "Selected rent charge", html);
        Assert.DoesNotContain("Other-membership-canary", html); Assert.DoesNotContain("Second confidential suite", html);
        if (route == "requests") { Assert.Contains("Status: Triaged", html); Assert.Contains("/Maintenance/Details/" + records.Request, html); }
        else
        {
            Assert.Contains("Issued: $1,125.00 USD", html); Assert.Contains("Recorded credits: $125.00 USD", html);
            Assert.Contains("Charge after credits: $1,000.00 USD", html); Assert.Contains("not an outstanding balance", html);
            Assert.Contains("/Billing/Account/" + records.Account, html);
        }
        var providerInput = System.Text.Json.JsonSerializer.Serialize(model.Input);
        Assert.DoesNotContain("Selected dripping tap", providerInput); Assert.DoesNotContain("Selected rent charge", providerInput);
        Assert.DoesNotContain("112500", providerInput); Assert.DoesNotContain(records.Account.ToString(), providerInput);
        using var scope = host.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
        Assert.Equal(7, await db.WorkOrders.CountAsync()); Assert.Equal(2, await db.Obligations.CountAsync()); Assert.Empty(await db.WorkOrderHistory.ToListAsync());
    }

    [Fact]
    public async Task Revoked_membership_suppresses_record_cards_even_without_knowledge_sources()
    {
        var model = new Model { Route = "requests" }; AccountTestHost host = null!; Fixture fixture = null!;
        host = new AccountTestHost
        {
            AssistantModel = model,
            AssistantTenantHelpEnabled = true,
            BeforeAssistantRelease = async () =>
        {
            using var scope = host.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            (await db.Memberships.SingleAsync(x => x.Id == fixture.First.Id)).Revoke(fixture.EditorId, "Access ended", DateTimeOffset.UtcNow);
            db.ActiveGrants.Remove(await db.ActiveGrants.SingleAsync(x => x.MembershipId == fixture.First.Id)); await db.SaveChangesAsync();
        }
        };
        await using (host)
        {
            await host.InitializeAsync(); fixture = await Seed(host); await SeedRecords(host, fixture); using var browser = host.Browser(); await Login(browser);
            using var response = await AccountTestHost.PostAsync(browser, "/Assistant", "/Assistant/Ask", ("question", "records"), ("membershipId", fixture.First.Id.ToString()));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); Assert.Empty(model.Input!.Sources);
            Assert.DoesNotContain("Selected dripping tap", await response.Content.ReadAsStringAsync());
        }
    }

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
    private static async Task<(Guid Request, Guid Account)> SeedRecords(AccountTestHost host, Fixture fixture)
    {
        using var scope = host.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); var now = DateTimeOffset.UtcNow;
        var request = WorkOrder.Submit(fixture.First.SuiteId, fixture.First.Id, fixture.First.UserId, "Selected dripping tap", "Synthetic water leak", "Kitchen", now.AddDays(-1));
        request.Triage(Priority.Normal, Trade.Plumbing, now); db.Add(request);
        for (var i = 0; i < 6; i++) db.Add(WorkOrder.Submit(fixture.Second.SuiteId, fixture.Second.Id, fixture.Second.UserId, "Other-membership-canary " + i, "Synthetic description", "Elsewhere", now));
        var first = RentAccount.Open(Guid.NewGuid(), fixture.First.Id); var second = RentAccount.Open(Guid.NewGuid(), fixture.Second.Id);
        var charge = Obligation.Issue(first.Id, new Money(112500), "Selected rent charge", new(2026, 11, 1), fixture.EditorId, now);
        var other = Obligation.Issue(second.Id, new Money(99999), "Other-membership-canary charge", new(2026, 11, 1), fixture.EditorId, now);
        var credit = ObligationCredit.RecordLocal(Guid.NewGuid(), fixture.EditorId, new(charge.Id, first.Id, charge.Version, charge.AmountMinorUnits), 12500, ObligationCreditKind.Credit, "Synthetic credit", now);
        charge.FinancialStateChanged(); db.AddRange(first, second, charge, other, credit); await db.SaveChangesAsync(); return (request.Id, first.Id);
    }
    private sealed class Model : IAssistantModel
    {
        public AssistantDraft? Draft { get; init; }
        public string Route { get; init; } = "new_request";
        public AssistantModelInput? Input { get; private set; }
        public int Calls { get; private set; }
        public Task<AssistantModelReply> AnswerAsync(AssistantModelInput input, CancellationToken cancellationToken)
        {
            Input = input; Calls++;
            return Task.FromResult(new AssistantModelReply(AssistantModelCode.Success, "generated-private-canary", input.Sources.Select(x => x.ArticleId).ToArray(), Route, Draft: Draft));
        }
    }
}
