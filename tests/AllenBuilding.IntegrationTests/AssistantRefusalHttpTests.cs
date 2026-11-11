using System.Net;
using AllenBuilding.Application.Assistant;
using AllenBuilding.Domain.Maintenance;
using AllenBuilding.Infrastructure.Persistence;
using AllenBuilding.Web.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Guest = AllenBuilding.IntegrationTests.AssistantGuestHttpTests;
using Tenant = AllenBuilding.IntegrationTests.AssistantTenantHttpTests;

namespace AllenBuilding.IntegrationTests;

public sealed class AssistantRefusalHttpTests
{
    [Fact]
    public async Task A_turned_off_assistant_answers_a_posted_question_with_the_unavailable_page()
    {
        await using var host = new AccountTestHost();
        await host.InitializeAsync();
        using var browser = host.Browser();
        using var response = await AccountTestHost.PostAsync(browser, "/Account/Login", "/Assistant/Ask", ("question", "How do I report a repair?"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("The assistant is unavailable right now", html);
        Assert.DoesNotContain("data-assistant-form", html);
    }

    [Fact]
    public async Task Disabled_stages_refuse_owned_scopes_reviews_and_guest_drafts()
    {
        var model = new Model();
        await using var host = new AccountTestHost { AssistantModel = model };
        await host.InitializeAsync();
        var fixture = await Tenant.Seed(host);
        var records = await Tenant.SeedRecords(host, fixture);
        using var browser = host.Browser();
        await Tenant.Login(browser);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/Assistant/Access?membershipId=" + fixture.First.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync($"/Assistant?membershipId={fixture.First.Id}&requestId={records.Request}")).StatusCode);
        var id = Guid.NewGuid().ToString();
        var conversation = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync($"/Assistant/Review/{id}?conversationId={conversation}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/Assistant/Guest/" + id)).StatusCode);
        foreach (var action in new[] { "/Assistant/Confirm", "/Assistant/Edit", "/Assistant/EditGuest", "/Assistant/ImportGuest" })
        {
            using var response = await AccountTestHost.PostAsync(browser, "/Portal", action, ("id", id), ("conversationId", conversation),
                ("version", Guid.NewGuid().ToString()), ("operationId", Guid.NewGuid().ToString()), ("membershipId", fixture.First.Id.ToString()),
                ("title", "Disabled stage"), ("description", "Synthetic"), ("location", "Hall"));
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{action} returned {response.StatusCode}.");
        }
        Assert.Equal(0, model.Calls);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
        Assert.Empty(await db.AssistantMaintenanceReviews.ToListAsync());
        Assert.Empty(await db.WorkOrderHistory.ToListAsync());
        Assert.Empty(await db.MaintenanceReceipts.ToListAsync());
    }

