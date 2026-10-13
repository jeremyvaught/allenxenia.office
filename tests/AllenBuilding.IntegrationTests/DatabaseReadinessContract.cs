using AllenBuilding.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AllenBuilding.IntegrationTests;

public static class DatabaseReadinessContract
{
    public static async Task VerifyAsync(Func<OfficeDbContext> create)
    {
        await using var db = create();
        var probe = new DatabaseReadiness(db);
        Assert.Equal(HealthStatus.Healthy, (await probe.CheckHealthAsync(new HealthCheckContext())).Status);
        const string future = "20991231000000_FutureSchema";
        await db.Database.ExecuteSqlRawAsync("INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`) VALUES ('20991231000000_FutureSchema', '10.0.12')");
        try { Assert.Equal(HealthStatus.Unhealthy, (await probe.CheckHealthAsync(new HealthCheckContext())).Status); }
        finally { await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM `__EFMigrationsHistory` WHERE `MigrationId` = {future}"); }
        // A forged/current history cannot conceal a missing column in the latest projection.
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE account_messages RENAME COLUMN origin_span_id TO readiness_missing_span");
        try
        {
            var failed = await probe.CheckHealthAsync(new HealthCheckContext());
            Assert.Equal(HealthStatus.Unhealthy, failed.Status); Assert.Null(failed.Exception); Assert.Empty(failed.Data);
        }
        finally { await db.Database.ExecuteSqlRawAsync("ALTER TABLE account_messages RENAME COLUMN readiness_missing_span TO origin_span_id"); }
        Assert.Equal(HealthStatus.Healthy, (await probe.CheckHealthAsync(new HealthCheckContext())).Status);
    }
}
