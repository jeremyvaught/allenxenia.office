using System.Net;
using AllenBuilding.Application.Assistant;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Web.Controllers;
using AllenBuilding.Web.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AllenBuilding.IntegrationTests;

public sealed class PilotAvailabilityTests
{
    [Fact]
    public async Task Signed_in_people_sharing_an_address_have_independent_assistant_turn_limits()
    {
        await using var host = new AccountTestHost { AssistantModel = new Model() }; await host.InitializeAsync();
        using var first = host.Browser(); using var second = host.Browser();
        await SignInPerson(host, first, "first@example.test"); await SignInPerson(host, second, "second@example.test");
        for (var turn = 0; turn < 5; turn++)
        {
            using var accepted = await AccountTestHost.PostAsync(first, "/Assistant", "/Assistant/Ask", ("question", "How do I find building help?"));
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }
        using var limited = await AccountTestHost.PostAsync(first, "/Assistant", "/Assistant/Ask", ("question", "One more question"));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        using var independent = await AccountTestHost.PostAsync(second, "/Assistant", "/Assistant/Ask", ("question", "How do I find building help?"));
        Assert.Equal(HttpStatusCode.OK, independent.StatusCode);
    }

    [Fact]
    public async Task One_signed_in_person_cannot_consume_another_persons_assistant_page_permits()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync(); var gate = new PageGate();
        await using var blocked = host.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Configure<MvcOptions>(options => options.Filters.Add(gate))));
        using var first = blocked.CreateClient(new() { AllowAutoRedirect = false }); using var other = blocked.CreateClient(new() { AllowAutoRedirect = false });
        await SignInPerson(host, first, "first@example.test"); await SignInPerson(host, other, "second@example.test");
        var one = first.GetAsync("/Assistant/Guest/" + Guid.NewGuid()); var two = first.GetAsync("/Assistant/Guest/" + Guid.NewGuid());
        Task<HttpResponseMessage>? independent = null;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var limited = await first.GetAsync("/Assistant/Guest/" + Guid.NewGuid()); Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            independent = other.GetAsync("/Assistant/Guest/" + Guid.NewGuid());
            await gate.ThirdEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { gate.Release.TrySetResult(); await Task.WhenAll(one, two); if (independent is not null) await independent; }
        Assert.Equal(HttpStatusCode.NotFound, (await independent!).StatusCode);
    }

    private static async Task SignInPerson(AccountTestHost host, HttpClient client, string email)
    {
        using var scope = host.Services.CreateScope(); var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
        Assert.True((await users.CreateAsync(new OfficeUser { Email = email, UserName = email, EmailConfirmed = true }, AccountTestHost.Password)).Succeeded);
        using var signedIn = await AccountTestHost.PostAsync(client, "/Account/Login", "/Account/Login", ("Email", email), ("Password", AccountTestHost.Password));
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
    }

    private sealed class Model : IAssistantModel
    {
        public Task<AssistantModelReply> AnswerAsync(AssistantModelInput input, CancellationToken cancellationToken) =>
            Task.FromResult(new AssistantModelReply(AssistantModelCode.Success, "Open building help.", [], "help"));
    }
    [Fact]
    public async Task Failed_passwords_block_new_sign_in_but_cannot_revoke_an_existing_manager_session()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync();
        var (id, _) = await host.SeedManagerAsync(); using var signedIn = host.Browser();
        await host.LoginManagerAsync(signedIn, id);
        using var attacker = host.Browser();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var failed = await AccountTestHost.PostAsync(attacker, "/Account/Login", "/Account/Login",
                ("Email", "manager@example.test"), ("Password", "Wrong-password-2026!"));
            Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        }
        using (var scope = host.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
            Assert.True(await users.IsLockedOutAsync((await users.FindByIdAsync(id))!));
        }
        using var current = await signedIn.GetAsync("/Management"); Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        using var locked = await AccountTestHost.PostAsync(attacker, "/Account/Login", "/Account/Login",
            ("Email", "manager@example.test"), ("Password", AccountTestHost.Password));
        Assert.Equal(HttpStatusCode.OK, locked.StatusCode); Assert.Null(locked.Headers.Location);
        using (var scope = host.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
            Assert.True((await users.UpdateSecurityStampAsync((await users.FindByIdAsync(id))!)).Succeeded);
        }
        using var revoked = await signedIn.GetAsync("/Management");
        Assert.Equal(HttpStatusCode.Redirect, revoked.StatusCode); Assert.Contains("/Account/Login", revoked.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Saturated_anonymous_assistant_pages_do_not_starve_database_readiness()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync();
        var gate = new PageGate();
        await using var blocked = host.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Configure<MvcOptions>(options => options.Filters.Add(gate))));
        using var browser = blocked.CreateClient(new() { AllowAutoRedirect = false });
        var first = browser.GetAsync("/Assistant/Guest/" + Guid.NewGuid()); var second = browser.GetAsync("/Assistant/Guest/" + Guid.NewGuid());
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var overflow = await browser.GetAsync("/Assistant/Guest/" + Guid.NewGuid()); Assert.Equal(HttpStatusCode.TooManyRequests, overflow.StatusCode);
            using var ready = await browser.GetAsync("/health/ready"); Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            using var live = await browser.GetAsync("/health/live"); Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        }
        finally { gate.Release.TrySetResult(); await Task.WhenAll(first, second); }
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void A_hosted_process_cannot_start_without_explicit_proxy_trust(string environment)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Proxy:Enabled"] = "false" });
        Assert.Throws<InvalidOperationException>(() => builder.AddOfficeProxy());
    }

    private sealed class PageGate : IAsyncActionFilter
    {
        private int _entered;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ThirdEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            if (context.Controller is AssistantController && context.ActionDescriptor.RouteValues["action"] == "Guest")
            {
                var count = Interlocked.Increment(ref _entered);
                if (count == 2) Entered.TrySetResult();
                if (count == 3) ThirdEntered.TrySetResult();
                await Release.Task.WaitAsync(context.HttpContext.RequestAborted);
            }
            await next();
        }
    }
}
