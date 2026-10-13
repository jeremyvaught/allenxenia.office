using System.Data;
using System.Data.Common;
using AllenBuilding.Application.Access;
using AllenBuilding.Application.Reporting;
using AllenBuilding.Domain.Maintenance;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AllenBuilding.Infrastructure.Persistence;

public sealed class ManagementReportReader(OfficeDbContext db) : IManagementReportReader
{
    public async Task<MaintenanceAgingReport?> MaintenanceAgingAsync(Actor actor, MaintenanceAgingQuery query, DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        if (!query.IsValid || !actor.ChallengedMfa || actor.ManagedBuildings.Count == 0) return null;
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = db.Database.GetDbConnection();
            if (db is MySqlOfficeDbContext)
            {
                await using var mode = connection.CreateCommand(); mode.CommandText = "SET TRANSACTION READ ONLY"; mode.CommandTimeout = 5;
                await mode.ExecuteNonQueryAsync(cancellationToken);
            }
            // SQLite deferred reads avoid taking a writer reservation. MySQL uses a nonlocking repeatable-read snapshot.
            await using var native = connection is SqliteConnection sqlite ? sqlite.BeginTransaction(IsolationLevel.Serializable, deferred: true)
                : await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
            MaintenanceAgingReport? report;
            await using (var transaction = await db.Database.UseTransactionAsync(native, cancellationToken))
            {
                var buildings = await ScopeAsync(actor, cancellationToken);
                if (buildings.Count is 0 or > 100 || query.BuildingId is { } requested && !buildings.Any(x => x.Id == requested)) return null;
                var scope = buildings.Where(x => query.BuildingId == null || x.Id == query.BuildingId).Select(x => x.Id).ToArray();
                MaintenanceAgingTotals totals;
                await using (var command = Command(connection, native, MaintenanceAgingSql.Totals(scope.Length), scope, observedAt))
                await using (var rows = await command.ExecuteReaderAsync(cancellationToken))
                {
                    await rows.ReadAsync(cancellationToken);
                    totals = new(Convert.ToInt64(rows.GetValue(0)), Convert.ToInt64(rows.GetValue(1)), Convert.ToInt64(rows.GetValue(2)), Convert.ToInt64(rows.GetValue(3)));
                }
                var items = new List<MaintenanceAgingItem>();
                await using (var command = Command(connection, native, MaintenanceAgingSql.Items(scope.Length, query.Bucket), scope, observedAt))
                {
                    Add(command, "@limit", MaintenanceAgingQuery.PageSize + 1, DbType.Int32);
                    Add(command, "@offset", (query.Page - 1) * MaintenanceAgingQuery.PageSize, DbType.Int32);
                    await using var rows = await command.ExecuteReaderAsync(cancellationToken);
                    while (await rows.ReadAsync(cancellationToken))
                        items.Add(new(Guid.Parse(rows.GetString(0)), rows.GetString(1), rows.GetString(2), rows.GetString(3),
                            Enum.Parse<WorkOrderStatus>(rows.GetString(4)), new DateTimeOffset(DateTime.SpecifyKind(rows.GetDateTime(5), DateTimeKind.Utc))));
                }
                report = new(query, observedAt, totals, items.Take(MaintenanceAgingQuery.PageSize).ToList(), items.Count > MaintenanceAgingQuery.PageSize && query.Page < 1000, buildings);
                await native.CommitAsync(cancellationToken);
            }
            // A removed grant must not release data from an older transaction snapshot.
            var fresh = await ScopeAsync(actor, cancellationToken);
            return report.Buildings.All(x => fresh.Any(y => x.Id == y.Id)) ? report : null;
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private async Task<List<ReportBuilding>> ScopeAsync(Actor actor, CancellationToken cancellationToken) =>
        await (from grant in db.BuildingManagers.AsNoTracking()
               join building in db.Buildings on grant.BuildingId equals building.Id
               where grant.UserId == actor.UserId && actor.ManagedBuildings.Contains(grant.BuildingId)
               orderby building.Name, building.Id
               select new ReportBuilding(building.Id, building.Name)).Take(101).ToListAsync(cancellationToken);

    internal static DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql, IReadOnlyList<Guid> buildings, DateTimeOffset observedAt)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; command.CommandTimeout = 5;
        for (var i = 0; i < buildings.Count; i++) Add(command, "@building" + i, buildings[i].ToString("D"), DbType.String);
        Add(command, "@asOf", observedAt.UtcDateTime, DbType.DateTime);
        Add(command, "@sevenDays", observedAt.AddDays(-7).UtcDateTime, DbType.DateTime);
        Add(command, "@thirtyDays", observedAt.AddDays(-30).UtcDateTime, DbType.DateTime);
        return command;
    }
    internal static void Add(DbCommand command, string name, object value, DbType type)
    {
        var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value; parameter.DbType = type; command.Parameters.Add(parameter);
    }
}

// Only fixed projections and allowlisted predicates enter SQL; all values are bound parameters.
internal static class MaintenanceAgingSql
{
    private const string Active = "w.status IN ('Submitted', 'Triaged', 'Assigned', 'InProgress')";
    private static string Population(int count) => $"FROM work_orders w JOIN suites s ON s.id = w.suite_id JOIN buildings b ON b.id = s.building_id WHERE s.building_id IN ({string.Join(",", Enumerable.Range(0, count).Select(i => "@building" + i))}) AND w.created_at <= @asOf";
    internal static string Totals(int count) => $"""
        /* maintenance-aging-totals */
        SELECT COALESCE(SUM(CASE WHEN {Active} AND w.created_at > @sevenDays THEN 1 ELSE 0 END), 0),
               COALESCE(SUM(CASE WHEN {Active} AND w.created_at <= @sevenDays AND w.created_at > @thirtyDays THEN 1 ELSE 0 END), 0),
               COALESCE(SUM(CASE WHEN {Active} AND w.created_at <= @thirtyDays THEN 1 ELSE 0 END), 0),
               COALESCE(SUM(CASE WHEN w.status = 'Completed' THEN 1 ELSE 0 END), 0)
        {Population(count)} AND ({Active} OR w.status = 'Completed')
        """;
    internal static string Items(int count, MaintenanceAgeBucket bucket) => $"""
        /* maintenance-aging-items */
        SELECT w.id, b.name, s.name, w.title, w.status, w.created_at
        {Population(count)} AND {Predicate(bucket)}
        ORDER BY w.created_at, w.id LIMIT @limit OFFSET @offset
        """;
    private static string Predicate(MaintenanceAgeBucket bucket) => bucket switch
    {
        MaintenanceAgeBucket.Active => Active,
        MaintenanceAgeBucket.Under7Days => $"{Active} AND w.created_at > @sevenDays",
        MaintenanceAgeBucket.From7To30Days => $"{Active} AND w.created_at <= @sevenDays AND w.created_at > @thirtyDays",
        MaintenanceAgeBucket.AtLeast30Days => $"{Active} AND w.created_at <= @thirtyDays",
        MaintenanceAgeBucket.AwaitingVerification => "w.status = 'Completed'",
        _ => throw new ArgumentOutOfRangeException(nameof(bucket))
    };
}
