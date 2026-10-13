using System.Net;
using AllenBuilding.Application.Access;
using AllenBuilding.Application.Billing;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AllenBuilding.IntegrationTests;

public sealed class FinancialExceptionHttpTests
{
    [Fact]
    public async Task Recovery_queue_is_private_escaped_csrf_protected_and_requires_recent_mfa_for_changes()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync();
        var (managerId, buildingId) = await host.SeedManagerAsync(); FinancialException issue;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            var suite = new Suite { BuildingId = buildingId, Name = "<script>suite()</script>" };
            var membership = Membership.Grant(suite.Id, managerId, host.Clock.GetUtcNow().AddYears(-1));
            membership.Revoke(managerId, "Access ended", host.Clock.GetUtcNow());
            var account = RentAccount.Open(Guid.NewGuid(), membership.Id);
            var binding = BillingCustomerBinding.Create(account.Id, "acct_http", false, "cus_http", host.Clock.GetUtcNow());
            var debt = Obligation.Issue(account.Id, new Money(1000), "Debt", new DateOnly(2026, 10, 1), managerId, host.Clock.GetUtcNow());
            var attempt = PaymentAttempt.Admit(Guid.NewGuid(), managerId, binding, [new(debt.Id, account.Id, debt.Version, 1000)], host.Clock.GetUtcNow());
            issue = FinancialException.Open(attempt, FinancialFailure.ProviderUnknown, null, host.Clock.GetUtcNow());
            db.AddRange(suite, membership, account, binding, debt, attempt, issue); await db.SaveChangesAsync();
        }
        using var guest = host.Browser(); using var manager = host.Browser();
        using var guestDenied = await guest.GetAsync("/FinancialExceptions"); Assert.Contains("/Account/Login", guestDenied.Headers.Location?.OriginalString);
        await host.LoginManagerAsync(manager, managerId);
        using var read = await manager.GetAsync("/FinancialExceptions"); var html = await read.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, read.StatusCode); Assert.Contains("no-store", read.Headers.CacheControl?.ToString());
        Assert.Contains("&lt;script&gt;suite()&lt;/script&gt;", html); Assert.DoesNotContain("<script>suite", html);
        var values = new[] { ("Id", issue.Id.ToString()), ("Version", issue.Version.ToString()), ("OperationId", Guid.NewGuid().ToString()), ("Reason", "<script>review()</script>"), ("OwnerUserId", managerId) };
        using var noToken = await manager.PostAsync("/FinancialExceptions/Assign", new FormUrlEncodedContent(values.Select(x => new KeyValuePair<string, string>(x.Item1, x.Item2))));
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        host.Clock.Offset = TimeSpan.FromMinutes(6);
        using var oldRead = await manager.GetAsync("/FinancialExceptions"); Assert.Equal(HttpStatusCode.OK, oldRead.StatusCode);
        using var expired = await AccountTestHost.PostAsync(manager, "/FinancialExceptions", "/FinancialExceptions/Assign", values);
        Assert.Contains("/Account/Denied", expired.Headers.Location?.OriginalString);
        host.Clock.Offset = TimeSpan.Zero;
        using var assigned = await AccountTestHost.PostAsync(manager, "/FinancialExceptions", "/FinancialExceptions/Assign", values);
        Assert.Equal("/FinancialExceptions", assigned.Headers.Location?.OriginalString);
        html = await manager.GetStringAsync("/FinancialExceptions"); Assert.Contains("&lt;script&gt;review()&lt;/script&gt;", html); Assert.DoesNotContain("<script>review", html);
        using var nonexistentMoneySetter = await manager.PostAsync("/FinancialExceptions/Resolve", new FormUrlEncodedContent([]));
        Assert.Contains(nonexistentMoneySetter.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            db.BuildingManagers.Remove(await db.BuildingManagers.SingleAsync(x => x.UserId == managerId)); await db.SaveChangesAsync();
        }
        using var ended = await manager.GetAsync("/FinancialExceptions"); Assert.Contains("/Account/Denied", ended.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Failed_queue_read_does_not_present_an_empty_queue_or_provider_diagnostics()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync(); var (managerId, _) = await host.SeedManagerAsync();
        await using var failing = host.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.Replace(ServiceDescriptor.Scoped<IFinancialExceptionStore, FailingStore>())));
        using var manager = failing.CreateClient(new() { AllowAutoRedirect = false }); await host.LoginManagerAsync(manager, managerId);
        using var read = await manager.GetAsync("/FinancialExceptions"); var html = await read.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, read.StatusCode); Assert.Contains("Retry the queue", html);
        Assert.DoesNotContain("synthetic-private-diagnostic", html); Assert.DoesNotContain("No financial exceptions", html);
    }
    private sealed class FailingStore : IFinancialExceptionStore
    {
        public Task<IReadOnlyList<FinancialCaseView>> ReadAsync(Actor actor, bool includeResolved, CancellationToken cancellationToken) => throw new OperationCanceledException("synthetic-private-diagnostic");
        public Task<BillingResult> ChangeAsync(Actor actor, FinancialCaseCommand command, string hash, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
