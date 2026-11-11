using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AllenBuilding.Application.Assistant;
using AllenBuilding.Infrastructure.Assistant;
using AllenBuilding.Web.Hosting;
using AllenBuilding.Web.Security;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AllenBuilding.IntegrationTests;

// Startup must refuse unsafe security settings. Builders are empty so no machine environment
// variables or developer files feed these checks.
public sealed class SecurityConfigurationTests : IDisposable
{
    private const string CertificatePassword = "disposable-wrapping-password";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "office-security-config-" + Guid.NewGuid().ToString("N"));
    private string Release => Path.Combine(_root, "release");
    private string Private => Path.Combine(_root, "private");
    private string Keys => Path.Combine(Private, "keys");

    public SecurityConfigurationTests()
    {
        Directory.CreateDirectory(Release);
        Directory.CreateDirectory(Private);
        Restrict(Private, directory: true);
    }

    [Fact]
    public void Production_refuses_to_start_without_a_persistent_key_directory()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Security("Production", new()));
        Assert.Contains("Protection:KeyDirectory", error.Message);
        Assert.False(Directory.Exists(Path.Combine(Release, ".data")));
    }

    [Fact]
    public void Production_refuses_to_start_without_a_wrapping_certificate()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Security("Production", new() { ["Protection:KeyDirectory"] = Keys }));
        Assert.Contains("Protection:CertificatePath", error.Message);
    }

    [Fact]
    public void Production_with_a_private_certificate_starts_with_secure_only_session_cookies()
    {
        var certificate = WriteCertificate("wrapping.pfx", withPrivateKey: true);
        using var production = Security("Production", new() { ["Protection:KeyDirectory"] = Keys, ["Protection:CertificatePath"] = certificate, ["Protection:CertificatePassword"] = CertificatePassword });
        Assert.Equal(CookieSecurePolicy.Always, SessionCookie(production).Cookie.SecurePolicy);
        Assert.Equal("AllenOffice.Session", SessionCookie(production).Cookie.Name);
        using var testing = Security("Testing", new() { ["Protection:KeyDirectory"] = Keys });
        Assert.Equal(CookieSecurePolicy.SameAsRequest, SessionCookie(testing).Cookie.SecurePolicy);
    }

    [Fact]
    public void Wrapping_certificate_without_its_private_key_is_rejected()
    {
        var publicOnly = WriteCertificate("public-only.pfx", withPrivateKey: false);
        var error = Assert.Throws<InvalidOperationException>(() => Security("Testing",
            new() { ["Protection:KeyDirectory"] = Keys, ["Protection:CertificatePath"] = publicOnly, ["Protection:CertificatePassword"] = CertificatePassword }));
        Assert.Contains("private key", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(_root, error.ToString());
        var complete = WriteCertificate("complete.pfx", withPrivateKey: true);
        using var accepted = Security("Testing",
            new() { ["Protection:KeyDirectory"] = Keys, ["Protection:CertificatePath"] = complete, ["Protection:CertificatePassword"] = CertificatePassword });
        Assert.NotNull(accepted.GetService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("too-short")]
    [InlineData("synthetic key with spaces")]
    [InlineData("oversize")]
    public void Enabled_assistant_refuses_a_missing_or_malformed_credential(string? key)
    {
        if (key == "oversize")
            key = new string('k', 1025);
        var builder = Builder("Production", new() { ["Assistant:Enabled"] = "true", ["OPENAI_API_KEY"] = key });
        var error = Assert.Throws<InvalidOperationException>(builder.AddOfficeAssistant);
        Assert.Contains("server-side OpenAI credential", error.Message);
        if (key is not null)
            Assert.DoesNotContain(key, error.Message);
    }

    [Fact]
    public void Enabled_assistant_with_a_valid_credential_uses_the_provider_model()
    {
        var builder = Builder("Production", new() { ["Assistant:Enabled"] = "true", ["OPENAI_API_KEY"] = "synthetic-assistant-credential" });
        builder.AddOfficeAssistant();
        using var services = builder.Services.BuildServiceProvider();
        using var scope = services.CreateScope();
        Assert.IsType<OpenAiAssistantModel>(scope.ServiceProvider.GetRequiredService<IAssistantModel>());
        Assert.Equal(new AssistantPolicy(true), services.GetRequiredService<AssistantPolicy>());
    }

    [Fact]
    public void Production_assistant_stays_disabled_by_default_even_with_a_credential_and_feature_flags()
    {
        var builder = Builder("Production", new()
        {
            ["OPENAI_API_KEY"] = "synthetic-assistant-credential",
            ["Assistant:TenantHelpEnabled"] = "true",
            ["Assistant:ActionsEnabled"] = "true",
            ["Assistant:FollowUpsEnabled"] = "true",
            ["Assistant:GuestDraftsEnabled"] = "true",
            ["Billing:PlanningEnabled"] = "true"
        });
        builder.AddOfficeAssistant();
        using var services = builder.Services.BuildServiceProvider();
        using var scope = services.CreateScope();
        Assert.IsType<DisabledAssistantModel>(scope.ServiceProvider.GetRequiredService<IAssistantModel>());
        Assert.Equal(new AssistantPolicy(false, BillingEnabled: true), services.GetRequiredService<AssistantPolicy>());
    }

    [Fact]
    public void Development_assistant_enables_its_features_only_when_a_credential_is_present()
    {
        var configured = Builder("Development", new() { ["OPENAI_API_KEY"] = "synthetic-assistant-credential", ["Assistant:GuestDraftsEnabled"] = "false" });
        configured.AddOfficeAssistant();
        using (var services = configured.Services.BuildServiceProvider())
            Assert.Equal(new AssistantPolicy(true, true, false, true, true, false), services.GetRequiredService<AssistantPolicy>());
        var missing = Builder("Development", new());
        missing.AddOfficeAssistant();
        using var disabled = missing.Services.BuildServiceProvider();
        Assert.Equal(new AssistantPolicy(false), disabled.GetRequiredService<AssistantPolicy>());
    }

    [Fact]
    public void Local_openai_file_is_read_only_in_a_development_source_checkout_and_never_overrides_configuration()
    {
        var checkout = Path.Combine(_root, "checkout");
        var content = Path.Combine(checkout, "src", "Web");
        Directory.CreateDirectory(content);
        var env = Path.Combine(checkout, ".env.local");
        File.WriteAllText(env, "OPENAI_API_KEY=synthetic-local-credential\n");
        Restrict(env, directory: false);

        var notCheckout = Builder("Development", new(), content);
        notCheckout.AddLocalOpenAiConfiguration();
        Assert.Null(notCheckout.Configuration["OPENAI_API_KEY"]);

        File.WriteAllText(Path.Combine(checkout, "AGENTS.md"), "synthetic marker");
        var production = Builder("Production", new(), content);
        production.AddLocalOpenAiConfiguration();
        Assert.Null(production.Configuration["OPENAI_API_KEY"]);

        var development = Builder("Development", new(), content);
        development.AddLocalOpenAiConfiguration();
        Assert.Equal("synthetic-local-credential", development.Configuration["OPENAI_API_KEY"]);

        var explicitKey = Builder("Development", new() { ["OPENAI_API_KEY"] = "synthetic-explicit-credential" }, content);
        explicitKey.AddLocalOpenAiConfiguration();
        Assert.Equal("synthetic-explicit-credential", explicitKey.Configuration["OPENAI_API_KEY"]);

        File.WriteAllText(env, "# no key here\nOTHER=value\n");
        Restrict(env, directory: false);
        var noKey = Builder("Development", new(), content);
        noKey.AddLocalOpenAiConfiguration();
        Assert.Null(noKey.Configuration["OPENAI_API_KEY"]);
    }

    [Fact]
    public async Task Manager_recovery_refuses_a_non_interactive_or_unauthorized_operator_before_prompting()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Testing", ContentRootPath = Release });
        await using var app = builder.Build();
        // Configured for the current OS user, so only the terminal check can refuse when input is redirected.
        var authorized = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Operations:ManagerRecoveryEnabled"] = "true", ["Operations:RecoveryOperatingSystemUser"] = Environment.UserName }).Build();
        var disabled = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Operations:ManagerRecoveryEnabled"] = "false", ["Operations:RecoveryOperatingSystemUser"] = Environment.UserName }).Build();
        if (Console.IsInputRedirected)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ManagerRecoveryCommand.RunAsync(app, authorized));
            Assert.Contains("interactive authorized deployment terminal", error.Message);
        }
        else
        {
            // Interactive run: the policy check refuses before any prompt can read input.
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ManagerRecoveryCommand.RunAsync(app, disabled));
            Assert.Contains("disabled or this operating-system identity", error.Message);
        }
        var wrongUser = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Operations:ManagerRecoveryEnabled"] = "true", ["Operations:RecoveryOperatingSystemUser"] = "not-" + Environment.UserName }).Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ManagerRecoveryCommand.RunAsync(app, wrongUser));
    }

    private ServiceProvider Security(string environment, Dictionary<string, string?> values)
    {
        var builder = Builder(environment, values);
        builder.AddOfficeSecurity();
        return builder.Services.BuildServiceProvider();
    }

    private WebApplicationBuilder Builder(string environment, Dictionary<string, string?> values, string? contentRoot = null)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { EnvironmentName = environment, ContentRootPath = contentRoot ?? Release });
        builder.Configuration.AddInMemoryCollection(values);
        return builder;
    }

    private static CookieAuthenticationOptions SessionCookie(IServiceProvider services) =>
        services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);

    private string WriteCertificate(string name, bool withPrivateKey)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Office disposable configuration check", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var exported = withPrivateKey ? certificate : X509CertificateLoader.LoadCertificate(certificate.RawData);
        var path = Path.Combine(Private, name);
        File.WriteAllBytes(path, exported.Export(X509ContentType.Pfx, CertificatePassword));
        Restrict(path, directory: false);
        return path;
    }

    private static void Restrict(string path, bool directory)
    {
        if (OperatingSystem.IsWindows())
            return;
        File.SetUnixFileMode(path, directory
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            : UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
