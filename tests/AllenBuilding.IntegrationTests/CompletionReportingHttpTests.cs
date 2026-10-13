using System.Net;
using AllenBuilding.Application.Access;
using AllenBuilding.Application.Reporting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AllenBuilding.IntegrationTests;

public sealed class CompletionReportingHttpTests
{
    [Fact]
    public async Task Manager_challenge_scope_and_no_store_protect_mobile_completion_report()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync(); var manager = await host.SeedManagerAsync();
        using var anonymous = host.Browser(); using var denied = await anonymous.GetAsync("/Reports/Completion"); Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        using var browser = host.Browser(); await host.LoginManagerAsync(browser, manager.UserId);
        using var page = await browser.GetAsync("/Reports/Completion"); Assert.Equal(HttpStatusCode.OK, page.StatusCode); Assert.Contains("no-store", page.Headers.CacheControl!.ToString());
        var html = await page.Content.ReadAsStringAsync(); Assert.Contains("Maintenance completion times", html); Assert.Contains("No completed samples", html); Assert.Contains("nearest rank", html);
        using var foreign = await browser.GetAsync("/Reports/Completion?BuildingId=" + Guid.NewGuid()); Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        using var invalid = await browser.GetAsync("/Reports/Completion?BuildingId=invalid"); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
    [Fact]
    public async Task Database_failure_renders_retry_without_partial_report_values()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync(); var manager = await host.SeedManagerAsync();
        await using var failing = host.WithWebHostBuilder(b => b.ConfigureServices(s => s.Replace(ServiceDescriptor.Scoped<ICompletionReportReader, Failing>())));
        using var browser = failing.CreateClient(new() { AllowAutoRedirect = false }); await host.LoginManagerAsync(browser, manager.UserId);
        using var result = await browser.GetAsync("/Reports/Completion"); Assert.Equal(HttpStatusCode.ServiceUnavailable, result.StatusCode);
        var html = await result.Content.ReadAsStringAsync(); Assert.DoesNotContain("private-provider-detail", html); Assert.Contains("Completion", html);
    }
    private sealed class Failing : ICompletionReportReader
    {
        public Task<CompletionReport?> ReadAsync(Actor actor, CompletionQuery query, DateTimeOffset observedAt, CancellationToken token) => throw new TimeoutException("private-provider-detail");
    }
}
