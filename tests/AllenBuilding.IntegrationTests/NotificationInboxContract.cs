using AllenBuilding.Application.Access;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Notifications;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AllenBuilding.IntegrationTests;

public static class NotificationInboxContract
{
    public static async Task VerifyAsync(Func<OfficeDbContext> create)
    {
        var protection = new EphemeralDataProtectionProvider(); var clock = TimeProvider.System;
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<IDataProtectionProvider>(protection);
        services.AddSingleton(clock); services.AddScoped<OfficeDbContext>(_ => create());
        services.AddIdentityCore<OfficeUser>().AddEntityFrameworkStores<OfficeDbContext>().AddDefaultTokenProviders();
        await using var provider = services.BuildServiceProvider();
        var manager = new OfficeUser { UserName = $"inbox-manager-{Guid.NewGuid():N}" };
        var tenant = new OfficeUser { UserName = $"inbox-tenant-{Guid.NewGuid():N}", Email = $"inbox-{Guid.NewGuid():N}@example.test", EmailConfirmed = true };
        tenant.NormalizedEmail = tenant.Email.ToUpperInvariant(); tenant.NormalizedUserName = tenant.UserName.ToUpperInvariant();
        var outsider = new OfficeUser { UserName = $"inbox-outsider-{Guid.NewGuid():N}", EmailConfirmed = true };
        var building = new Building { Name = "Inbox contract" }; var suite = new Suite { BuildingId = building.Id, Name = "Inbox suite" };
        var member = Membership.Grant(suite.Id, tenant.Id, clock.GetUtcNow().AddDays(-1));
        var failure = new AccountMessage
        {
            UserId = tenant.Id,
            Kind = "PasswordReset",
            State = MessageState.NeedsReview,
            CreatedAt = clock.GetUtcNow().AddHours(-3),
            ProtectedPayload = "never-render-token"
        };
        var actor = new Actor(manager.Id, new HashSet<Guid> { building.Id }, true, true);
        var current = new TestActor { Actor = actor };
        await using (var db = create())
        {
            db.AddRange(manager, tenant, outsider, building, suite, member,
                new ActiveGrant { UserId = tenant.Id, SuiteId = suite.Id, MembershipId = member.Id }, failure);
            db.Add(new AccountMessage { UserId = outsider.Id, Kind = "PasswordReset", State = MessageState.NeedsReview, CreatedAt = clock.GetUtcNow(), ProtectedPayload = "foreign" });
            for (var i = 0; i < 120; i++) db.Add(new AccountMessage { UserId = manager.Id, BuildingId = building.Id, Kind = "SecurityNotice", State = MessageState.Delivered, CreatedAt = clock.GetUtcNow().AddMinutes(-i), ProtectedPayload = "private" });
            await db.SaveChangesAsync();
            var page = await new NotificationInbox(db, clock).ReadAsync(actor, 1);
            Assert.Equal(failure.Id, Assert.Single(page.NeedsAttention).Id); Assert.Equal(tenant.Email, page.NeedsAttention[0].ResetRecipient);
            Assert.Equal(100, page.Recent.Count); Assert.False(page.HasNextPage);
            Assert.False(await new NotificationQueue(db, protection, clock, new Uri("https://example.test/"))
                .RetryAsync(current, failure.Id, failure.LeaseToken, "Do not replay an expired credential"));
        }
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
            await new AccountInvitations(db, users, current, protection, clock).QueueResetAsync(tenant.Email!);
            Assert.Equal(MessageState.Suppressed, (await db.AccountMessages.AsNoTracking().SingleAsync(x => x.Id == failure.Id)).State);
            Assert.Empty((await new NotificationInbox(db, clock).ReadAsync(actor, 1)).NeedsAttention);
            var fresh = await db.AccountMessages.SingleAsync(x => x.UserId == tenant.Id && x.State == MessageState.Queued);
            fresh.State = MessageState.NeedsReview; await db.SaveChangesAsync();
            Assert.Equal(fresh.Id, Assert.Single((await new NotificationInbox(db, clock).ReadAsync(actor, 1)).NeedsAttention).Id);
            db.ActiveGrants.Remove(await db.ActiveGrants.SingleAsync(x => x.MembershipId == member.Id)); await db.SaveChangesAsync();
            Assert.Empty((await new NotificationInbox(db, clock).ReadAsync(actor, 1)).NeedsAttention);
        }
    }
}
