using System.Net;

namespace AllenBuilding.IntegrationTests;

public sealed class PreferencesHttpTests
{
    [Fact]
    public async Task Anonymous_theme_forms_require_csrf_and_persist_a_validated_choice_without_script()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync(); using var browser = host.Browser();
        using var forged = await browser.PostAsync("/preferences/theme", new FormUrlEncodedContent(new Dictionary<string, string> { ["value"] = "dark" })); Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode); Assert.False(forged.Headers.Contains("Set-Cookie"));
        using var saved = await AccountTestHost.PostAsync(browser, "/", "/preferences/theme", ("value", "dark"), ("returnUrl", "/Home/Privacy"));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode); Assert.Equal("/Home/Privacy", saved.Headers.Location!.OriginalString);
        var cookie = Assert.Single(saved.Headers.GetValues("Set-Cookie")); Assert.Contains("allen-office-theme=dark", cookie); Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("data-theme=\"dark\"", await browser.GetStringAsync("/Home/Privacy"));
        using var system = await AccountTestHost.PostAsync(browser, "/", "/preferences/theme", ("value", "system"), ("returnUrl", "https://untrusted.example/"));
        Assert.Equal(HttpStatusCode.Redirect, system.StatusCode); Assert.Equal("/", system.Headers.Location!.OriginalString);
        Assert.DoesNotContain("data-theme=\"dark\"", await browser.GetStringAsync("/"));
    }
    [Fact]
    public async Task Invalid_themes_and_get_requests_cannot_update_preference()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync(); using var browser = host.Browser();
        using var invalid = await AccountTestHost.PostAsync(browser, "/", "/preferences/theme", ("value", "<script>"), ("returnUrl", "/"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); Assert.False(invalid.Headers.Contains("Set-Cookie"));
        using var get = await browser.GetAsync("/preferences/theme?value=dark"); Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.DoesNotContain("data-theme=\"dark\"", await browser.GetStringAsync("/"));
    }
}
