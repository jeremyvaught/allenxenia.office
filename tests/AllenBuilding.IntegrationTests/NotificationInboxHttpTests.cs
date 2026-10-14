using System.Net;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AllenBuilding.IntegrationTests;

public sealed class NotificationInboxHttpTests
{
    [Fact]
    public async Task Old_unresolved_delivery_remains_reachable_after_more_than_one_hundred_successes()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync(); var (managerId, building) = await host.SeedManagerAsync();
        var old = new AccountMessage
        {
            UserId = managerId,
            BuildingId = building,
            Kind = "SecurityNotice",
            State = MessageState.NeedsReview,
            CreatedAt = host.Clock.GetUtcNow().AddDays(-2),
            ProtectedPayload = "never-render-this-payload",
            LastError = "DeliveryRejected"
        };
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); db.Add(old);
            for (var i = 0; i < 120; i++) db.Add(new AccountMessage
            {
                UserId = managerId,
                BuildingId = building,
                Kind = "SecurityNotice",
                State = MessageState.Delivered,
                CreatedAt = host.Clock.GetUtcNow().AddMinutes(-i),
                ProtectedPayload = "never-render-this-payload"
            });
            await db.SaveChangesAsync();
        }
        using var manager = host.Browser(); await host.LoginManagerAsync(manager, managerId);
        var html = await manager.GetStringAsync("/Notifications"); Assert.Contains(old.Id.ToString(), html);
        Assert.Contains("Needs attention", html); Assert.DoesNotContain("never-render-this-payload", html);
    }

    [Fact]
    public async Task All_unresolved_pages_are_reachable_independently_of_recent_delivery_history()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync(); var (managerId, building) = await host.SeedManagerAsync();
        var messages = Enumerable.Range(0, 28).Select(i => new AccountMessage
        {
            UserId = managerId,
            BuildingId = building,
            Kind = "SecurityNotice",
            State = MessageState.NeedsReview,
            CreatedAt = host.Clock.GetUtcNow().AddMinutes(-i),
            ProtectedPayload = "private"
        }).ToArray();
        using (var scope = host.Services.CreateScope()) { var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); db.AddRange(messages); await db.SaveChangesAsync(); }
        using var manager = host.Browser(); await host.LoginManagerAsync(manager, managerId);
        var first = await manager.GetStringAsync("/Notifications"); var second = await manager.GetStringAsync("/Notifications?reviewPage=2");
        Assert.Contains("reviewPage=2", first);
        foreach (var message in messages) Assert.True(first.Contains(message.Id.ToString()) ^ second.Contains(message.Id.ToString()));
        using var invalid = await manager.GetAsync("/Notifications?reviewPage=0"); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Tenant_reset_failure_is_scoped_metadata_with_fresh_link_recovery_and_no_arbitrary_retry()
    {
        await using var host = new AccountTestHost(); await host.InitializeAsync(); var (managerId, building) = await host.SeedManagerAsync();
        string tenantId; Guid memberId; Guid failedId; Guid version;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
            var tenant = new OfficeUser { Email = "reset-tenant@example.test", UserName = "reset-tenant@example.test", EmailConfirmed = true, DisplayName = "Reset tenant" };
            Assert.True((await users.CreateAsync(tenant, AccountTestHost.Password)).Succeeded); tenantId = tenant.Id;
            var suite = new Suite { BuildingId = building, Name = "Reset suite" }; var member = Membership.Grant(suite.Id, tenant.Id, host.Clock.GetUtcNow().AddDays(-1)); memberId = member.Id;
            db.AddRange(suite, member, new ActiveGrant { UserId = tenant.Id, SuiteId = suite.Id, MembershipId = member.Id }); await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<AccountInvitations>().QueueResetAsync(tenant.Email);
            var failed = await db.AccountMessages.SingleAsync(x => x.UserId == tenant.Id); failed.State = MessageState.NeedsReview;
            failed.CreatedAt = host.Clock.GetUtcNow().AddHours(-3); failed.LastError = "DeliveryRejected"; failedId = failed.Id; version = failed.LeaseToken;
            var outsider = new OfficeUser { Email = "unrelated@example.test", UserName = "unrelated@example.test", EmailConfirmed = true };
            Assert.True((await users.CreateAsync(outsider, AccountTestHost.Password)).Succeeded);
            db.Add(new AccountMessage { UserId = outsider.Id, Kind = "PasswordReset", State = MessageState.NeedsReview, CreatedAt = host.Clock.GetUtcNow(), ProtectedPayload = "secret-reset-token-canary" });
            await db.SaveChangesAsync();
        }
        using var manager = host.Browser(); await host.LoginManagerAsync(manager, managerId);
        var html = await manager.GetStringAsync("/Notifications"); Assert.Contains("reset-tenant@example.test", html);
        Assert.Contains("request a fresh password reset", html); Assert.DoesNotContain("unrelated@example.test", html);
        Assert.DoesNotContain("secret-reset-token-canary", html); Assert.DoesNotContain("Approve another delivery attempt", html);
        using var retry = await AccountTestHost.PostAsync(manager, "/Management", "/Notifications/Retry", ("Id", failedId.ToString()), ("Version", version.ToString()), ("Reason", "Forged retry"));
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            await scope.ServiceProvider.GetRequiredService<AccountInvitations>().QueueResetAsync("reset-tenant@example.test");
            Assert.Equal(MessageState.Suppressed, (await db.AccountMessages.AsNoTracking().SingleAsync(x => x.Id == failedId)).State);
            var pending = await db.AccountMessages.SingleAsync(x => x.UserId == tenantId && x.State == MessageState.Queued);
            pending.State = MessageState.NeedsReview; await db.SaveChangesAsync();
            db.ActiveGrants.Remove(await db.ActiveGrants.SingleAsync(x => x.MembershipId == memberId)); await db.SaveChangesAsync();
        }
        var revoked = await manager.GetStringAsync("/Notifications"); Assert.DoesNotContain("reset-tenant@example.test", revoked);
    }
}
