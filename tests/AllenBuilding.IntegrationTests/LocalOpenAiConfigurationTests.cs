using AllenBuilding.Web.Hosting;

namespace AllenBuilding.IntegrationTests;

public sealed class LocalOpenAiConfigurationTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("duplicate")]
    [InlineData("permissions")]
    [InlineData("symlink")]
    [InlineData("large")]
    [InlineData("empty")]
    public void Local_credentials_are_bounded_private_and_errors_do_not_reveal_contents(string mode)
    {
        if (OperatingSystem.IsWindows() && mode is "permissions" or "symlink") return;
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ".env.local"); var link = Path.Combine(directory, "link");
        try
        {
            File.WriteAllText(path, mode switch
            {
                "duplicate" => "OPENAI_API_KEY=synthetic-private-key\nOPENAI_API_KEY=other-private-key",
                "large" => new string('x', 16385),
                "empty" => "OPENAI_API_KEY=",
                _ => "# ignored\nOPENAI_API_KEY=\"synthetic-private-key\""
            });
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            if (!OperatingSystem.IsWindows() && mode == "permissions") File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.OtherRead);
            if (mode == "symlink") File.CreateSymbolicLink(link, path);
            if (mode == "valid") Assert.Equal("synthetic-private-key", LocalOpenAiConfiguration.ReadKey(path));
            else
            {
                var exception = Assert.Throws<InvalidOperationException>(() => LocalOpenAiConfiguration.ReadKey(mode == "symlink" ? link : path));
                Assert.DoesNotContain("synthetic-private-key", exception.ToString()); Assert.Null(exception.InnerException);
            }
        }
        finally { if (File.Exists(link)) File.Delete(link); File.Delete(path); Directory.Delete(directory); }
    }
}
