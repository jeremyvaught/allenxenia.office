using System.Text.Json;
using AllenBuilding.DemoSeed;

namespace AllenBuilding.IntegrationTests;

// Runs the demonstration seeder for real, so a model or workflow change that breaks it fails here.
public sealed class DemoSeedTests
{
    [Fact]
    public async Task Seeder_writes_the_specified_demo_data_once()
    {
        var directory = Path.Combine(DemoRoot(), $"test-{Guid.NewGuid():N}");
        try
        {
            var report = await DemoSeeder.RunAsync(directory);

            Assert.Empty(report.Failures);
            // The counts the demo specification names, stated here independently of the seeder's own table.
            var specified = new Dictionary<string, int>
            {
                ["Suites"] = 7,
                ["Managers with an authenticator enrolled"] = 2,
                ["Tenants with access"] = 7,
                ["Revoked memberships"] = 1,
                ["Invitations pending"] = 1,
                ["Invitations expired"] = 1,
                ["Contractors active"] = 3,
                ["Contractors inactive"] = 1,
                ["Requests"] = 30,
                ["Requests submitted and untouched"] = 4,
                ["Requests reopened and back in submitted"] = 1,
                ["Requests triaged"] = 3,
                ["Requests assigned"] = 4,
                ["Requests in progress"] = 4,
                ["Requests completed, waiting to verify"] = 3,
                ["Requests closed"] = 9,
                ["Requests cancelled"] = 2,
                ["Closed requests from the former tenant"] = 2,
                ["Open requests from suite 302"] = 1,
                ["Tenants who see a request that is not their own"] = 0,
                ["Help articles published"] = 6,
                ["Help articles in draft"] = 1,
                ["Emails waiting to send"] = 2,
                ["Emails failed, with a retry available"] = 1,
                ["Payment, autopay, provider and financial-exception rows"] = 0,
                ["Rent accounts"] = 7,
                ["Rent accounts with rent terms"] = 7,
                ["One-time charges"] = 3,
                ["Rent report total matches the account pages"] = 1,
                ["Tenants who see billing that is not their own, or a credit reason"] = 0,
                ["Dashboard: new requests to triage"] = 5,
                ["Dashboard: finished, waiting to verify"] = 3,
                ["Dashboard: invitations not yet accepted"] = 1,
            };
            foreach (var (name, expected) in specified)
                Assert.True(report[name] == expected, $"{name}: expected {expected}, found {report[name]}.");
            Assert.True(report["Requests with a tenant comment and a manager reply"] >= 8);
            Assert.True(report["Requests with an internal manager note"] >= 4);
            Assert.True(report["Open urgent requests"] >= 2);
            Assert.True(report["Open requests older than 21 days"] >= 2);
            // Rent for last month and this month; the newest tenant may not have a whole month yet.
            Assert.InRange(report["Monthly rent charges"], 12, 13);
            Assert.InRange(report["Tenants with an overdue balance"], 1, 2);
            Assert.True(report["Tenants with nothing overdue"] >= 5);
            Assert.True(report["Credits recorded by a manager"] >= 10);

            var accounts = Path.Combine(directory, "accounts.json");
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(accounts));
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(accounts));
            Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("password").GetString()));
            var managers = json.RootElement.GetProperty("managers").EnumerateArray().ToArray();
            Assert.Equal(2, managers.Length);
            Assert.All(managers, manager => Assert.False(string.IsNullOrWhiteSpace(manager.GetProperty("authenticatorKey").GetString())));
            Assert.Equal(7, json.RootElement.GetProperty("tenants").GetArrayLength());
            Assert.DoesNotContain("@", (await File.ReadAllTextAsync(accounts)).Replace("@example.test", ""));

            var again = await Assert.ThrowsAsync<InvalidOperationException>(() => DemoSeeder.RunAsync(directory));
            Assert.Contains("Refusing to overwrite", again.Message);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("artifacts/elsewhere/demo")]
    [InlineData("artifacts/demo")]
    [InlineData("artifacts/demo/nested/deeper")]
    [InlineData("src/AllenBuilding.Web/.data")]
    public async Task Seeder_refuses_any_target_that_is_not_a_named_directory_under_artifacts_demo(string relative)
    {
        var target = Path.Combine(Directory.GetParent(Directory.GetParent(DemoRoot())!.FullName)!.FullName, relative);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => DemoSeeder.RunAsync(target));
        Assert.Contains("artifacts/demo/<name>", refused.Message);
    }

    [Fact]
    public async Task Seeder_refuses_a_demo_directory_outside_this_repository()
    {
        var target = Path.Combine(Path.GetTempPath(), $"office-demo-{Guid.NewGuid():N}", "artifacts", "demo", "run");
        await Assert.ThrowsAsync<InvalidOperationException>(() => DemoSeeder.RunAsync(target));
        Assert.False(Directory.Exists(target));
    }

    private static string DemoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AllenXeniaOffice.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "artifacts", "demo");
    }
}
