using System.Net;

namespace AllenBuilding.IntegrationTests;

public sealed class HomeHttpTests
{
    [Fact]
    public async Task Public_page_is_a_sign_in_entry_and_signed_in_visitors_go_to_their_portal()
    {
        await using var host = new AccountTestHost();
        await host.InitializeAsync();
        var (managerId, _) = await host.SeedManagerAsync();
        using var client = host.Browser();

        var home = await client.GetStringAsync("/");
        Assert.Contains("Allen Building tenant portal", home);
        Assert.Contains("action=\"/Account/Login\"", home);
        Assert.Contains("href=\"/Maintenance/Create\"", home);
        Assert.Contains("href=\"/Knowledge\"", home);
        Assert.DoesNotContain("#services", home);

        using var wrong = await AccountTestHost.PostAsync(client, "/", "/Account/Login",
            ("Email", "manager@example.test"), ("Password", "not-the-password"));
        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        Assert.Contains("Unable to sign in.", await wrong.Content.ReadAsStringAsync());

        using var password = await AccountTestHost.PostAsync(client, "/", "/Account/Login",
            ("Email", "manager@example.test"), ("Password", AccountTestHost.Password));
        Assert.Equal("/Account/Factor", password.Headers.Location?.OriginalString);
        using var factor = await AccountTestHost.PostAsync(client, "/Account/Factor", "/Account/Factor",
            ("Code", await host.FactorCodeAsync(managerId)));
        Assert.Equal("/Portal", factor.Headers.Location?.OriginalString);

        using var signedIn = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Equal("/Portal", signedIn.Headers.Location?.OriginalString);
    }
}
