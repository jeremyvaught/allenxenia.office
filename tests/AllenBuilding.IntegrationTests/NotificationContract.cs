using AllenBuilding.Application.Access;
using AllenBuilding.Application.Notifications;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Notifications;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.IntegrationTests;

public static class NotificationContract
{
    public static async Task VerifyAsync(Func<OfficeDbContext> create)
    {
        var protection = new EphemeralDataProtectionProvider();
        var protector = protection.CreateProtector("AllenOffice.AccountMessage.v1");
        var clock = new ManualClock(DateTimeOffset.UtcNow.AddYears(-1));
        var user = new OfficeUser { UserName = $"outbox-{Guid.NewGuid():N}@example.test", Email = $"outbox-{Guid.NewGuid():N}@example.test", EmailConfirmed = true };
        var building = new Building { Name = "Outbox test" };
        await using (var db = create()) { db.AddRange(user, building, new BuildingManager { UserId = user.Id, BuildingId = building.Id }); await db.SaveChangesAsync(); }
        NotificationQueue Queue(OfficeDbContext db) => new(db, protection, clock, new Uri("http://localhost:5081/"));
        async Task<Guid> Add()
        {
            await using var db = create();
            var message = new AccountMessage { UserId = user.Id, BuildingId = building.Id, Kind = "SecurityNotice", ProtectedPayload = protector.Protect("Synthetic security notice"), CreatedAt = clock.GetUtcNow(), NextAttemptAt = clock.GetUtcNow() };
            db.AccountMessages.Add(message); await db.SaveChangesAsync(); return message.Id;
        }
        async Task<MessageLease?> Claim() { await using var db = create(); return await Queue(db).ClaimAsync(default); }
        async Task<bool> Complete(MessageLease lease, DeliveryResult result) { await using var db = create(); return await Queue(db).CompleteAsync(lease, result, default); }
        var id = await Add(); var first = (await Claim())!; Assert.Equal(id, first.Id); Assert.Null(await Claim());
        clock.Advance(TimeSpan.FromMinutes(3)); var replacement = (await Claim())!;
        Assert.NotEqual(first.Token, replacement.Token);
        Assert.False(await Complete(first, new(DeliveryOutcome.Delivered)));
        Assert.True(await Complete(replacement, new(DeliveryOutcome.Delivered)));
        var retryId = await Add(); MessageLease last = null!;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            last = (await Claim())!; Assert.Equal(retryId, last.Id);
            Assert.True(await Complete(last, new(DeliveryOutcome.Retryable, "private-provider-key")));
            Assert.Null(await Claim()); clock.Advance(TimeSpan.FromMinutes(9));
        }
        await using (var db = create())
        {
            var saved = await db.AccountMessages.SingleAsync(x => x.Id == retryId);
            Assert.Equal(MessageState.NeedsReview, saved.State); Assert.Equal(5, saved.Attempts);
            Assert.Equal("DeliveryRetryable", saved.LastError);
            var denied = new TestActor { Actor = new Actor(user.Id, new HashSet<Guid> { building.Id }, true, false) };
            Assert.False(await Queue(db).RetryAsync(denied, retryId, last.Token, "Configuration checked"));
        }
        await using (var db = create())
        {
            var manager = new TestActor { Actor = new Actor(user.Id, new HashSet<Guid> { building.Id }, true, true) };
            Assert.True(await Queue(db).RetryAsync(manager, retryId, last.Token, "Configuration checked"));
            Assert.False(await Queue(db).RetryAsync(manager, retryId, last.Token, "Duplicate retry"));
        }
        var retried = (await Claim())!; Assert.Equal(retryId, retried.Id);
        Assert.True(await Complete(retried, new(DeliveryOutcome.Delivered)));
        var unknownId = await Add();
        await using (var db = create()) Assert.True(await new NotificationDispatcher(Queue(db), new ThrowingTransport()).ProcessOneAsync());
        await using (var db = create())
        {
            var saved = await db.AccountMessages.SingleAsync(x => x.Id == unknownId);
            Assert.Equal(MessageState.NeedsReview, saved.State); Assert.Equal("DeliveryUnknown", saved.LastError);
        }
        var deliveredId = await Add(); var directory = Path.Combine(Path.GetTempPath(), $"office-mail-check-{Guid.NewGuid():N}");
        try
        {
            var transport = new LocalInboxTransport(directory);
            await using (var db = create()) Assert.True(await new NotificationDispatcher(Queue(db), transport).ProcessOneAsync());
            var path = Path.Combine(directory, $"{deliveredId:D}.json"); Assert.True(File.Exists(path));
            var envelope = System.Text.Json.JsonSerializer.Deserialize<EmailEnvelope>(await File.ReadAllTextAsync(path))!;
            Assert.Equal(DeliveryOutcome.Delivered, (await transport.SendAsync(envelope, default)).Outcome);
            Assert.Single(Directory.GetFiles(directory));
            Assert.Equal(DeliveryOutcome.PermanentFailure, (await transport.SendAsync(envelope with { Text = "Changed content" }, default)).Outcome);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }

        // Delivery admission rechecks the exact original grant, even if a later grant exists.
        var suite = new Suite { BuildingId = building.Id, Name = "Notification suite" };
        var tenant = new OfficeUser { UserName = $"ended-{Guid.NewGuid():N}@example.test", Email = "ended@example.test", EmailConfirmed = true };
        var membership = Membership.Grant(suite.Id, tenant.Id, clock.GetUtcNow().AddDays(-1));
        var order = AllenBuilding.Domain.Maintenance.WorkOrder.Submit(suite.Id, membership.Id, tenant.Id, "Test request", "Private details", "Kitchen", clock.GetUtcNow());
        membership.Revoke(user.Id, "Access ended", clock.GetUtcNow());
        var returning = Membership.Grant(suite.Id, tenant.Id, clock.GetUtcNow());
        var history = new Domain.Maintenance.WorkOrderHistory { WorkOrderId = order.Id, ActorId = tenant.Id, Action = "Submitted", OccurredAt = clock.GetUtcNow() };
        var suppressed = new AccountMessage
        {
            UserId = tenant.Id,
            BuildingId = building.Id,
            Kind = "MaintenanceUpdate",
            ProtectedPayload = protector.Protect(System.Text.Json.JsonSerializer.Serialize(new { workOrderId = order.Id, membershipId = membership.Id, historyId = history.Id })),
            CreatedAt = clock.GetUtcNow(),
            NextAttemptAt = clock.GetUtcNow()
        };
        await using (var db = create())
        {
            db.AddRange(suite, tenant, membership, order, history, returning, new ActiveGrant { UserId = tenant.Id, SuiteId = suite.Id, MembershipId = returning.Id }, suppressed);
            await db.SaveChangesAsync();
            Assert.True(await new NotificationDispatcher(Queue(db), new ThrowingTransport()).ProcessOneAsync());
        }
        await using (var db = create()) Assert.Equal(MessageState.Suppressed, (await db.AccountMessages.SingleAsync(x => x.Id == suppressed.Id)).State);
    }
}

public sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan duration) => _now += duration;
}
public sealed class ThrowingTransport : IEmailTransport
{
    public Task<DeliveryResult> SendAsync(EmailEnvelope envelope, CancellationToken cancellationToken)
        => throw new InvalidOperationException("private-provider-key");
}
