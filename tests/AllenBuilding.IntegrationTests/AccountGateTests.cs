using System.Net;
using System.Text.RegularExpressions;
using AllenBuilding.Application.Access;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AllenBuilding.IntegrationTests;

// Rejected, expired and stale account-flow outcomes that must not grant authority.
public sealed class AccountGateTests
{
    private const string Email = "manager@example.test";

    [Fact]
    public async Task Invalid_login_form_is_redisplayed_without_signing_in_or_issuing_a_session()
    {
        await using var host = new AccountTestHost();
        await host.InitializeAsync();
        await host.SeedManagerAsync();
        using var browser = host.Browser();
        using var rejected = await AccountTestHost.PostAsync(browser, "/Account/Login?ReturnUrl=%2FManagement", "/Account/Login",
            ("Email", "not-an-email"), ("Password", AccountTestHost.Password), ("ReturnUrl", "/Management"));
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        Assert.Contains("Welcome back", await rejected.Content.ReadAsStringAsync());
        Assert.DoesNotContain(SetCookies(rejected), x => x.StartsWith("AllenOffice.Session=", StringComparison.Ordinal));
        using var portal = await browser.GetAsync("/Portal");
        Assert.Contains("/Account/Login", portal.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Unenrolled_manager_asking_for_enrollment_is_sent_there_instead_of_the_portal()
    {
        await using var host = new AccountTestHost();
        await host.InitializeAsync();
        await host.SeedManagerAsync(enrolled: false);
        using var browser = host.Browser();
        using var signedIn = await AccountTestHost.PostAsync(browser, "/Account/Login", "/Account/Login",
            ("Email", Email), ("Password", AccountTestHost.Password), ("ReturnUrl", "/Account/Enroll"));
        Assert.Equal("/Account/Enroll", signedIn.Headers.Location?.OriginalString);
        var page = await browser.GetStringAsync("/Account/Enroll");
        Assert.Contains("Protect your account", page);
        Assert.Matches("<code[^>]*>[A-Z2-7]{16,}</code>", page);
    }

    [Fact]
    public async Task Factor_post_without_a_password_challenge_returns_to_sign_in_with_the_safe_destination()
    {
        await using var host = new AccountTestHost();
        await host.InitializeAsync();
        var (id, _) = await host.SeedManagerAsync();
        using var browser = host.Browser();
        using var restart = await AccountTestHost.PostAsync(browser, "/Account/Login", "/Account/Factor",
            ("Code", await host.FactorCodeAsync(id)), ("ReturnUrl", "/Maintenance/Manage"));
        Assert.Equal(HttpStatusCode.Redirect, restart.StatusCode);
        Assert.Equal("/Account/Login?returnUrl=%2FMaintenance%2FManage", restart.Headers.Location?.OriginalString);
        using var unsafeRestart = await AccountTestHost.PostAsync(browser, "/Account/Login", "/Account/Factor",
            ("Code", await host.FactorCodeAsync(id)), ("ReturnUrl", "//foreign.test"));
        Assert.Equal("/Account/Login", unsafeRestart.Headers.Location?.OriginalString);
        using var management = await browser.GetAsync("/Management");
        Assert.Contains("/Account/Login", management.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Expired_password_challenge_rejects_an_otherwise_valid_factor_code()
    {
        await using var host = new AccountTestHost();
        await host.InitializeAsync();
        var (id, _) = await host.SeedManagerAsync();
        using var browser = host.Browser();
        using var password = await AccountTestHost.PostAsync(browser, "/Account/Login", "/Account/Login", ("Email", Email), ("Password", AccountTestHost.Password));
        Assert.Equal("/Account/Factor", password.Headers.Location?.OriginalString);
        var html = await browser.GetStringAsync("/Account/Factor");
        host.Clock.Offset = TimeSpan.FromMinutes(6);
        using var expired = await Post(browser, html, "/Account/Factor", ("Code", await host.FactorCodeAsync(id)));
        Assert.Equal("/Account/Login", expired.Headers.Location?.OriginalString);
        using var management = await browser.GetAsync("/Management");
        Assert.Contains("/Account/Login", management.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Blank_factor_code_redisplays_the_challenge_and_spaced_codes_are_accepted()
    {
        await using var host = new AccountTestHost();
        await host.InitializeAsync();
        var (id, _) = await host.SeedManagerAsync();
        using var browser = host.Browser();
        await AccountTestHost.PostAsync(browser, "/Account/Login", "/Account/Login", ("Email", Email), ("Password", AccountTestHost.Password));
        using var blank = await AccountTestHost.PostAsync(browser, "/Account/Factor", "/Account/Factor", ("Code", ""));
        Assert.Equal(HttpStatusCode.OK, blank.StatusCode);
        Assert.Contains("One more step", await blank.Content.ReadAsStringAsync());
        using var stillChallenged = await browser.GetAsync("/Management");
        Assert.Equal(HttpStatusCode.Redirect, stillChallenged.StatusCode);
        var code = await host.FactorCodeAsync(id);
        using var spaced = await AccountTestHost.PostAsync(browser, "/Account/Factor", "/Account/Factor", ("Code", code[..3] + " - " + code[3..]));
        Assert.Equal("/Portal", spaced.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/Management")).StatusCode);
    }

    [Fact]
    public async Task Wrong_recovery_code_is_rejected_without_consuming_codes_or_auditing_a_recovery()
    {
        await using var host = new AccountTestHost();
        await host.InitializeAsync();
        var (id, _) = await host.SeedManagerAsync();
        using (var scope = host.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
            Assert.NotNull(await users.GenerateNewTwoFactorRecoveryCodesAsync((await users.FindByIdAsync(id))!, 2));
        }
        using var browser = host.Browser();
        await AccountTestHost.PostAsync(browser, "/Account/Login", "/Account/Login", ("Email", Email), ("Password", AccountTestHost.Password));
        using var wrong = await AccountTestHost.PostAsync(browser, "/Account/Factor", "/Account/Factor", ("Code", "WRONG-RECOVERY"), ("Recovery", "true"));
        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        Assert.Contains("Unable to verify this code", await wrong.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/Management")).StatusCode);
        using var check = host.Services.CreateScope();
        var checkUsers = check.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
        Assert.Equal(2, await checkUsers.CountRecoveryCodesAsync((await checkUsers.FindByIdAsync(id))!));
        Assert.Empty(await check.ServiceProvider.GetRequiredService<OfficeDbContext>().AccountAudits.Where(x => x.Action == "RecoveryCodeUsed").ToListAsync());
    }

    [Fact]
    public async Task Enrolled_manager_cannot_reopen_or_resubmit_enrollment_to_replace_the_authenticator()
    {
        await using var host = new AccountTestHost();
        await host.InitializeAsync();
        var (id, _) = await host.SeedManagerAsync();
        using var browser = host.Browser();
        await host.LoginManagerAsync(browser, id);
        string original;
        using (var scope = host.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
            original = (await users.GetAuthenticatorKeyAsync((await users.FindByIdAsync(id))!))!;
        }
        using var page = await browser.GetAsync("/Account/Enroll");
        Assert.Equal("/Account/Login", page.Headers.Location?.OriginalString);
        using var resubmit = await AccountTestHost.PostAsync(browser, "/Portal", "/Account/Enroll", ("Code", await host.FactorCodeAsync(id)));
        Assert.Equal("/Account/Login", resubmit.Headers.Location?.OriginalString);
        using var check = host.Services.CreateScope();
        var checkUsers = check.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
        Assert.Equal(original, await checkUsers.GetAuthenticatorKeyAsync((await checkUsers.FindByIdAsync(id))!));
        Assert.Equal(0, await checkUsers.CountRecoveryCodesAsync((await checkUsers.FindByIdAsync(id))!));
    }

    [Fact]
    public async Task Enrollment_page_with_a_stale_password_requires_signing_in_again_without_showing_a_key()
    {
        await using var host = new AccountTestHost();
        await host.InitializeAsync();
        await host.SeedManagerAsync(enrolled: false);
        using var browser = host.Browser();
        await AccountTestHost.PostAsync(browser, "/Account/Login", "/Account/Login", ("Email", Email), ("Password", AccountTestHost.Password));
        host.Clock.Offset = TimeSpan.FromMinutes(11);
        using var stale = await browser.GetAsync("/Account/Enroll");
        Assert.Equal("/Account/Login?returnUrl=%2FAccount%2FEnroll", stale.Headers.Location?.OriginalString);
        Assert.DoesNotContain("Protect your account", await stale.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Operator_enrollment_code_rejects_wrong_or_malformed_codes_and_expires_its_session()
    {
        await using var host = new AccountTestHost();
        await host.InitializeAsync();
        var (id, _) = await host.SeedManagerAsync();
        var code = new string('C', 32);
        using (var scope = host.Services.CreateScope())
        {
            var services = scope.ServiceProvider;
            var users = services.GetRequiredService<UserManager<OfficeUser>>();
            var user = (await users.FindByIdAsync(id))!;
            Assert.Equal(AccessCode.Success, await new OperatorMfaRecovery(services.GetRequiredService<OfficeDbContext>(), users,
                services.GetRequiredService<AccountInvitations>(), new(true, Environment.UserName)).ExecuteAsync(new(id, user.SecurityStamp!, "synthetic-verification", "Synthetic lost device", code)));
        }
        using var browser = host.Browser();
        await AccountTestHost.PostAsync(browser, "/Account/Login", "/Account/Login", ("Email", Email), ("Password", AccountTestHost.Password));
        var wrongCode = new string('D', 32);
        using var wrong = await AccountTestHost.PostAsync(browser, "/Account/Enroll", "/Account/AuthorizeEnrollment", ("Code", wrongCode));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        var wrongPage = await wrong.Content.ReadAsStringAsync();
        Assert.Contains("Unable to verify this recovery code", wrongPage);
        Assert.DoesNotContain(wrongCode, wrongPage);
        using var malformed = await AccountTestHost.PostAsync(browser, "/Account/Enroll", "/Account/AuthorizeEnrollment", ("Code", "short"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Contains("Unable to verify this recovery code", await malformed.Content.ReadAsStringAsync());
        using var accepted = await AccountTestHost.PostAsync(browser, "/Account/Enroll", "/Account/AuthorizeEnrollment", ("Code", code));
        Assert.Equal("/Account/Enroll", accepted.Headers.Location?.OriginalString);
        Assert.Contains("Protect your account", await browser.GetStringAsync("/Account/Enroll"));
        var enrollPage = await browser.GetStringAsync("/Account/Enroll");
        host.Clock.Offset = TimeSpan.FromMinutes(11);
        Assert.Contains("Verify your recovery", await browser.GetStringAsync("/Account/Enroll"));
        using var expired = await Post(browser, enrollPage, "/Account/Enroll", ("Code", await host.FactorCodeAsync(id)));
        Assert.Equal(HttpStatusCode.Forbidden, expired.StatusCode);
        using var check = host.Services.CreateScope();
        Assert.False((await check.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>().FindByIdAsync(id))!.TwoFactorEnabled);
    }

    [Fact]
    public async Task Recovery_code_review_requires_a_factor_challenged_session()
    {
        await using var host = new AccountTestHost();
        await host.InitializeAsync();
        var (id, _) = await host.SeedManagerAsync(enrolled: false);
        using var browser = host.Browser();
        await AccountTestHost.PostAsync(browser, "/Account/Login", "/Account/Login", ("Email", Email), ("Password", AccountTestHost.Password));
        using var review = await browser.GetAsync("/Account/RecoveryCodes");
        Assert.StartsWith("http://localhost/Account/Denied", review.Headers.Location?.OriginalString);
        using var replace = await AccountTestHost.PostAsync(browser, "/Portal", "/Account/RecoveryCodes", ("Password", AccountTestHost.Password), ("Code", "123456"));
        Assert.StartsWith("http://localhost/Account/Denied", replace.Headers.Location?.OriginalString);
        using var check = host.Services.CreateScope();
        var users = check.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
        Assert.Equal(0, await users.CountRecoveryCodesAsync((await users.FindByIdAsync(id))!));
        Assert.Empty(await check.ServiceProvider.GetRequiredService<OfficeDbContext>().AccountAudits.Where(x => x.Action == "RecoveryCodesReplaced").ToListAsync());
    }

    [Fact]
    public async Task Recovery_code_replacement_rejects_a_wrong_password_or_code_and_counts_the_failed_factor()
    {
        await using var host = new AccountTestHost();
        await host.InitializeAsync();
        var (id, _) = await host.SeedManagerAsync();
        using var browser = host.Browser();
        await host.LoginManagerAsync(browser, id);
        using var wrongPassword = await AccountTestHost.PostAsync(browser, "/Account/RecoveryCodes", "/Account/RecoveryCodes", ("Password", "Wrong-synthetic-2026!"), ("Code", await host.FactorCodeAsync(id)));
        Assert.Equal(HttpStatusCode.OK, wrongPassword.StatusCode);
        Assert.Contains("Unable to verify your password and code", await wrongPassword.Content.ReadAsStringAsync());
        using var wrongCode = await AccountTestHost.PostAsync(browser, "/Account/RecoveryCodes", "/Account/RecoveryCodes", ("Password", AccountTestHost.Password), ("Code", "000000"));
        Assert.Equal(HttpStatusCode.OK, wrongCode.StatusCode);
        Assert.Contains("Unable to verify your password and code", await wrongCode.Content.ReadAsStringAsync());
        using var check = host.Services.CreateScope();
        var users = check.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
        var user = (await users.FindByIdAsync(id))!;
        Assert.Equal(0, await users.CountRecoveryCodesAsync(user));
        Assert.Equal(2, user.AccessFailedCount);
    }

    [Fact]
    public async Task Authenticator_reset_is_refused_outside_a_fresh_recovery_session_or_with_a_wrong_password()
    {
        await using var host = new AccountTestHost();
        await host.InitializeAsync();
        var (id, _) = await host.SeedManagerAsync();
        string recovery;
        using (var scope = host.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
            recovery = (await users.GenerateNewTwoFactorRecoveryCodesAsync((await users.FindByIdAsync(id))!, 1))!.Single();
        }
        using var normal = host.Browser();
        await host.LoginManagerAsync(normal, id);
        using var notRecovery = await AccountTestHost.PostAsync(normal, "/Account/RecoveryCodes", "/Account/RecoverAuthenticator", ("Password", AccountTestHost.Password));
        Assert.StartsWith("http://localhost/Account/Denied", notRecovery.Headers.Location?.OriginalString);

        using var browser = host.Browser();
        await AccountTestHost.PostAsync(browser, "/Account/Login", "/Account/Login", ("Email", Email), ("Password", AccountTestHost.Password));
        using var recovered = await AccountTestHost.PostAsync(browser, "/Account/Factor", "/Account/Factor", ("Code", recovery), ("Recovery", "true"));
        Assert.Equal("/Account/RecoveryCodes", recovered.Headers.Location?.OriginalString);
        var review = await browser.GetStringAsync("/Account/RecoveryCodes");
        Assert.Contains("Lost your authenticator?", review);
        using var wrongPassword = await Post(browser, review, "/Account/RecoverAuthenticator", ("Password", "Wrong-synthetic-2026!"));
        Assert.Equal(HttpStatusCode.OK, wrongPassword.StatusCode);
        Assert.Contains("Unable to verify your password.", await wrongPassword.Content.ReadAsStringAsync());
        host.Clock.Offset = TimeSpan.FromMinutes(6);
        using var stale = await Post(browser, review, "/Account/RecoverAuthenticator", ("Password", AccountTestHost.Password));
        Assert.StartsWith("http://localhost/Account/Denied", stale.Headers.Location?.OriginalString);
        using var check = host.Services.CreateScope();
        var checkUsers = check.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
        Assert.True((await checkUsers.FindByIdAsync(id))!.TwoFactorEnabled);
        Assert.Empty(await check.ServiceProvider.GetRequiredService<OfficeDbContext>().AccountAudits.Where(x => x.Action == "AuthenticatorRecovered").ToListAsync());
    }

    [Fact]
    public async Task Denied_page_offers_reverification_only_to_a_challenged_manager_whose_factor_is_stale()
    {
        await using var host = new AccountTestHost();
        await host.InitializeAsync();
        var (id, building) = await host.SeedManagerAsync();
        using var anonymous = host.Browser();
        var guest = await anonymous.GetStringAsync("/Account/Denied");
        Assert.Contains("You do not have access to this area", guest);
        Assert.DoesNotContain("/Account/Reauthenticate", guest);

        using var browser = host.Browser();
        await host.LoginManagerAsync(browser, id);
        var recent = await browser.GetStringAsync("/Account/Denied");
        Assert.Contains("You do not have access to this area", recent);
        Assert.DoesNotContain("/Account/Reauthenticate", recent);

        host.Clock.Offset = TimeSpan.FromMinutes(6);
        using var sensitive = await AccountTestHost.PostAsync(browser, "/Management", "/Management/AddSuite", ("BuildingId", building.ToString()), ("Name", "Reverified suite"));
        Assert.StartsWith("http://localhost/Account/Denied", sensitive.Headers.Location?.OriginalString);
        var stale = await browser.GetStringAsync(sensitive.Headers.Location!);
        Assert.Contains("This action requires a recent verified sign-in.", stale);
        Assert.Contains("href=\"/Account/Reauthenticate\"", stale);
    }

    [Fact]
    public async Task Reauthentication_rejects_a_wrong_code_and_a_correct_one_restores_sensitive_actions()
    {
        await using var host = new AccountTestHost();
        await host.InitializeAsync();
        var (id, building) = await host.SeedManagerAsync();
        using var browser = host.Browser();
        await host.LoginManagerAsync(browser, id);
        host.Clock.Offset = TimeSpan.FromMinutes(6);
        using var wrong = await AccountTestHost.PostAsync(browser, "/Account/Reauthenticate", "/Account/Reauthenticate", ("Password", AccountTestHost.Password), ("Code", "000000"));
        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        Assert.Contains("Unable to verify your password and code", await wrong.Content.ReadAsStringAsync());
        using var stillStale = await AccountTestHost.PostAsync(browser, "/Management", "/Management/AddSuite", ("BuildingId", building.ToString()), ("Name", "Stale suite"));
        Assert.StartsWith("http://localhost/Account/Denied", stillStale.Headers.Location?.OriginalString);
        using var verified = await AccountTestHost.PostAsync(browser, "/Account/Reauthenticate", "/Account/Reauthenticate", ("Password", AccountTestHost.Password), ("Code", await host.FactorCodeAsync(id)));
        Assert.Equal("/Portal", verified.Headers.Location?.OriginalString);
        using var allowed = await AccountTestHost.PostAsync(browser, "/Management", "/Management/AddSuite", ("BuildingId", building.ToString()), ("Name", "Reverified suite"));
        Assert.Equal(HttpStatusCode.Redirect, allowed.StatusCode);
        Assert.DoesNotContain("/Account/Denied", allowed.Headers.Location?.OriginalString);
        using var scope = host.Services.CreateScope();
        Assert.Equal("Reverified suite", (await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().Suites.SingleAsync()).Name);
    }

    [Fact]
    public async Task Reauthentication_is_refused_for_a_password_only_account()
    {
        await using var host = new AccountTestHost();
        await host.InitializeAsync();
        var (id, _) = await host.SeedManagerAsync(enrolled: false);
        using var browser = host.Browser();
        await AccountTestHost.PostAsync(browser, "/Account/Login", "/Account/Login", ("Email", Email), ("Password", AccountTestHost.Password));
        using var refused = await AccountTestHost.PostAsync(browser, "/Account/Reauthenticate", "/Account/Reauthenticate", ("Password", AccountTestHost.Password), ("Code", "123456"));
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        Assert.Contains("Unable to verify your password and code", await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/Management")).StatusCode);
        using var check = host.Services.CreateScope();
        Assert.False((await check.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>().FindByIdAsync(id))!.TwoFactorEnabled);
    }

    private static IEnumerable<string> SetCookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [];

    private static Task<HttpResponseMessage> Post(HttpClient client, string formHtml, string action, params (string Key, string Value)[] values)
    {
        var token = Regex.Match(formHtml, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);
        return client.PostAsync(action, new FormUrlEncodedContent(values.Select(x => new KeyValuePair<string, string>(x.Key, x.Value))
            .Append(new("__RequestVerificationToken", WebUtility.HtmlDecode(token)))));
    }
}
