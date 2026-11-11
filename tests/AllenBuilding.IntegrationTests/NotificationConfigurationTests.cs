using AllenBuilding.Application.Notifications;
using AllenBuilding.Infrastructure.Notifications;
using AllenBuilding.Web.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AllenBuilding.IntegrationTests;

public sealed class NotificationConfigurationTests
{
    [Fact]
    public void Smtp_composition_does_not_send_or_start_a_worker_until_explicitly_enabled()
    {
        var builder = Builder("Smtp", "https://office.example.test/", false);
        builder.AddOfficeNotifications();
        using var services = builder.Services.BuildServiceProvider();
        Assert.IsType<SmtpEmailTransport>(services.GetRequiredService<IEmailTransport>());
        Assert.DoesNotContain(builder.Services, x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(NotificationWorker));
    }

    [Theory]
    [InlineData("LocalInbox", "https://office.example.test/", false)]
    [InlineData("Disabled", "https://office.example.test/", true)]
    [InlineData("Smtp", "http://office.example.test/", false)]
    [InlineData("Smtp", "https://user:private-canary@office.example.test/", false)]
    [InlineData("Smtp", "https://office.example.test/subdirectory", false)]
    public void Unsafe_production_notification_configuration_is_rejected(string mode, string origin, bool enabled)
    {
        var builder = Builder(mode, origin, enabled);
        var error = Assert.Throws<InvalidOperationException>(() => builder.AddOfficeNotifications());
        Assert.DoesNotContain("private-canary", error.Message);
    }

    [Theory]
    [InlineData("Testing")]
    [InlineData("Development")]
    public void Local_inbox_is_accepted_only_for_local_loopback_hosts(string environment)
    {
        var builder = Local(environment, new() { ["Notifications:Mode"] = "LocalInbox", ["Notifications:PublicOrigin"] = "http://127.0.0.1:5081/" });
        builder.AddOfficeNotifications();
        using var services = builder.Services.BuildServiceProvider();
        Assert.IsType<LocalInboxTransport>(services.GetRequiredService<IEmailTransport>());
        var remote = Local(environment, new() { ["Notifications:Mode"] = "LocalInbox", ["Notifications:PublicOrigin"] = "https://office.example.test/" });
        Assert.Contains("loopback", Assert.Throws<InvalidOperationException>(() => remote.AddOfficeNotifications()).Message);
    }

    [Fact]
    public void Unknown_notification_mode_is_rejected_even_locally()
    {
        var builder = Local("Testing", new() { ["Notifications:Mode"] = "Console" });
        Assert.Contains("Notifications:Mode", Assert.Throws<InvalidOperationException>(() => builder.AddOfficeNotifications()).Message);
    }

    [Fact]
    public void Default_production_notifications_are_unconfigured_and_start_no_worker()
    {
        var builder = Local("Production", new());
        builder.AddOfficeNotifications();
        using var services = builder.Services.BuildServiceProvider();
        Assert.IsType<UnconfiguredEmailTransport>(services.GetRequiredService<IEmailTransport>());
        Assert.DoesNotContain(builder.Services, x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(NotificationWorker));
    }

    [Fact]
    public void Smtp_without_credentials_is_rejected_and_enabled_smtp_starts_the_worker()
    {
        var missing = Local("Production", new() { ["Notifications:Mode"] = "Smtp", ["Notifications:PublicOrigin"] = "https://office.example.test/" });
        Assert.Contains("SMTP requires", Assert.Throws<InvalidOperationException>(() => missing.AddOfficeNotifications()).Message);
        var enabled = Builder("Smtp", "https://office.example.test/", true);
        enabled.AddOfficeNotifications();
        Assert.Contains(enabled.Services, x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(NotificationWorker));
    }

    private static WebApplicationBuilder Local(string environment, Dictionary<string, string?> values)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { EnvironmentName = environment, ContentRootPath = Path.GetTempPath() });
        builder.Configuration.AddInMemoryCollection(values);
        return builder;
    }

    private static WebApplicationBuilder Builder(string mode, string origin, bool enabled)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Notifications:Mode"] = mode,
            ["Notifications:PublicOrigin"] = origin,
            ["Notifications:Enabled"] = enabled.ToString(),
            ["Notifications:Smtp:Host"] = "smtp.example.test",
            ["Notifications:Smtp:Port"] = "587",
            ["Notifications:Smtp:Security"] = "StartTls",
            ["Notifications:Smtp:UserName"] = "synthetic-user",
            ["Notifications:Smtp:Password"] = "synthetic-password",
            ["Notifications:Smtp:FromAddress"] = "sender@example.test"
        });
        return builder;
    }
}
