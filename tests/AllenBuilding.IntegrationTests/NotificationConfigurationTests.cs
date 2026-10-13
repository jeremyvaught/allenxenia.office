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
