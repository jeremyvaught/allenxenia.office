using AllenBuilding.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MySql.Data.MySqlClient;

namespace AllenBuilding.IntegrationTests;

public sealed class MySqlConnectionPolicyTests
{
    [Fact]
    public void Production_migrations_require_the_separate_migrator_connection()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "MySql",
            ["ConnectionStrings:Office"] = "Server=localhost;Database=office;User ID=runtime;Password=synthetic-password;",
            ["ConnectionStrings:OfficeMigrator"] = "Server=localhost;Database=office;User ID=migrator;Password=synthetic-password;MaximumPoolSize=2;"
        }).Build();
        var environment = new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = Environments.Production };
        using var runtime = new ServiceCollection().AddOfficeStorage(configuration, environment).BuildServiceProvider();
        using var runtimeScope = runtime.CreateScope();
        Assert.Equal("runtime", new MySqlConnectionStringBuilder(runtimeScope.ServiceProvider.GetRequiredService<OfficeDbContext>().Database.GetConnectionString()).UserID);
        using var migrator = new ServiceCollection().AddOfficeStorage(configuration, environment, migrations: true).BuildServiceProvider();
        using var migrationScope = migrator.CreateScope();
        var selected = new MySqlConnectionStringBuilder(migrationScope.ServiceProvider.GetRequiredService<OfficeDbContext>().Database.GetConnectionString());
        Assert.Equal("migrator", selected.UserID); Assert.Equal(2u, selected.MaximumPoolSize);
        configuration["ConnectionStrings:OfficeMigrator"] = null;
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddOfficeStorage(configuration, environment, migrations: true));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void Production_loopback_pools_and_timeouts_are_bounded(string server)
    {
        var options = new MySqlConnectionStringBuilder(MySqlConnectionPolicy.Validate($"Server={server};Database=office;User ID=runtime;Password=synthetic-secret;SslMode=Disabled", false));
        Assert.Equal(10u, options.MaximumPoolSize); Assert.Equal(5u, options.ConnectionTimeout); Assert.Equal(15u, options.DefaultCommandTimeout);
        Assert.True(options.ConnectionReset); Assert.False(options.PersistSecurityInfo); Assert.False(options.AllowLoadLocalInfile);
    }

    [Theory]
    [InlineData("Server=other-host;SslMode=Preferred")]
    [InlineData("Server=other-host;SslMode=Required")]
    [InlineData("Server=other-host;SslMode=VerifyCA")]
    [InlineData("Server=localhost;MaximumPoolSize=11")]
    [InlineData("Server=localhost;MinimumPoolSize=11")]
    [InlineData("Server=localhost;Pooling=False")]
    [InlineData("Server=localhost;PersistSecurityInfo=True")]
    [InlineData("Server=localhost;AllowLoadLocalInfile=True")]
    [InlineData("Server=localhost;ConnectionTimeout=0")]
    [InlineData("Server=localhost;DefaultCommandTimeout=0")]
    public void Unsafe_runtime_configuration_is_rejected_without_secret_values(string settings)
    {
        var error = Assert.Throws<InvalidOperationException>(() => MySqlConnectionPolicy.Validate($"{settings};Database=office;User ID=runtime;Password=synthetic-secret;", false));
        Assert.DoesNotContain("synthetic-secret", error.Message); Assert.DoesNotContain("other-host", error.Message);
    }

    [Fact]
    public void Remote_verified_tls_and_local_sockets_preserve_a_smaller_explicit_budget()
    {
        var remote = new MySqlConnectionStringBuilder(MySqlConnectionPolicy.Validate("Server=db.example.test;Database=office;User ID=runtime;Password=synthetic-secret;SslMode=VerifyFull;MaximumPoolSize=4;ConnectionTimeout=2;DefaultCommandTimeout=3", false));
        Assert.Equal(MySqlSslMode.VerifyFull, remote.SslMode); Assert.Equal(4u, remote.MaximumPoolSize); Assert.Equal(2u, remote.ConnectionTimeout); Assert.Equal(3u, remote.DefaultCommandTimeout);
        var socket = new MySqlConnectionStringBuilder(MySqlConnectionPolicy.Validate("Server=/run/mysqld/mysqld.sock;Protocol=Unix;Database=office;User ID=runtime;Password=synthetic-secret;SslMode=Disabled", false));
        Assert.Equal(MySqlConnectionProtocol.UnixSocket, socket.ConnectionProtocol);
    }

    [Fact]
    public void Parse_failures_and_missing_credentials_are_sanitized_while_isolated_tests_remain_supported()
    {
        Assert.Throws<InvalidOperationException>(() => MySqlConnectionPolicy.Validate("private-canary=secret-value", false));
        Assert.Throws<InvalidOperationException>(() => MySqlConnectionPolicy.Validate("Server=localhost;Database=office;User ID=runtime", false));
        var isolated = MySqlConnectionPolicy.Validate("Server=127.0.0.1;SslMode=Disabled;MaximumPoolSize=20", true);
        Assert.Equal(20u, new MySqlConnectionStringBuilder(isolated).MaximumPoolSize);
    }
}
