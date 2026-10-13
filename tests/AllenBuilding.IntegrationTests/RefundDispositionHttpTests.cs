using System.Net;
using System.Text.RegularExpressions;
using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Persistence;
using AllenBuilding.Web.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AllenBuilding.IntegrationTests;

public sealed class RefundDispositionHttpTests
{
    [Fact]
    public void Itemized_unicode_reviews_fit_the_bounded_protected_ticket()
    {
        var now = DateTimeOffset.UtcNow; var actor = "review-manager";
        var items = Enumerable.Range(0, 25).Select(_ => new ReviewedRefundItem(Guid.NewGuid(), Guid.NewGuid(), 1)).ToArray();
        var command = new ApplyRefundDisposition(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), RefundDisposition.CreditDebt, items, new string('界', 2000));
        var lines = items.Select(x => new BillingReviewLine(new string('界', 120), "$0.01 USD from " + x.ObligationId.ToString("D"))).ToArray();
        var tickets = new BillingReviewTickets(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), TimeProvider.System);
        var page = tickets.Create(new(actor, now, "Original account", "Review refund disposition", lines, Refund: command));
        Assert.InRange(page.Ticket.Length, 16385, 65536);
        var loaded = tickets.Read(page.Ticket, actor); Assert.NotNull(loaded); Assert.Equal(command.Reason, loaded.Refund!.Reason); Assert.Equal(25, loaded.Refund.Items.Count);
        Assert.Null(tickets.Read(page.Ticket, "another-manager")); Assert.Null(tickets.Read(new string('A', 65537), actor));
    }
    [Fact]
    public async Task Refund_confirmation_is_actor_bound_exact_replay_safe_and_preserves_the_hold()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync(); var (managerId, buildingId) = await host.SeedManagerAsync();
        var fixture = await Seed(host, managerId, buildingId); var route = "/FinancialExceptions/Refund/" + fixture.Command.MovementId;
        using var disabledManager = host.Browser(); await host.LoginManagerAsync(disabledManager, managerId);
        using var disabled = await disabledManager.GetAsync(route); Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);
        Assert.DoesNotContain("Review debt disposition", await disabledManager.GetStringAsync("/FinancialExceptions"));
        await using var enabled = host.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton(new RefundDispositionPolicy(true)))));
        using var manager = enabled.CreateClient(new() { AllowAutoRedirect = false }); await host.LoginManagerAsync(manager, managerId);
        using var guest = enabled.CreateClient(new() { AllowAutoRedirect = false }); using var denied = await guest.GetAsync(route); Assert.Contains("/Account/Login", denied.Headers.Location?.OriginalString);
        var html = await manager.GetStringAsync(route); Assert.Contains("no", (await manager.GetAsync(route)).Headers.CacheControl?.ToString());
        Assert.Contains("&lt;script&gt;original()&lt;/script&gt;", html); Assert.DoesNotContain("<script>original", html);
        var values = Form(fixture.Command);
        using var fractional = await AccountTestHost.PostAsync(manager, route, "/FinancialExceptions/PrepareRefund", values.Select(x => x.Key == "Items[0].Amount" ? (x.Key, "0.251") : x).ToArray()); Assert.Equal(HttpStatusCode.BadRequest, fractional.StatusCode);
        using var prepared = await AccountTestHost.PostAsync(manager, route, "/FinancialExceptions/PrepareRefund", values); Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);
        var reviewHtml = await prepared.Content.ReadAsStringAsync(); var ticket = Ticket(reviewHtml);
        Assert.Contains("$0.25 USD", reviewHtml); Assert.Contains("Reduce debt by the returned amount", reviewHtml); Assert.Contains("&lt;script&gt;reason()&lt;/script&gt;", reviewHtml);
        using (var scope = host.Services.CreateScope()) { var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); Assert.False(await db.RefundAllocations.AnyAsync()); Assert.False(await db.ObligationCredits.AnyAsync()); }
        using var noCsrf = await manager.PostAsync("/FinancialExceptions/ConfirmRefund", new FormUrlEncodedContent(new Dictionary<string, string> { ["Ticket"] = ticket })); Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        using var tampered = await Confirm(manager, ticket[..^10] + "tampered"); Assert.Equal(HttpStatusCode.BadRequest, tampered.StatusCode);
        string wrongActor; string expired;
        using (var scope = enabled.Services.CreateScope())
        {
            var tickets = scope.ServiceProvider.GetRequiredService<BillingReviewTickets>(); var review = tickets.Read(ticket, managerId)!;
            wrongActor = tickets.Create(review with { ActorId = "different-authorized-manager" }).Ticket;
            expired = tickets.Create(review with { PreparedAt = host.Clock.GetUtcNow().AddMinutes(-6) }).Ticket;
        }
        using var other = await Confirm(manager, wrongActor); Assert.Equal(HttpStatusCode.BadRequest, other.StatusCode);
        using var oldTicket = await Confirm(manager, expired); Assert.Equal(HttpStatusCode.BadRequest, oldTicket.StatusCode);
        using var saved = await AccountTestHost.PostAsync(manager, "/FinancialExceptions", "/FinancialExceptions/ConfirmRefund", ("Ticket", ticket), ("Amount", "99999.99"), ("Disposition", "RetainDebt"), ("MovementId", Guid.NewGuid().ToString()));
        Assert.Equal("/FinancialExceptions", saved.Headers.Location?.OriginalString);
        using var replay = await Confirm(manager, ticket); Assert.Equal(HttpStatusCode.Redirect, replay.StatusCode);
        html = await manager.GetStringAsync("/FinancialExceptions"); Assert.Contains("provider agreement pending", html); Assert.DoesNotContain("Review debt disposition", html);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); Assert.Equal(-25, (await db.RefundAllocations.SingleAsync()).SignedMinorUnits);
            Assert.Equal(25, (await db.ObligationCredits.SingleAsync()).AmountMinorUnits); Assert.Equal(1000, (await db.SettlementPayments.SingleAsync()).AmountMinorUnits);
            Assert.Equal(RefundDisposition.CreditDebt, (await db.CashMovementReviews.SingleAsync()).Disposition); Assert.Equal(CashMovementReviewState.AppliedPendingVerification, (await db.CashMovementReviews.SingleAsync()).State);
            Assert.NotEqual(FinancialExceptionState.Resolved, (await db.FinancialExceptions.SingleAsync()).State); Assert.Single(await db.FinancialExceptionHistory.Where(x => x.Action == "RefundDispositionApplied").ToListAsync());
            db.BuildingManagers.Remove(await db.BuildingManagers.SingleAsync()); await db.SaveChangesAsync();
        }
        var csrf = WebUtility.HtmlDecode(Regex.Match(reviewHtml, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        using var revokedReplay = await manager.PostAsync("/FinancialExceptions/ConfirmRefund", new FormUrlEncodedContent(new Dictionary<string, string> { ["Ticket"] = ticket, ["__RequestVerificationToken"] = csrf })); Assert.Contains("/Account/Denied", revokedReplay.Headers.Location?.OriginalString);
    }
    [Fact]
    public async Task Stale_items_and_expired_mfa_cannot_apply_a_prepared_refund()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync(); var (managerId, buildingId) = await host.SeedManagerAsync();
        var fixture = await Seed(host, managerId, buildingId); var route = "/FinancialExceptions/Refund/" + fixture.Command.MovementId;
        await using var enabled = host.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton(new RefundDispositionPolicy(true)))));
        using var manager = enabled.CreateClient(new() { AllowAutoRedirect = false }); await host.LoginManagerAsync(manager, managerId);
        using var prepared = await AccountTestHost.PostAsync(manager, route, "/FinancialExceptions/PrepareRefund", Form(fixture.Command)); var ticket = Ticket(await prepared.Content.ReadAsStringAsync());
        host.Clock.Offset = TimeSpan.FromMinutes(6);
        using var oldMfa = await Confirm(manager, ticket); Assert.Contains("/Account/Denied", oldMfa.Headers.Location?.OriginalString); host.Clock.Offset = TimeSpan.Zero;
        using (var scope = host.Services.CreateScope()) { var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); (await db.Obligations.SingleAsync()).FinancialStateChanged(); await db.SaveChangesAsync(); }
        using var stale = await Confirm(manager, ticket); Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using (var scope = host.Services.CreateScope()) { var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); Assert.False(await db.RefundAllocations.AnyAsync()); Assert.False(await db.ObligationCredits.AnyAsync()); }
    }
    private static Task<HttpResponseMessage> Confirm(HttpClient client, string ticket) => AccountTestHost.PostAsync(client, "/FinancialExceptions", "/FinancialExceptions/ConfirmRefund", ("Ticket", ticket));
    private static string Ticket(string html)
    { var ticket = WebUtility.HtmlDecode(Regex.Match(html, "name=\"Ticket\"[^>]*value=\"([^\"]+)\"").Groups[1].Value); Assert.NotEmpty(ticket); return ticket; }
    private static (string Key, string Value)[] Form(ApplyRefundDisposition command) =>
    [ ("OperationId", command.OperationId.ToString()), ("MovementId", command.MovementId.ToString()), ("ReviewVersion", command.ReviewVersion.ToString()),
      ("Items[0].ObligationId", command.Items[0].ObligationId.ToString()), ("Items[0].Version", command.Items[0].Version.ToString()), ("Items[0].Amount", "0.25"),
      ("Disposition", "CreditDebt"), ("Reason", "<script>reason()</script>") ];
    private static async Task<(ApplyRefundDisposition Command, Guid CaptureId)> Seed(AccountTestHost host, string actor, Guid building)
    {
        using var scope = host.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); var now = host.Clock.GetUtcNow();
        var suite = new Suite { BuildingId = building, Name = "Refund test office" }; var membership = Membership.Grant(suite.Id, actor, now.AddDays(-1)); membership.Revoke(actor, "Original tenancy ended", now);
        var account = RentAccount.Open(Guid.NewGuid(), membership.Id); var binding = BillingCustomerBinding.Create(account.Id, "acct_refund_http", false, "cus_refund_http", now);
        var debt = Obligation.Issue(account.Id, new Money(1000), "<script>original()</script>", new DateOnly(2026, 10, 1), actor, now);
        var attempt = PaymentAttempt.Admit(Guid.NewGuid(), actor, binding, [new(debt.Id, account.Id, debt.Version, 1000)], now); attempt.BeginDispatch(now);
        var capture = SettlementPayment.Record(attempt, new(binding.ProviderAccount, false, binding.CustomerId, "cs_refund_http", "pi_refund_http", 1000, "USD"), now);
        var movement = CashMovement.Record(capture, attempt, new(binding.ProviderAccount, false, account.Id, attempt.Id, binding.CustomerId, attempt.PaymentId!, "re_refund_http", "txn_refund_http", CashMovementKind.Refund, -25, "USD", now), now);
        var review = CashMovementReview.Open(movement); debt.FinancialStateChanged();
        db.AddRange(suite, membership, account, binding, debt, attempt, capture, movement, review, FinancialException.Open(attempt, FinancialFailure.ExternalCashMovement, "Paid", now)); await db.SaveChangesAsync();
        return (new(Guid.NewGuid(), account.Id, movement.Id, review.Version, RefundDisposition.CreditDebt, [new(debt.Id, debt.Version, 25)], "HTTP reviewed refund"), capture.Id);
    }
}
