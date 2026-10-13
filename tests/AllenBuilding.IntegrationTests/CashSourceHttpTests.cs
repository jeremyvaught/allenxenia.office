using System.Net;
using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AllenBuilding.IntegrationTests;

public sealed class CashSourceHttpTests
{
    [Fact]
    public async Task Source_requests_are_gated_scoped_csrf_protected_recent_and_cannot_replace_original_references()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync(); var (managerId, buildingId) = await host.SeedManagerAsync(); FinancialException issue; PaymentAttempt original;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); var now = host.Clock.GetUtcNow();
            var suite = new Suite { BuildingId = buildingId, Name = "<script>source-office()</script>" }; var member = Membership.Grant(suite.Id, managerId, now.AddDays(-1)); member.Revoke(managerId, "Ended original tenancy", now);
            var account = RentAccount.Open(Guid.NewGuid(), member.Id); var binding = BillingCustomerBinding.Create(account.Id, "acct_source_http", false, "cus_source_http", now);
            var debt = Obligation.Issue(account.Id, new Money(1000), "Original debt", new DateOnly(2026, 10, 1), managerId, now);
            original = PaymentAttempt.Admit(Guid.NewGuid(), managerId, binding, [new(debt.Id, account.Id, debt.Version, 1000)], now); original.BeginDispatch(now);
            var capture = SettlementPayment.Record(original, new(binding.ProviderAccount, false, binding.CustomerId, "cs_source_http", "pi_source_http", 1000, "USD"), now);
            issue = FinancialException.Open(original, FinancialFailure.ExternalCashMovement, "Paid", now); db.AddRange(suite, member, account, binding, debt, original, capture, issue); await db.SaveChangesAsync();
        }
        var values = new[] { ("Id", issue.Id.ToString()), ("Version", issue.Version.ToString()), ("OperationId", Guid.NewGuid().ToString()), ("SourceId", "du_source_http"), ("Reason", "<script>reason()</script>"), ("PaymentId", "pi_substitute"), ("CustomerId", "cus_substitute") };
        using var disabled = host.Browser(); await host.LoginManagerAsync(disabled, managerId);
        Assert.DoesNotContain("Request cash-source verification", await disabled.GetStringAsync("/FinancialExceptions"));
        using var gated = await AccountTestHost.PostAsync(disabled, "/FinancialExceptions", "/FinancialExceptions/InvestigateSource", values); Assert.Equal(HttpStatusCode.NotFound, gated.StatusCode);
        await using var enabled = host.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton(new CashSourceRecoveryPolicy(true)))));
        using var manager = enabled.CreateClient(new() { AllowAutoRedirect = false }); await host.LoginManagerAsync(manager, managerId);
        using var guest = enabled.CreateClient(new() { AllowAutoRedirect = false }); using var denied = await guest.GetAsync("/FinancialExceptions"); Assert.Contains("/Account/Login", denied.Headers.Location?.OriginalString);
        var html = await manager.GetStringAsync("/FinancialExceptions"); Assert.Contains("Request cash-source verification", html); Assert.Contains("&lt;script&gt;source-office()&lt;/script&gt;", html);
        using var noCsrf = await manager.PostAsync("/FinancialExceptions/InvestigateSource", new FormUrlEncodedContent(values.Select(x => new KeyValuePair<string, string>(x.Item1, x.Item2)))); Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        using var badReference = await AccountTestHost.PostAsync(manager, "/FinancialExceptions", "/FinancialExceptions/InvestigateSource", values.Select(x => x.Item1 == "SourceId" ? (x.Item1, "https://untrusted.test") : x).ToArray()); Assert.Equal(HttpStatusCode.BadRequest, badReference.StatusCode);
        host.Clock.Offset = TimeSpan.FromMinutes(6);
        using var stale = await AccountTestHost.PostAsync(manager, "/FinancialExceptions", "/FinancialExceptions/InvestigateSource", values); Assert.Contains("/Account/Denied", stale.Headers.Location?.OriginalString); host.Clock.Offset = TimeSpan.Zero;
        using var requested = await AccountTestHost.PostAsync(manager, "/FinancialExceptions", "/FinancialExceptions/InvestigateSource", values); Assert.Equal("/FinancialExceptions", requested.Headers.Location?.OriginalString);
        using var replay = await AccountTestHost.PostAsync(manager, "/FinancialExceptions", "/FinancialExceptions/InvestigateSource", values); Assert.Equal(HttpStatusCode.Redirect, replay.StatusCode);
        html = await manager.GetStringAsync("/FinancialExceptions"); Assert.Contains("du_source_http", html); Assert.Contains("Verification queued", html); Assert.Contains("&lt;script&gt;reason()&lt;/script&gt;", html); Assert.DoesNotContain("<script>reason", html);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); var job = await db.CashSourceWork.SingleAsync(); var recorded = await db.PaymentAttempts.SingleAsync();
            Assert.Equal(original.Id, job.AttemptId); Assert.Equal("du_source_http", job.SourceId); Assert.Equal("pi_source_http", recorded.PaymentId); Assert.Equal("cus_source_http", recorded.CustomerId);
            Assert.False(await db.CashMovements.AnyAsync()); Assert.False(await db.ObligationCredits.AnyAsync()); Assert.Equal(1000, (await db.SettlementPayments.SingleAsync()).AmountMinorUnits);
            Assert.Equal(1, await db.FinancialExceptionHistory.CountAsync(x => x.Action == "CashReconcileRequested")); db.BuildingManagers.Remove(await db.BuildingManagers.SingleAsync()); await db.SaveChangesAsync();
        }
        using var revoked = await manager.GetAsync("/FinancialExceptions"); Assert.Contains("/Account/Denied", revoked.Headers.Location?.OriginalString);
    }
}
