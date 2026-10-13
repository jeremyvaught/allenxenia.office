using AllenBuilding.Web.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

namespace AllenBuilding.IntegrationTests;

public sealed class RuntimeConfigurationTests
{
    [Fact]
    public void Private_runtime_settings_load_from_outside_the_release_without_a_file_watcher()
    {
        var directory = Path.Combine(Path.GetTempPath(), "office-runtime-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(directory, "runtime.json");
        try
        {
            File.WriteAllText(path, "{\"Notifications\":{\"Enabled\":false},\"SyntheticPrivateValue\":\"private-value\"}");
            Private(path); var builder = Builder(path); builder.AddOfficeRuntimeConfiguration(false);
            Assert.Equal("private-value", builder.Configuration["SyntheticPrivateValue"]); Assert.False(builder.Configuration.GetValue<bool>("Notifications:Enabled"));
            File.WriteAllText(path, "{\"SyntheticPrivateValue\":\"later-value\"}");
            Assert.Equal("private-value", builder.Configuration["SyntheticPrivateValue"]);
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }
    [Fact]
    public void Environment_override_wins_and_private_configuration_cannot_live_inside_the_release()
    {
        var directory = Path.Combine(Path.GetTempPath(), "office-runtime-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(directory, "runtime.json"); var name = "OfficeRuntimeUnit_" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { [name] = "file-value" })); Private(path);
            Environment.SetEnvironmentVariable(name, "process-value"); var builder = Builder(path); builder.AddOfficeRuntimeConfiguration(false); Assert.Equal("process-value", builder.Configuration[name]);
            var inside = Builder(path); inside.Environment.ContentRootPath = directory; Assert.Throws<InvalidOperationException>(() => inside.AddOfficeRuntimeConfiguration(false));
        }
        finally { Environment.SetEnvironmentVariable(name, null); File.Delete(path); Directory.Delete(directory); }
    }
    [Theory]
    [InlineData("permissions")]
    [InlineData("parent-permissions")]
    [InlineData("malformed")]
    [InlineData("large")]
    [InlineData("missing")]
    [InlineData("symlink")]
    public void Unsafe_runtime_files_fail_without_disclosing_contents_or_paths(string failure)
    {
        if (OperatingSystem.IsWindows() && failure is "permissions" or "parent-permissions" or "symlink") return;
        var directory = Path.Combine(Path.GetTempPath(), "office-runtime-private-canary-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(directory, "private-canary.json"); var link = Path.Combine(directory, "link.json");
        try
        {
            File.WriteAllText(path, failure == "large" ? new string('a', 1048577) : failure == "malformed" ? "{private-secret-canary" : "{\"Canary\":\"private-secret-canary\"}"); Private(path);
            if (!OperatingSystem.IsWindows() && failure == "permissions") File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.OtherRead);
            if (!OperatingSystem.IsWindows() && failure == "parent-permissions") File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute);
            if (failure == "missing") File.Delete(path);
            if (failure == "symlink") File.CreateSymbolicLink(link, path);
            var error = Assert.Throws<InvalidOperationException>(() => Builder(failure == "symlink" ? link : path).AddOfficeRuntimeConfiguration(false));
            Assert.DoesNotContain("private-canary", error.ToString()); Assert.DoesNotContain("private-secret-canary", error.ToString()); Assert.Null(error.InnerException);
        }
        finally { if (File.Exists(link)) File.Delete(link); if (File.Exists(path)) File.Delete(path); Directory.Delete(directory); }
    }
    [Fact]
    public void Relative_files_and_migration_credentials_in_the_web_process_are_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => Builder("relative.json").AddOfficeRuntimeConfiguration(false));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:OfficeMigrator"] = "synthetic-migration-secret" });
        var error = Assert.Throws<InvalidOperationException>(() => builder.AddOfficeRuntimeConfiguration(false)); Assert.DoesNotContain("synthetic-migration-secret", error.Message);
        builder.AddOfficeRuntimeConfiguration(true);
    }
    private static WebApplicationBuilder Builder(string path)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Office:RuntimeConfigFile"] = path }); return builder;
    }
    private static void Private(string path) { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
}
