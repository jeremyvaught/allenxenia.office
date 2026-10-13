using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace AllenBuilding.IntegrationTests;

public sealed class StatusPageTests
{
    [Fact]
    public async Task Invalid_browser_posts_keep_their_failure_status_without_echoing_the_submitted_body()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync(); using var browser = host.Browser();
        using var response = await browser.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["Email"] = "private-email-canary@example.test", ["Password"] = "private-password-canary", ["code"] = "123456" }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(); Assert.Contains("Building help", html);
        Assert.DoesNotContain("private-email-canary", html); Assert.DoesNotContain("private-password-canary", html);
    }
    [Theory]
    [InlineData("/missing-page?private-query-canary=do-not-display")]
    [InlineData("/Knowledge/Article/11111111-1111-1111-1111-111111111111")]
    public async Task Missing_pages_and_articles_keep_404_with_a_safe_themed_recovery_page(string path)
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync(); using var browser = host.Browser();
        using var response = await browser.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Page not found", html); Assert.Contains("/css/app.css", html);
        Assert.Contains("Theme", html); Assert.Contains("Building help", html);
        Assert.DoesNotContain("private-query-canary", html);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Failed_posts_render_once_without_replaying_actions_or_exposing_exception_details()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync();
        var failure = new Failure();
        await using var failing = host.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Configure<MvcOptions>(options => options.Filters.Add(failure))));
        using var browser = failing.CreateClient(new() { AllowAutoRedirect = false });
        using var response = await AccountTestHost.PostAsync(browser, "/", "/Home/Privacy", ("notes", "private-body-canary"));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Something went wrong", html); Assert.Contains("/css/app.css", html);
        Assert.DoesNotContain("private-error-canary", html); Assert.DoesNotContain("private-body-canary", html);
        Assert.Equal(1, failure.Calls);
    }

    private sealed class Failure : IAsyncResourceFilter
    {
        public int Calls { get; private set; }
        public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
        {
            if (context.ActionDescriptor.RouteValues["controller"] == "Home" && context.ActionDescriptor.RouteValues["action"] == "Privacy")
            { Calls++; throw new IOException("private-error-canary"); }
            await next();
        }
    }
}
