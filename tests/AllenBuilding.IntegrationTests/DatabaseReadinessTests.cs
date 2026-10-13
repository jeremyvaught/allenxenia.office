using System.Net;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AllenBuilding.IntegrationTests;

public sealed class DatabaseReadinessTests
{
    [Fact]
    public async Task Process_liveness_and_database_readiness_are_separate_and_probes_do_not_migrate()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"office-readiness-{Guid.NewGuid():N}"); Directory.CreateDirectory(directory);
        try
        {
            await using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["Database:Provider"] = "Sqlite", ["ConnectionStrings:Office"] = $"Data Source={Path.Combine(directory, "probe.db")}", ["Protection:KeyDirectory"] = Path.Combine(directory, "keys") }));
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<SqliteOfficeDbContext>();
                    services.RemoveAll<DbContextOptions<SqliteOfficeDbContext>>();
                    services.RemoveAll<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<SqliteOfficeDbContext>>();
                    services.AddDbContext<SqliteOfficeDbContext>(options => options.UseSqlite($"Data Source={Path.Combine(directory, "probe.db")};Foreign Keys=True"));
                });
            });
            using var client = host.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
            var pending = await client.GetAsync("/health/ready");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, pending.StatusCode);
            Assert.Equal("no-store", pending.Headers.CacheControl?.ToString());
            Assert.Equal("{\"status\":\"Unavailable\"}", await pending.Content.ReadAsStringAsync());
            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
            await db.Database.MigrateAsync();
            Assert.Equal(0, await db.Users.CountAsync());
            Assert.True(File.Exists(Path.Combine(directory, "probe.db")));
            using var diagnosticScope = host.Services.CreateScope();
            var diagnosticDb = diagnosticScope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            var expected = diagnosticDb.Database.GetMigrations().ToArray();
            var applied = (await diagnosticDb.Database.GetAppliedMigrationsAsync()).ToArray();
            Assert.True(expected.Length > 0 && expected.SequenceEqual(applied), $"expected={expected.Length}, applied={applied.Length}");
            var direct = await new DatabaseReadiness(diagnosticDb).CheckHealthAsync(new HealthCheckContext());
            Assert.True(direct.Status == HealthStatus.Healthy, direct.Description);
            var ready = await client.GetAsync("/health/ready");
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode); Assert.Equal("{\"status\":\"Ready\"}", await ready.Content.ReadAsStringAsync());
            Assert.Equal(0, await db.Users.CountAsync());
            await db.Database.ExecuteSqlRawAsync("INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('20991231000000_FutureSchema', '10.0.12')");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task A_database_error_or_cancelled_probe_has_a_sanitized_unavailable_result()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-private-password-{Guid.NewGuid():N}", "db.sqlite");
        await using var db = new SqliteOfficeDbContext(new DbContextOptionsBuilder<SqliteOfficeDbContext>().UseSqlite($"Data Source={path};Mode=ReadOnly").Options);
        var probe = new DatabaseReadiness(db);
        var failed = await probe.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, failed.Status); Assert.Null(failed.Exception); Assert.Empty(failed.Data);
        Assert.Equal("DatabaseUnavailable", failed.Description);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Equal(HealthStatus.Unhealthy, (await probe.CheckHealthAsync(new HealthCheckContext(), cancelled.Token)).Status);
    }
}
