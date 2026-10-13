using AllenBuilding.Application.Notifications;
using AllenBuilding.Infrastructure.Notifications;
using Microsoft.AspNetCore.DataProtection;

namespace AllenBuilding.Web.Security;

public static class NotificationsRegistration
{
    public static void AddOfficeNotifications(this WebApplicationBuilder builder)
    {
        var local = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing");
        var mode = builder.Configuration["Notifications:Mode"] ?? "Disabled";
        if (mode is not ("Disabled" or "Smtp") && !(local && mode == "LocalInbox"))
            throw new InvalidOperationException("Notifications:Mode must be Disabled, Smtp, or local LocalInbox.");
        var origin = new Uri(builder.Configuration["Notifications:PublicOrigin"] ?? (local ? "http://localhost:5081/" : "https://office.allenxenia.com/"));
        if (!origin.IsAbsoluteUri || origin.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(origin.UserInfo) || origin.AbsolutePath != "/" || !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment) ||
            (!local && origin.Scheme != "https") || (local && !origin.IsLoopback))
            throw new InvalidOperationException("Notifications:PublicOrigin must be a root HTTPS origin, or a loopback origin for local use.");
        builder.Services.AddScoped<NotificationQueue>(services => new NotificationQueue(
            services.GetRequiredService<AllenBuilding.Infrastructure.Persistence.OfficeDbContext>(), services.GetRequiredService<IDataProtectionProvider>(),
            services.GetRequiredService<TimeProvider>(), origin));
        builder.Services.AddScoped<INotificationQueue>(services => services.GetRequiredService<NotificationQueue>());
        builder.Services.AddScoped<NotificationDispatcher>();
        if (mode == "LocalInbox") builder.Services.AddSingleton<IEmailTransport>(new LocalInboxTransport(Path.Combine(builder.Environment.ContentRootPath, ".data", "mail")));
        else if (mode == "Smtp")
        {
            var options = builder.Configuration.GetSection("Notifications:Smtp").Get<SmtpEmailOptions>() ?? new SmtpEmailOptions();
            options.Validate();
            builder.Services.AddSingleton<IEmailTransport>(new SmtpEmailTransport(options));
        }
        else builder.Services.AddSingleton<IEmailTransport, UnconfiguredEmailTransport>();
        if (builder.Configuration.GetValue<bool>("Notifications:Enabled"))
        {
            if (mode == "Disabled") throw new InvalidOperationException("A notification worker requires a configured transport.");
            builder.Services.AddHostedService<NotificationWorker>();
        }
    }
}

public sealed class NotificationWorker(IServiceScopeFactory scopes, ILogger<NotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                // Bounded batch; a fresh scope on each timer iteration recovers after database errors.
                for (var count = 0; count < 20 && !stoppingToken.IsCancellationRequested; count++)
                    if (!await scope.ServiceProvider.GetRequiredService<NotificationDispatcher>().ProcessOneAsync(stoppingToken)) break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Notification dispatch could not complete; retrying on the next interval."); }
        }
    }
}
