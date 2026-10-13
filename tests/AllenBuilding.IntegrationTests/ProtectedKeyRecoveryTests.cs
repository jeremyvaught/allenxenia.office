using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AllenBuilding.Web.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AllenBuilding.IntegrationTests;

public sealed class ProtectedKeyRecoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "office-key-proof-" + Guid.NewGuid().ToString("N"));
    private string Release => Path.Combine(root, "release");
    private string Private => Path.Combine(root, "private");
    private string Certificate => Path.Combine(Private, "wrapping.pfx");
    private string Keys => Path.Combine(Private, "keys");
    private const string Password = "disposable-wrapping-password";

    public ProtectedKeyRecoveryTests()
    {
        Directory.CreateDirectory(Release); Directory.CreateDirectory(Private); RestrictDirectory(Private);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Office disposable key recovery", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        File.WriteAllBytes(Certificate, certificate.Export(X509ContentType.Pfx, Password)); RestrictFile(Certificate);
    }

    [Fact]
    public void Wrapped_keys_survive_new_release_and_independent_restore_but_require_the_original_private_certificate()
    {
        const string secret = "synthetic-protected-message"; string token;
        using (var first = Provider(Keys, Certificate)) token = first.GetRequiredService<IDataProtectionProvider>().CreateProtector("AllenOffice.AccountMessage.v1").Protect(secret);
        var keyFiles = Directory.GetFiles(Keys, "key-*.xml"); Assert.NotEmpty(keyFiles);
        foreach (var path in keyFiles)
        {
            var xml = File.ReadAllText(path); Assert.Contains("encryptedSecret", xml); Assert.DoesNotContain("<masterKey", xml); Assert.DoesNotContain(secret, xml);
        }
        using (var replacement = Provider(Keys, Certificate)) Assert.Equal(secret, replacement.GetRequiredService<IDataProtectionProvider>().CreateProtector("AllenOffice.AccountMessage.v1").Unprotect(token));
        var restored = Path.Combine(root, "restored"); Directory.CreateDirectory(restored); RestrictDirectory(restored);
        var restoredKeys = Path.Combine(restored, "keys"); Directory.CreateDirectory(restoredKeys); RestrictDirectory(restoredKeys);
        foreach (var source in keyFiles) { var destination = Path.Combine(restoredKeys, Path.GetFileName(source)); File.Copy(source, destination); RestrictFile(destination); }
        var restoredCertificate = Path.Combine(restored, "wrapping.pfx"); File.Copy(Certificate, restoredCertificate); RestrictFile(restoredCertificate);
        using (var recovery = Provider(restoredKeys, restoredCertificate)) Assert.Equal(secret, recovery.GetRequiredService<IDataProtectionProvider>().CreateProtector("AllenOffice.AccountMessage.v1").Unprotect(token));
        using (var isolated = Provider(Path.Combine(restored, "empty"), restoredCertificate))
            Assert.Throws<CryptographicException>(() => isolated.GetRequiredService<IDataProtectionProvider>().CreateProtector("AllenOffice.AccountMessage.v1").Unprotect(token));
        using var wrongKey = RSA.Create(2048); var request = new CertificateRequest("CN=Wrong wrapping certificate", wrongKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var wrong = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var wrongPath = Path.Combine(restored, "wrong.pfx"); File.WriteAllBytes(wrongPath, wrong.Export(X509ContentType.Pfx, Password)); RestrictFile(wrongPath);
        using var wrongProvider = Provider(restoredKeys, wrongPath);
        Assert.Throws<CryptographicException>(() => wrongProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("AllenOffice.AccountMessage.v1").Unprotect(token));
    }

    [Theory]
    [InlineData("keys-inside-release")]
    [InlineData("certificate-inside-release")]
    [InlineData("relative-key-directory")]
    [InlineData("shared-parent")]
    [InlineData("shared-certificate")]
    [InlineData("linked-certificate")]
    [InlineData("linked-keys")]
    [InlineData("wrong-password")]
    public void Unsafe_production_key_locations_fail_without_private_paths_or_passwords(string failure)
    {
        if (OperatingSystem.IsWindows() && failure is "shared-parent" or "shared-certificate" or "linked-certificate" or "linked-keys") return;
        var keys = Keys; var certificate = Certificate;
        if (failure == "keys-inside-release") keys = Path.Combine(Release, "keys");
        if (failure == "relative-key-directory") keys = "relative-private-keys";
        if (failure == "certificate-inside-release") { certificate = Path.Combine(Release, "secret.pfx"); File.Copy(Certificate, certificate); RestrictFile(certificate); }
        if (!OperatingSystem.IsWindows() && failure == "shared-parent") File.SetUnixFileMode(Private, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute);
        if (!OperatingSystem.IsWindows() && failure == "shared-certificate") File.SetUnixFileMode(Certificate, UnixFileMode.UserRead | UnixFileMode.GroupRead);
        if (failure == "linked-certificate") { certificate = Path.Combine(Private, "link.pfx"); File.CreateSymbolicLink(certificate, Certificate); }
        if (failure == "linked-keys") { Directory.CreateDirectory(Keys); RestrictDirectory(Keys); keys = Path.Combine(Private, "linked-keys"); Directory.CreateSymbolicLink(keys, Keys); }
        var error = Assert.Throws<InvalidOperationException>(() => Provider(keys, certificate, failure == "wrong-password" ? "private-password-canary" : Password));
        Assert.Null(error.InnerException); Assert.DoesNotContain(root, error.ToString()); Assert.DoesNotContain("private-password-canary", error.ToString());
    }

    private ServiceProvider Provider(string keys, string certificate, string password = Password)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production", ContentRootPath = Release });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Protection:KeyDirectory"] = keys, ["Protection:CertificatePath"] = certificate, ["Protection:CertificatePassword"] = password });
        builder.AddOfficeSecurity(); return builder.Services.BuildServiceProvider();
    }
    private static void RestrictDirectory(string path) { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    private static void RestrictFile(string path) { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    public void Dispose() { Directory.Delete(root, recursive: true); }
}
