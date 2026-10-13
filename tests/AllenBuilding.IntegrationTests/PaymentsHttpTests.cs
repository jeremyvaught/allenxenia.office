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

public sealed class PaymentsHttpTests
{
    private static readonly CollectionPolicy Policy = new(true, "acct_http_pay", false, new("https://payments.example.test", "pmc_http_pay"));
    [Fact]
    public async Task Tenant_payment_review_is_private_exact_confirmed_idempotent_and_return_is_not_a_receipt()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync(); var fixture = await Seed(host);
        using var disabled = host.Browser(); await Login(disabled); using var off = await disabled.GetAsync("/Payments?accountId=" + fixture.Account.Id); Assert.Equal(HttpStatusCode.NotFound, off.StatusCode);
        await using var enabled = host.WithWebHostBuilder(b => b.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton(Policy))));
        using var tenant = enabled.CreateClient(new() { AllowAutoRedirect = false }); await Login(tenant);
        using var guest = enabled.CreateClient(new() { AllowAutoRedirect = false }); using var denied = await guest.GetAsync("/Payments?accountId=" + fixture.Account.Id); Assert.Contains("/Account/Login", denied.Headers.Location?.OriginalString);
        var page = "/Payments?accountId=" + fixture.Account.Id;
        var choice = await tenant.GetStringAsync(page); Assert.Contains("&lt;script&gt;charge()&lt;/script&gt;", choice); Assert.DoesNotContain("<script>charge", choice);
        using var prepared = await AccountTestHost.PostAsync(tenant, page, "/Payments/Prepare", ("accountId", fixture.Account.Id.ToString()), ("operationId", Guid.NewGuid().ToString()), ("obligationIds", fixture.Debt.Id.ToString()), ("obligationIds", fixture.Small.Id.ToString()));
        Assert.Equal(HttpStatusCode.OK, prepared.StatusCode); Assert.Contains("no", prepared.Headers.CacheControl?.ToString()); var html = await prepared.Content.ReadAsStringAsync(); var ticket = Ticket(html);
        Assert.Contains("$10.25 USD", html); using (var scope = host.Services.CreateScope()) Assert.False(await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().PaymentAttempts.AnyAsync());
        using var noCsrf = await tenant.PostAsync("/Payments/Confirm", new FormUrlEncodedContent(new Dictionary<string, string> { ["ticket"] = ticket })); Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        using var forged = await Confirm(tenant, ticket[..^10] + "changed"); Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        string expired; string wrong;
        using (var scope = enabled.Services.CreateScope())
        {
            var tickets = scope.ServiceProvider.GetRequiredService<PaymentReviewTickets>(); var review = tickets.Read(ticket, fixture.User.Id)!;
            expired = tickets.Create(review with { PreparedAt = host.Clock.GetUtcNow().AddMinutes(-6) }).Ticket; wrong = tickets.Create(review with { ActorId = "another-payer" }).Ticket;
        }
        using var old = await Confirm(tenant, expired); Assert.Equal(HttpStatusCode.BadRequest, old.StatusCode);
        var recovery = await old.Content.ReadAsStringAsync(); Assert.Contains("Review the payment again", recovery); Assert.Contains("recent payment requests", recovery); Assert.Contains("View billing and payment requests", recovery); Assert.DoesNotContain("Private payment office", recovery); Assert.Contains("no-store", old.Headers.CacheControl!.ToString());
        using var other = await Confirm(tenant, wrong); Assert.Equal(HttpStatusCode.BadRequest, other.StatusCode);
        using var admitted = await AccountTestHost.PostAsync(tenant, page, "/Payments/Confirm", ("ticket", ticket), ("accountId", Guid.NewGuid().ToString()), ("amount", "100000.00"));
        Assert.Equal(HttpStatusCode.Redirect, admitted.StatusCode); var route = admitted.Headers.Location!.OriginalString;
        using var replay = await Confirm(tenant, ticket); Assert.Equal(route, replay.Headers.Location?.OriginalString);
        var beforeRead = host.Clock.GetUtcNow().AddSeconds(-1);
        var status = await tenant.GetStringAsync(route); Assert.Contains("Preparing checkout", status);
        var encodedRead = Regex.Match(status, @"App records read (\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}) UTC").Groups[1].Value;
        Assert.NotEmpty(encodedRead);
        var readAt = DateTimeOffset.ParseExact(encodedRead, "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);
        Assert.InRange(readAt, beforeRead, host.Clock.GetUtcNow()); Assert.Contains("$10.25 USD", status); Assert.DoesNotContain("cus_", status); Assert.DoesNotContain("acct_", status);
        Guid attemptId;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); var attempt = await db.PaymentAttempts.Include(x => x.Items).SingleAsync(); attemptId = attempt.Id;
            Assert.Equal(1025, attempt.TotalMinorUnits); Assert.Equal(2, attempt.Items.Count); Assert.Equal(2, await db.CollectionGuards.CountAsync()); Assert.False(await db.SettlementPayments.AnyAsync());
        }
        using var returned = await tenant.GetAsync("/Billing/Return?attemptId=" + attemptId + "&payment_status=paid"); Assert.Equal(route, returned.Headers.Location?.OriginalString);
        using (var scope = host.Services.CreateScope()) Assert.False(await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().SettlementPayments.AnyAsync());
        using var foreignStatus = await tenant.GetAsync("/Payments/Status/" + Guid.NewGuid()); Assert.Equal(HttpStatusCode.NotFound, foreignStatus.StatusCode);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); var member = await db.Memberships.SingleAsync(); member.Revoke(fixture.User.Id, "Access ended", host.Clock.GetUtcNow()); db.ActiveGrants.Remove(await db.ActiveGrants.SingleAsync()); await db.SaveChangesAsync();
        }
        using var revokedStatus = await tenant.GetAsync(route); Assert.Equal(HttpStatusCode.NotFound, revokedStatus.StatusCode);
        using var revokedReplay = await tenant.PostAsync("/Payments/Confirm", Form(html, ticket)); Assert.Equal(HttpStatusCode.NotFound, revokedReplay.StatusCode);
    }
    [Fact]
    public async Task Small_remainder_alone_and_stale_review_cannot_create_a_payment()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync(); var fixture = await Seed(host);
        await using var enabled = host.WithWebHostBuilder(b => b.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton(Policy))));
        using var tenant = enabled.CreateClient(new() { AllowAutoRedirect = false }); await Login(tenant); var page = "/Payments?accountId=" + fixture.Account.Id;
        using var tooSmall = await AccountTestHost.PostAsync(tenant, page, "/Payments/Prepare", ("accountId", fixture.Account.Id.ToString()), ("operationId", Guid.NewGuid().ToString()), ("obligationIds", fixture.Small.Id.ToString())); Assert.Equal(HttpStatusCode.BadRequest, tooSmall.StatusCode); Assert.Contains("Choose an eligible payment", await tooSmall.Content.ReadAsStringAsync());
        using var prepared = await AccountTestHost.PostAsync(tenant, page, "/Payments/Prepare", ("accountId", fixture.Account.Id.ToString()), ("operationId", Guid.NewGuid().ToString()), ("obligationIds", fixture.Debt.Id.ToString())); var ticket = Ticket(await prepared.Content.ReadAsStringAsync());
        using (var scope = host.Services.CreateScope()) { var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); (await db.Obligations.SingleAsync(x => x.Id == fixture.Debt.Id)).FinancialStateChanged(); await db.SaveChangesAsync(); }
        using var stale = await Confirm(tenant, ticket); Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode); Assert.Contains("Check the current balance", await stale.Content.ReadAsStringAsync());
        using var verify = host.Services.CreateScope(); Assert.False(await verify.ServiceProvider.GetRequiredService<OfficeDbContext>().PaymentAttempts.AnyAsync());
    }
    private static Task<HttpResponseMessage> Confirm(HttpClient client, string ticket) => AccountTestHost.PostAsync(client, "/Portal", "/Payments/Confirm", ("ticket", ticket));
    private static FormUrlEncodedContent Form(string html, string ticket) => new(new Dictionary<string, string> { ["ticket"] = ticket, ["__RequestVerificationToken"] = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value) });
    private static string Ticket(string html) { var value = WebUtility.HtmlDecode(Regex.Match(html, "name=\"ticket\"[^>]*value=\"([^\"]+)\"").Groups[1].Value); Assert.NotEmpty(value); return value; }
    private static async Task Login(HttpClient client) { using var result = await AccountTestHost.PostAsync(client, "/Account/Login", "/Account/Login", ("Email", "payer@example.test"), ("Password", AccountTestHost.Password)); Assert.Equal("/Portal", result.Headers.Location?.OriginalString); }
    private static async Task<(OfficeUser User, RentAccount Account, Obligation Debt, Obligation Small)> Seed(AccountTestHost host)
    {
        using var scope = host.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>(); var now = host.Clock.GetUtcNow();
        var user = new OfficeUser { Email = "payer@example.test", UserName = "payer@example.test", EmailConfirmed = true }; Assert.True((await users.CreateAsync(user, AccountTestHost.Password)).Succeeded);
        var building = new Building { Name = "Payment HTTP" }; var suite = new Suite { BuildingId = building.Id, Name = "Private payment office" }; var member = Membership.Grant(suite.Id, user.Id, now.AddDays(-1)); var account = RentAccount.Open(Guid.NewGuid(), member.Id);
        var binding = BillingCustomerBinding.Create(account.Id, Policy.ProviderAccount, false, "cus_http_pay", now);
        var debt = Obligation.Issue(account.Id, new Money(1000), "<script>charge()</script>", new DateOnly(2026, 10, 1), user.Id, now); var small = Obligation.Issue(account.Id, new Money(1000), "Carried small balance", new DateOnly(2026, 9, 1), user.Id, now);
        var credit = ObligationCredit.RecordLocal(Guid.NewGuid(), user.Id, new(small.Id, account.Id, small.Version, 1000), 975, ObligationCreditKind.Credit, "Approved earlier credit", now); small.FinancialStateChanged();
        db.AddRange(building, suite, member, account, binding, debt, small, credit, new ActiveGrant { UserId = user.Id, SuiteId = suite.Id, MembershipId = member.Id }); await db.SaveChangesAsync(); return (user, account, debt, small);
    }
}
