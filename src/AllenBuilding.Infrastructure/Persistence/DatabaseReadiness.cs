using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AllenBuilding.Infrastructure.Persistence;

public sealed class DatabaseReadiness(OfficeDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        var previousTimeout = db.Database.GetCommandTimeout();
        db.Database.SetCommandTimeout(3);
        try
        {
            await db.Database.OpenConnectionAsync(deadline.Token);
            var expected = db.Database.GetMigrations().Order(StringComparer.Ordinal).ToArray();
            var applied = (await db.Database.GetAppliedMigrationsAsync(deadline.Token)).Order(StringComparer.Ordinal).ToArray();
            if (expected.Length == 0 || !expected.SequenceEqual(applied, StringComparer.Ordinal))
                return HealthCheckResult.Unhealthy("SchemaUnavailable");
            // Verify the current projection, not only an operator-editable history row.
            // No recipient, token or protected payload is retrieved.
            _ = await db.AccountMessages.AsNoTracking().Select(x => new { x.Id, x.State, x.OriginTraceId, x.OriginSpanId, x.OriginOperationId })
                .Take(1).ToArrayAsync(deadline.Token);
            return HealthCheckResult.Healthy("Ready");
        }
        catch (Exception) { return HealthCheckResult.Unhealthy("DatabaseUnavailable"); }
        finally
        {
            db.Database.SetCommandTimeout(previousTimeout);
            try { await db.Database.CloseConnectionAsync(); } catch (Exception) { }
        }
    }
}
