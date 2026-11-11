using AllenBuilding.Application.Notifications;
using AllenBuilding.Web.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AllenBuilding.IntegrationTests;

public sealed class NotificationWorkerTests
{
    [Fact]
    public async Task Worker_logs_the_failure_type_but_not_its_private_message()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddScoped<INotificationQueue, UnreachableQueue>();
        services.AddSingleton<IEmailTransport, ThrowingTransport>();
        services.AddScoped<NotificationDispatcher>();
        await using var provider = services.BuildServiceProvider();
        var logs = new CapturedLogger<NotificationWorker>();
        using var worker = new NotificationWorker(provider.GetRequiredService<IServiceScopeFactory>(), logs);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            // The worker's first tick is five seconds after start.
            for (var i = 0; i < 150 && logs.Messages.IsEmpty; i++)
                await Task.Delay(100);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
        var logged = Assert.Single(logs.Messages);
        Assert.Contains("retrying on the next interval", logged);
        Assert.Contains("System.TimeoutException", logged);
        Assert.DoesNotContain("tenant@example.test", logged);
    }

    private sealed class UnreachableQueue : INotificationQueue
    {
        public Task<MessageLease?> ClaimAsync(CancellationToken cancellationToken) =>
            throw new TimeoutException("Server=db.internal;User=tenant@example.test");
        public Task<EmailEnvelope?> ComposeAsync(MessageLease lease, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> CompleteAsync(MessageLease lease, DeliveryResult result, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
