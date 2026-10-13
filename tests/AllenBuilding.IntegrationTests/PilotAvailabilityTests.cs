using System.Net;
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
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            if (context.Controller is AssistantController && context.ActionDescriptor.RouteValues["action"] == "Guest")
            {
                if (Interlocked.Increment(ref _entered) == 2) Entered.TrySetResult();
                await Release.Task.WaitAsync(context.HttpContext.RequestAborted);
            }
            await next();
        }
    }
}