    [Fact]
    public async Task Cross_site_invalid_and_out_of_sequence_turns_are_refused_before_the_model()
    {
        var model = new Model();
        await using var host = new AccountTestHost { AssistantModel = model, AssistantFollowUpsEnabled = true };
        await host.InitializeAsync();
        using var browser = host.Browser();
        browser.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");
        using (var crossSite = await AccountTestHost.PostAsync(browser, "/Assistant", "/Assistant/Ask", ("question", "entry")))
            Assert.Equal(HttpStatusCode.BadRequest, crossSite.StatusCode);
        browser.DefaultRequestHeaders.Remove("Sec-Fetch-Site");
        foreach (var question in new[] { "   ", new string('q', 4001) })
        {
            using var invalid = await AccountTestHost.PostAsync(browser, "/Assistant", "/Assistant/Ask", ("question", question));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Contains("Please ask a question of up to 4,000 characters", await invalid.Content.ReadAsStringAsync());
        }
        using var negative = await AccountTestHost.PostAsync(browser, "/Assistant", "/Assistant/Ask",
            ("question", "entry"), ("conversationId", Guid.NewGuid().ToString()), ("sequence", "-1"));
        Assert.Equal(HttpStatusCode.Conflict, negative.StatusCode);
        Assert.Contains("This conversation is busy, expired or no longer available", await negative.Content.ReadAsStringAsync());
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task An_exhausted_daily_budget_returns_the_limit_page_without_a_model_call()
    {
        var model = new Model();
        await using var host = new AccountTestHost { AssistantModel = model };
        await host.InitializeAsync();
        var budget = host.Services.GetRequiredService<AssistantBudget>();
        for (var index = 0; index < 100; index++)
            Assert.True(budget.TryAdmit("synthetic-partition-" + index));
        using var browser = host.Browser();
        using var limited = await AccountTestHost.PostAsync(browser, "/Assistant", "/Assistant/Ask", ("question", "entry"));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Contains("reached the assistant's current limit", await limited.Content.ReadAsStringAsync());
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task A_provider_timeout_shows_unavailable_and_does_not_consume_the_new_conversation()
    {
        var model = new Model { Fail = true };
        await using var host = new AccountTestHost { AssistantModel = model, AssistantFollowUpsEnabled = true };
        await host.InitializeAsync();
        using var browser = host.Browser();
        var conversation = Guid.NewGuid().ToString();
        using var timedOut = await AccountTestHost.PostAsync(browser, "/Assistant", "/Assistant/Ask", ("question", "entry"), ("conversationId", conversation));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, timedOut.StatusCode);
        var html = await timedOut.Content.ReadAsStringAsync();
        Assert.Contains("The assistant is unavailable right now", html);
        Assert.DoesNotContain(model.Answer, html);
        model.Fail = false;
        using var retried = await AccountTestHost.PostAsync(browser, "/Assistant", "/Assistant/Ask", ("question", "entry"), ("conversationId", conversation));
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Contains(model.Answer, await retried.Content.ReadAsStringAsync());
        Assert.Equal(2, model.Calls);
    }

    [Fact]
    public async Task A_conversation_that_expires_during_generation_discards_the_answer()
    {
        var model = new Model { Answer = "late-answer-canary" };
        await using var host = new AccountTestHost { AssistantModel = model, AssistantFollowUpsEnabled = true };
        model.BeforeAnswer = () => host.Clock.Offset = TimeSpan.FromMinutes(31);
        await host.InitializeAsync();
        using var browser = host.Browser();
        using var response = await AccountTestHost.PostAsync(browser, "/Assistant", "/Assistant/Ask", ("question", "entry"), ("conversationId", Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("late-answer-canary", html);
        Assert.DoesNotContain("name=\"sequence\" value=\"1\"", html);
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task Access_probes_are_bound_to_the_browser_membership_review_and_expiry()
    {
        var model = new Model { Draft = new(AssistantReviewKind.Request, "Probe draft", "Synthetic leak", "Hall") };
        await using var host = new AccountTestHost { AssistantModel = model, AssistantTenantHelpEnabled = true, AssistantActionsEnabled = true, AssistantFollowUpsEnabled = true };
        await host.InitializeAsync();
        var fixture = await Tenant.Seed(host);
        using var browser = host.Browser();
        await Tenant.Login(browser);
        var path = await Tenant.Prepare(browser, fixture.First.Id);
        var fields = Tenant.ReviewFields(await browser.GetStringAsync(path));
        var review = fields.Single(x => x.Key == "id").Value;
        var conversation = fields.Single(x => x.Key == "conversationId").Value;
        var reviewProbe = $"/Assistant/Access?membershipId={fixture.First.Id}&conversationId={conversation}&reviewId={review}";
        var conversationProbe = $"/Assistant/Access?membershipId={fixture.First.Id}&conversationId={conversation}";
        Assert.Equal(HttpStatusCode.NoContent, (await browser.GetAsync(reviewProbe)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await browser.GetAsync(conversationProbe)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(reviewProbe.Replace(fixture.First.Id.ToString(), fixture.Second.Id.ToString()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(reviewProbe.Replace(review, Guid.NewGuid().ToString()))).StatusCode);
        using var otherBrowser = host.Browser();
        await Tenant.Login(otherBrowser);
        using var foreign = await otherBrowser.GetAsync(conversationProbe);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        var foreignHtml = await foreign.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Probe draft", foreignHtml);
        Assert.DoesNotContain("First confidential suite", foreignHtml);
        host.Clock.Offset = TimeSpan.FromMinutes(31);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(conversationProbe)).StatusCode);
        using var scope = host.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().WorkOrders.ToListAsync());
    }

    [Fact]
    public async Task Review_edit_and_confirm_errors_keep_the_draft_and_nothing_executes_until_confirmation()
    {
        var model = new Model { Draft = new(AssistantReviewKind.Request, "Reviewed sink", "Synthetic leak", "Kitchen") };
        await using var host = new AccountTestHost { AssistantModel = model, AssistantTenantHelpEnabled = true, AssistantActionsEnabled = true };
        await host.InitializeAsync();
        var fixture = await Tenant.Seed(host);
        using var browser = host.Browser();
        await Tenant.Login(browser);
        var path = await Tenant.Prepare(browser, fixture.First.Id);
        var fields = Tenant.ReviewFields(await browser.GetStringAsync(path));
        var editFields = fields.Where(x => x.Key != "operationId").ToArray();
        var original = await Review(host);

        using var blank = await AccountTestHost.PostAsync(browser, path, "/Assistant/Edit", editFields.Concat(new[] { ("description", "Only details"), ("location", "Kitchen") }).ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Contains("Review needs attention", await blank.Content.ReadAsStringAsync());
        browser.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");
        using (var crossSite = await AccountTestHost.PostAsync(browser, path, "/Assistant/Edit", editFields.Append(("cancel", "true")).ToArray()))
            Assert.Equal(HttpStatusCode.BadRequest, crossSite.StatusCode);
        browser.DefaultRequestHeaders.Remove("Sec-Fetch-Site");
        using var unknownEdit = await AccountTestHost.PostAsync(browser, path, "/Assistant/Edit",
            editFields.Select(x => x.Key == "id" ? (x.Key, Guid.NewGuid().ToString()) : x).Append(("cancel", "true")).ToArray());
        Assert.Equal(HttpStatusCode.NotFound, unknownEdit.StatusCode);
        using var staleEdit = await AccountTestHost.PostAsync(browser, path, "/Assistant/Edit",
            editFields.Select(x => x.Key == "version" ? (x.Key, Guid.NewGuid().ToString()) : x).Append(("cancel", "true")).ToArray());
        Assert.Equal(HttpStatusCode.Conflict, staleEdit.StatusCode);
        using var unknownConfirm = await AccountTestHost.PostAsync(browser, path, "/Assistant/Confirm",
            fields.Select(x => x.Key == "id" ? (x.Key, Guid.NewGuid().ToString()) : x).ToArray());
        Assert.Equal(HttpStatusCode.NotFound, unknownConfirm.StatusCode);
        Assert.Contains("Review needs attention", await unknownConfirm.Content.ReadAsStringAsync());
        using var wrongOperation = await AccountTestHost.PostAsync(browser, path, "/Assistant/Confirm",
            fields.Select(x => x.Key == "operationId" ? (x.Key, Guid.NewGuid().ToString()) : x).ToArray());
        Assert.Equal(HttpStatusCode.Conflict, wrongOperation.StatusCode);

        var unchanged = await Review(host);
        Assert.Equal(original.Version, unchanged.Version);
        Assert.Equal("Reviewed sink", unchanged.Title);
        Assert.Equal(AssistantReviewState.Ready, unchanged.State);
        Assert.Contains("Confirm and submit request", await browser.GetStringAsync(path));
        using (var scope = host.Services.CreateScope())
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().WorkOrders.ToListAsync());

        using var confirmed = await AccountTestHost.PostAsync(browser, path, "/Assistant/Confirm", fields);
        Assert.Equal(HttpStatusCode.Redirect, confirmed.StatusCode);
        using var check = host.Services.CreateScope();
        Assert.Equal("Reviewed sink", Assert.Single(await check.ServiceProvider.GetRequiredService<OfficeDbContext>().WorkOrders.ToListAsync()).Title);
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task Guest_draft_errors_keep_the_original_text_and_an_imported_draft_continues_to_its_review()
    {
        var model = new Guest.Model();
        await using var host = Guest.Host(model);
        await host.InitializeAsync();
        var fixture = await Guest.Seed(host);
        using var browser = host.Browser();
        var path = await Guest.Stage(browser);
        var fields = Guest.Fields(await browser.GetStringAsync(path));
        using var unknown = await AccountTestHost.PostAsync(browser, path, "/Assistant/EditGuest",
            fields.Select(x => x.Key == "id" ? (x.Key, Guid.NewGuid().ToString()) : x).Concat(new[] { ("title", "Edited"), ("description", "Edited"), ("location", "Hall") }).ToArray());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var blank = await AccountTestHost.PostAsync(browser, path, "/Assistant/EditGuest", fields.Concat(new[] { ("title", " "), ("description", "Edited"), ("location", "Hall") }).ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        var current = await browser.GetStringAsync(path);
        Assert.Contains("Synthetic sink", current);
        Assert.Equal(fields, Guest.Fields(current));

        await Guest.Login(browser);
        browser.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");
        using (var crossSite = await AccountTestHost.PostAsync(browser, path, "/Assistant/ImportGuest", fields.Append(("membershipId", fixture.First.Id.ToString())).ToArray()))
            Assert.Equal(HttpStatusCode.BadRequest, crossSite.StatusCode);
        browser.DefaultRequestHeaders.Remove("Sec-Fetch-Site");
        using (var scope = host.Services.CreateScope())
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().AssistantMaintenanceReviews.ToListAsync());

        using var imported = await AccountTestHost.PostAsync(browser, path, "/Assistant/ImportGuest", fields.Append(("membershipId", fixture.First.Id.ToString())).ToArray());
        Assert.Equal(HttpStatusCode.Redirect, imported.StatusCode);
        var reviewPath = imported.Headers.Location!.OriginalString;
        using var resumed = await browser.GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, resumed.StatusCode);
        Assert.Equal(reviewPath, resumed.Headers.Location?.OriginalString);
        using var check = host.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<OfficeDbContext>();
        Assert.Equal(AssistantReviewState.Ready, Assert.Single(await db.AssistantMaintenanceReviews.ToListAsync()).State);
        Assert.Empty(await db.WorkOrders.ToListAsync());
    }

    private static async Task<AssistantMaintenanceReview> Review(AccountTestHost host)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().AssistantMaintenanceReviews.AsNoTracking().SingleAsync();
    }

    private sealed class Model : IAssistantModel
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public string Answer { get; set; } = "Synthetic guidance answer";
        public AssistantDraft? Draft { get; init; }
        public Action? BeforeAnswer { get; set; }
        public Task<AssistantModelReply> AnswerAsync(AssistantModelInput input, CancellationToken cancellationToken)
        {
            Calls++;
            BeforeAnswer?.Invoke();
            if (Fail)
                throw new OperationCanceledException("Synthetic provider timeout");
            return Task.FromResult(new AssistantModelReply(AssistantModelCode.Success, Answer, input.Sources.Select(x => x.ArticleId).ToArray(),
                Draft is null ? "help" : "new_request", Draft: Draft));
        }
    }
}
