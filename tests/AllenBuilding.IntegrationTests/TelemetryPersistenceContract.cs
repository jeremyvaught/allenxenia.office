using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using AllenBuilding.Application.Diagnostics;
using AllenBuilding.Application.Notifications;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Notifications;
using AllenBuilding.Infrastructure.Persistence;
using AllenBuilding.Web.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace AllenBuilding.IntegrationTests;

public static class TelemetryPersistenceContract
{
    public static async Task VerifyAsync(Func<OfficeDbContext> create)
    {
        var records = new ProjectionExporter();
        using var sdk = Sdk.CreateTracerProviderBuilder().AddSource(OfficeTelemetry.Name).AddConnectorNet().SetSampler(new AlwaysOnSampler())
            .AddProcessor(new SimpleActivityExportProcessor(records)).Build();
        var protection = new EphemeralDataProtectionProvider(); var protector = protection.CreateProtector("AllenOffice.AccountMessage.v1");
        var clock = new ManualClock(DateTimeOffset.UtcNow); var operationId = Guid.NewGuid();
        var user = new OfficeUser { UserName = $"trace-{Guid.NewGuid():N}", Email = "private-recipient-canary@example.test", EmailConfirmed = true };
        var building = new Building { Name = "Trace contract building" }; Guid messageId; string traceId; string spanId;
        using (var untrusted = new Activity("untrusted-context").SetIdFormat(ActivityIdFormat.W3C).AddBaggage("private-header", "private-baggage-canary").Start())
        using (var request = OfficeTelemetry.Begin(OfficeOperationKind.Http))
        using (var command = OfficeTelemetry.Begin(OfficeOperationKind.MaintenanceSubmit, operationId))
        {
            Assert.NotEqual(untrusted.TraceId.ToHexString(), request.TraceId);
            await using var db = create(); db.AddRange(user, building);
            var message = new AccountMessage { UserId = user.Id, BuildingId = building.Id, Kind = "SecurityNotice", ProtectedPayload = protector.Protect("private-body-canary"), CreatedAt = clock.GetUtcNow(), NextAttemptAt = clock.GetUtcNow() };
            db.AccountMessages.Add(message); await db.SaveChangesAsync();
            messageId = message.Id; traceId = command.TraceId; spanId = command.SpanId; command.Result = OfficeOutcome.Success; request.Result = OfficeOutcome.Success;
        }
        NotificationQueue Queue(OfficeDbContext db) => new(db, protection, clock, new Uri("http://localhost:5081/"));
        async Task ProcessToAttempt(int target, IEmailTransport transport)
        {
            await using var db = create(); var dispatcher = new NotificationDispatcher(Queue(db), transport);
            for (var i = 0; i < 500; i++)
            {
                if (await db.AccountMessages.AsNoTracking().Where(x => x.Id == messageId).Select(x => x.Attempts).SingleAsync() >= target) return;
                Assert.True(await dispatcher.ProcessOneAsync());
            }
            Assert.Fail("The bounded shared-fixture queue did not reach the correlated message.");
        }
        await using (var db = create())
        {
            var saved = await db.AccountMessages.AsNoTracking().SingleAsync(x => x.Id == messageId);
            Assert.Equal(traceId, saved.OriginTraceId); Assert.Equal(spanId, saved.OriginSpanId); Assert.Equal(operationId, saved.OriginOperationId);
        }
        await ProcessToAttempt(1, new RetryTransport());
        clock.Advance(TimeSpan.FromMinutes(1));
        await ProcessToAttempt(2, new DeliveredTransport());
        await using (var db = create())
        {
            var saved = await db.AccountMessages.AsNoTracking().SingleAsync(x => x.Id == messageId);
            Assert.Equal(MessageState.Delivered, saved.State); Assert.Equal(2, saved.Attempts);
            Assert.Equal(traceId, saved.OriginTraceId); Assert.Equal(spanId, saved.OriginSpanId); Assert.Equal(operationId, saved.OriginOperationId);
            if (db is MySqlOfficeDbContext)
            {
                using var query = OfficeTelemetry.Begin(OfficeOperationKind.MaintenanceAging);
                await db.Database.ExecuteSqlRawAsync("SELECT 'private-sql-literal-canary'"); query.Result = OfficeOutcome.Success;
            }
        }
        var attempts = records.Items.Where(x => x.Kind == "notification.dispatch" && x.MessageId == messageId.ToString("D")).ToList();
        Assert.Equal(2, attempts.Count); Assert.Equal(new[] { "retry", "success" }, attempts.Select(x => x.Outcome));
        Assert.Equal(2, attempts.Select(x => x.TraceId).Distinct().Count());
        Assert.All(attempts, x => { Assert.NotEqual(traceId, x.TraceId); Assert.Equal(operationId.ToString("D"), x.OperationId); Assert.Equal(new TraceLink(traceId, spanId), Assert.Single(x.Links)); });
        var encoded = JsonSerializer.Serialize(attempts);
        foreach (var secret in new[] { user.Email, "private-body-canary", "private-provider-code-canary", "private-sql-literal-canary", "private-baggage-canary" }) Assert.DoesNotContain(secret!, encoded);
        await using var check = create();
        if (check is MySqlOfficeDbContext) Assert.True(records.DatabaseSpans > 0);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AllenXeniaOffice.slnx"))) root = root.Parent;
        Assert.NotNull(root); var directory = Path.Combine(root.FullName, "artifacts", "telemetry"); Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, check is MySqlOfficeDbContext ? "notification-incident-mysql.json" : "notification-incident-sqlite.json"), JsonSerializer.Serialize(new
        {
            Origin = new { TraceId = traceId, SpanId = spanId, OperationId = operationId },
            Attempts = attempts,
            DriverSpansObserved = records.DatabaseSpans,
            DriverAttributeNames = records.DatabaseAttributeNames.Distinct().Order().ToArray(),
            Scope = "Synthetic retry then delivery; persisted correlation and native driver tracing; no real mail, Stripe incident or hosted destination"
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private sealed class RetryTransport : IEmailTransport
    { public Task<DeliveryResult> SendAsync(EmailEnvelope envelope, CancellationToken cancellationToken) => Task.FromResult(new DeliveryResult(DeliveryOutcome.Retryable, "private-provider-code-canary")); }
    private sealed class DeliveredTransport : IEmailTransport
    { public Task<DeliveryResult> SendAsync(EmailEnvelope envelope, CancellationToken cancellationToken) => Task.FromResult(new DeliveryResult(DeliveryOutcome.Delivered)); }
    private sealed class ProjectionExporter : BaseExporter<Activity>
    {
        public ConcurrentQueue<SafeTrace> Items { get; } = new();
        public ConcurrentBag<string> DatabaseAttributeNames { get; } = new();
        public int DatabaseSpans;
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch)
            {
                var safe = SafeTrace.From(activity); Items.Enqueue(safe);
                if (safe.Kind == "database") { Interlocked.Increment(ref DatabaseSpans); foreach (var tag in activity.TagObjects) DatabaseAttributeNames.Add(tag.Key); }
                var encoded = JsonSerializer.Serialize(safe);
                Assert.DoesNotContain("private-sql-literal-canary", encoded); Assert.DoesNotContain("private-baggage-canary", encoded);
            }
            return ExportResult.Success;
        }
    }
}
