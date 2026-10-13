using System.Net;
using System.Text.RegularExpressions;
using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using AllenBuilding.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AllenBuilding.IntegrationTests;

public sealed class AutopayHttpTests
{
    private static readonly AutopayPolicy Policy = new(true, "acct_http_autopay", false);
    [Fact]
    public async Task Enrollment_and_stop_require_private_protected_explicit_confirmations_and_preserve_pending_provider_work()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync(); var fixture = await Seed(host);
        using var disabled = host.Browser(); await Login(disabled); using var off = await disabled.GetAsync("/Autopay?accountId=" + fixture.Account.Id); Assert.Equal(HttpStatusCode.NotFound, off.StatusCode);
        await using var enabled = host.WithWebHostBuilder(b => b.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton(Policy))));
        using var tenant = enabled.CreateClient(new() { AllowAutoRedirect = false }); await Login(tenant);
        var page = "/Autopay?accountId=" + fixture.Account.Id;
        using var foreign = await tenant.GetAsync("/Autopay?accountId=" + Guid.NewGuid()); Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        var review = await Prepare(tenant, page, fixture); var ticket = Ticket(review); Assert.Contains("Review monthly autopay", review); Assert.Contains("$1,000.00 USD", review); Assert.Contains("No initial rent payment", review); Assert.Contains("name=\"accepted\"", review); Assert.DoesNotContain("cus_", review);
        using (var scope = enabled.Services.CreateScope()) Assert.False(await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().AutopayAuthorizations.AnyAsync());
        using var noCsrf = await tenant.PostAsync("/Autopay/Confirm", new FormUrlEncodedContent(new Dictionary<string, string> { ["ticket"] = ticket, ["accepted"] = "true" })); Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        using var uncheckedConsent = await Confirm(tenant, ticket, false); Assert.Equal(HttpStatusCode.BadRequest, uncheckedConsent.StatusCode); Assert.Contains("no-store", uncheckedConsent.Headers.CacheControl!.ToString());
        using var tampered = await Confirm(tenant, ticket[..^8] + "changed"); Assert.Equal(HttpStatusCode.BadRequest, tampered.StatusCode);
        string expired; string wrong;
        using (var scope = enabled.Services.CreateScope())
        {
            var tickets = scope.ServiceProvider.GetRequiredService<AutopayReviewTickets>(); var value = tickets.Read(ticket, fixture.User.Id)!;
            expired = tickets.Create(value with { PreparedAt = host.Clock.GetUtcNow().AddMinutes(-6) }).Ticket; wrong = tickets.Create(value with { ActorId = "foreign-actor" }).Ticket;
        }
        using var old = await Confirm(tenant, expired); Assert.Equal(HttpStatusCode.BadRequest, old.StatusCode);
        using var another = await Confirm(tenant, wrong); Assert.Equal(HttpStatusCode.BadRequest, another.StatusCode);
        using var confirmed = await AccountTestHost.PostAsync(tenant, "/Portal", "/Autopay/Confirm", ("ticket", ticket), ("accepted", "true"), ("amount", "0.50"), ("accountId", Guid.NewGuid().ToString())); Assert.Equal(HttpStatusCode.Redirect, confirmed.StatusCode);
        using var replay = await Confirm(tenant, ticket); Assert.Equal(confirmed.Headers.Location, replay.Headers.Location);
        var pending = await tenant.GetStringAsync(page); Assert.Contains("Preparing payment-method setup", pending); Assert.DoesNotContain("Verified active", pending); Assert.Contains("provider verification are still pending", pending);
        Guid id;
        using (var scope = enabled.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); var auth = await db.AutopayAuthorizations.SingleAsync(); id = auth.Id; Assert.Equal(100000, auth.MonthlyMinorUnits); Assert.Equal(31, auth.DueDay);
            Assert.Equal(AutopayState.AwaitingMethod, auth.State); Assert.Empty(await db.Obligations.ToListAsync()); Assert.Empty(await db.SettlementPayments.ToListAsync()); Assert.Equal(1, await db.AutopayWork.CountAsync());
        }
        using var returned = await tenant.GetAsync("/Autopay/Return?authorizationId=" + id + "&paid=true&session_id=cs_fabricated"); Assert.Equal(HttpStatusCode.Redirect, returned.StatusCode);
        using var foreignReturn = await tenant.GetAsync("/Autopay/Return?authorizationId=" + Guid.NewGuid()); Assert.Equal(HttpStatusCode.NotFound, foreignReturn.StatusCode);
        using var continueGet = await tenant.GetAsync("/Autopay/Continue?id=" + id); Assert.Equal(HttpStatusCode.NotFound, continueGet.StatusCode); // Conventional MVC has no GET action for this name.
        using var noHandoffCsrf = await tenant.PostAsync("/Autopay/Continue", new FormUrlEncodedContent(new Dictionary<string, string> { ["id"] = id.ToString() })); Assert.Equal(HttpStatusCode.BadRequest, noHandoffCsrf.StatusCode);
        using var noGateway = await AccountTestHost.PostAsync(tenant, page, "/Autopay/Continue", ("id", id.ToString())); Assert.Equal(HttpStatusCode.Redirect, noGateway.StatusCode); Assert.Equal(returned.Headers.Location, noGateway.Headers.Location);
        using (var unchanged = enabled.Services.CreateScope())
        {
            var db = unchanged.ServiceProvider.GetRequiredService<OfficeDbContext>(); Assert.Equal(AutopayState.AwaitingMethod, (await db.AutopayAuthorizations.SingleAsync()).State); Assert.Equal(1, await db.AutopayWork.CountAsync()); Assert.False(await db.SettlementPayments.AnyAsync());
        }
        using var stopReview = await AccountTestHost.PostAsync(tenant, page, "/Autopay/PrepareStop", ("id", id.ToString()), ("timing", "Immediately")); Assert.Equal(HttpStatusCode.OK, stopReview.StatusCode); var stopHtml = await stopReview.Content.ReadAsStringAsync(); var stopTicket = Ticket(stopHtml);
        Assert.Contains("Existing unpaid rent remains owed", stopHtml);
        using var stopped = await Confirm(tenant, stopTicket); Assert.Equal(HttpStatusCode.Redirect, stopped.StatusCode);
        using var stopReplay = await Confirm(tenant, stopTicket); Assert.Equal(stopped.Headers.Location, stopReplay.Headers.Location);
        var stopping = await tenant.GetStringAsync(page); Assert.Contains("Stop requested", stopping); Assert.Contains("verification pending", stopping); Assert.DoesNotContain("Verified stopped", stopping); Assert.DoesNotContain("Review enrollment", stopping);
        using (var scope = enabled.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); Assert.Equal(AutopayState.StopPending, (await db.AutopayAuthorizations.SingleAsync()).State); Assert.Equal(2, await db.AutopayWork.CountAsync());
            var member = await db.Memberships.SingleAsync(); member.Revoke(fixture.User.Id, "Access revoked", host.Clock.GetUtcNow()); db.ActiveGrants.Remove(await db.ActiveGrants.SingleAsync()); await db.SaveChangesAsync();
        }
        using var denied = await tenant.GetAsync(page); Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        using var deniedReplay = await tenant.PostAsync("/Autopay/Confirm", Form(review, ticket)); Assert.Equal(HttpStatusCode.NotFound, deniedReplay.StatusCode);
    }
    [Fact]
    public async Task A_known_method_handoff_is_post_only_and_provider_return_cannot_activate_consent()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync(); var fixture = await Seed(host); var probe = new MethodProbe();
        await using var enabled = host.WithWebHostBuilder(b => b.ConfigureServices(s => { s.Replace(ServiceDescriptor.Singleton(Policy)); s.AddSingleton<IAutopayMethodGatewayFactory>(probe); }));
        Guid id;
        using (var scope = enabled.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); var now = DateTimeOffset.FromUnixTimeSeconds(host.Clock.GetUtcNow().ToUnixTimeSeconds());
            var binding = await db.BillingCustomers.SingleAsync(); var account = await db.RentAccounts.SingleAsync(); var terms = await db.RentAgreements.SingleAsync();
            var auth = AutopayAuthorization.Consent(Guid.NewGuid(), fixture.User.Id, account, binding, AutopaySchedule.Select(terms, now, []), true, now);
            var work = AutopayWork.Enqueue(auth, AutopayWorkStage.Setup, now, new("https://office.example.test", "pmc_method")); work.Claim(now); work.BeginDispatch(now);
            auth.ObserveVerifiedSetup(new(Policy.ProviderAccount, false, account.Id, binding.CustomerId, auth.ConsentHash, "cs_http_method", now.AddHours(24), AutopaySetupState.Waiting, null, true, false, now), false);
            work.Complete(work.LeaseToken, CollectionWorkOutcome.Waiting, now);
            db.AddRange(auth, work, new AutopaySlot { AccountId = account.Id, AuthorizationId = auth.Id }); await db.SaveChangesAsync(); id = auth.Id;
        }
        using var tenant = enabled.CreateClient(new() { AllowAutoRedirect = false }); await Login(tenant); var page = "/Autopay?accountId=" + fixture.Account.Id;
        var html = await tenant.GetStringAsync(page); Assert.Contains("Continue payment-method setup", html); Assert.DoesNotContain("cs_http_method", html); Assert.DoesNotContain("cus_", html);
        using var handoff = await AccountTestHost.PostAsync(tenant, page, "/Autopay/Continue", ("id", id.ToString())); Assert.Equal(HttpStatusCode.Redirect, handoff.StatusCode);
        Assert.Equal("https://checkout.stripe.com/c/setup/synthetic", handoff.Headers.Location!.AbsoluteUri); Assert.Contains("no-store", handoff.Headers.CacheControl!.ToString()); Assert.Equal(1, probe.Reads); Assert.True(probe.Disposed);
        using var returned = await tenant.GetAsync("/Autopay/Return?authorizationId=" + id + "&paid=true"); Assert.Equal(HttpStatusCode.Redirect, returned.StatusCode); Assert.Equal(1, probe.Reads);
        using var verify = enabled.Services.CreateScope(); var check = verify.ServiceProvider.GetRequiredService<OfficeDbContext>(); Assert.Equal(AutopayState.AwaitingMethod, (await check.AutopayAuthorizations.SingleAsync()).State); Assert.False(await check.SettlementPayments.AnyAsync()); Assert.Equal(1, await check.AutopayWork.CountAsync());
    }
    [Fact]
    public async Task Changed_terms_and_expired_consent_cannot_be_enrolled_from_a_previous_review()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync(); var fixture = await Seed(host);
        await using var enabled = host.WithWebHostBuilder(b => b.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton(Policy))));
        using var tenant = enabled.CreateClient(new() { AllowAutoRedirect = false }); await Login(tenant); var page = "/Autopay?accountId=" + fixture.Account.Id;
        var review = await Prepare(tenant, page, fixture); var ticket = Ticket(review);
        using (var scope = enabled.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); (await db.RentAccounts.SingleAsync()).TermsChanged(); await db.SaveChangesAsync();
        }
        using var stale = await Confirm(tenant, ticket); Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode); Assert.Contains("Review autopay again", await stale.Content.ReadAsStringAsync());
        using var verify = enabled.Services.CreateScope(); Assert.False(await verify.ServiceProvider.GetRequiredService<OfficeDbContext>().AutopayAuthorizations.AnyAsync());
    }
    private static async Task<string> Prepare(HttpClient client, string page, Fixture fixture)
    {
        using var prepared = await AccountTestHost.PostAsync(client, page, "/Autopay/Prepare", ("accountId", fixture.Account.Id.ToString()), ("agreementId", fixture.Terms.Id.ToString())); Assert.Equal(HttpStatusCode.OK, prepared.StatusCode); Assert.Contains("no-store", prepared.Headers.CacheControl!.ToString()); return await prepared.Content.ReadAsStringAsync();
    }
    private static Task<HttpResponseMessage> Confirm(HttpClient client, string ticket, bool accepted = true) => AccountTestHost.PostAsync(client, "/Portal", "/Autopay/Confirm", ("ticket", ticket), ("accepted", accepted.ToString()));
    private static string Ticket(string html) { var value = WebUtility.HtmlDecode(Regex.Match(html, "name=\"ticket\"[^>]*value=\"([^\"]+)\"").Groups[1].Value); Assert.NotEmpty(value); return value; }
    private static FormUrlEncodedContent Form(string html, string ticket) => new(new Dictionary<string, string> { ["ticket"] = ticket, ["accepted"] = "true", ["__RequestVerificationToken"] = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value) });
    private static async Task Login(HttpClient client) { using var result = await AccountTestHost.PostAsync(client, "/Account/Login", "/Account/Login", ("Email", "autopay@example.test"), ("Password", AccountTestHost.Password)); Assert.Equal("/Portal", result.Headers.Location?.OriginalString); }
    private sealed record Fixture(OfficeUser User, RentAccount Account, RentAgreement Terms);
    private sealed class MethodProbe : IAutopayMethodGatewayFactory, IAutopayMethodGateway, IDisposable
    {
        public int Reads { get; private set; }
        public bool Disposed { get; private set; }
        public IAutopayMethodGateway Open(AutopayMethodSnapshot original) => this;
        public Task<AutopayMethodHandoff?> ReadAsync(AutopayMethodSnapshot original, CancellationToken cancellationToken) { Reads++; return Task.FromResult<AutopayMethodHandoff?>(new(original.Authorization.SetupSessionId!, original.Authorization.SetupExpiresAt!.Value, new("https://checkout.stripe.com/c/setup/synthetic"))); }
        public void Dispose() => Disposed = true;
    }
    private static async Task<Fixture> Seed(AccountTestHost host)
    {
        using var scope = host.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>(); var now = host.Clock.GetUtcNow();
        var user = new OfficeUser { Email = "autopay@example.test", UserName = "autopay@example.test", EmailConfirmed = true }; Assert.True((await users.CreateAsync(user, AccountTestHost.Password)).Succeeded);
        var building = new Building { Name = "Autopay HTTP" }; var suite = new Suite { BuildingId = building.Id, Name = "Private autopay suite" }; var member = Membership.Grant(suite.Id, user.Id, now.AddDays(-1)); var account = RentAccount.Open(Guid.NewGuid(), member.Id);
        var binding = BillingCustomerBinding.Create(account.Id, Policy.ProviderAccount, false, "cus_http_autopay", now);
        var terms = RentAgreement.Create(account.Id, new Money(100000), 31, new DateOnly(2026, 1, 1), null, user.Id, now);
        db.AddRange(building, suite, member, account, binding, terms, new ActiveGrant { UserId = user.Id, SuiteId = suite.Id, MembershipId = member.Id }); await db.SaveChangesAsync(); return new(user, account, terms);
    }
}
