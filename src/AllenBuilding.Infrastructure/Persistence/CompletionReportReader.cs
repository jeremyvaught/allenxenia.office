using System.Data;
using AllenBuilding.Application.Access;
using AllenBuilding.Application.Reporting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Infrastructure.Persistence;

public sealed partial class ManagementReportReader : ICompletionReportReader
{
    public async Task<CompletionReport?> ReadAsync(Actor actor, CompletionQuery query, DateTimeOffset observedAt, CancellationToken token)
    {
        if (!query.IsValid || !actor.ChallengedMfa || actor.ManagedBuildings.Count == 0) return null;
        await db.Database.OpenConnectionAsync(token);
        try
        {
            var connection = db.Database.GetDbConnection();
            if (db is MySqlOfficeDbContext)
            {
                await using var mode = connection.CreateCommand(); mode.CommandText = "SET TRANSACTION READ ONLY"; mode.CommandTimeout = 5; await mode.ExecuteNonQueryAsync(token);
            }
            await using var native = connection is SqliteConnection sqlite ? sqlite.BeginTransaction(IsolationLevel.Serializable, deferred: true) : await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
            CompletionReport report;
            await using (var transaction = await db.Database.UseTransactionAsync(native, token))
            {
                var buildings = await ScopeAsync(actor, token);
                if (buildings.Count is 0 or > 100 || query.BuildingId is { } requested && !buildings.Any(x => x.Id == requested)) return null;
                var scope = buildings.Where(x => query.BuildingId == null || x.Id == query.BuildingId).Select(x => x.Id).ToArray();
                await using var command = Command(connection, native, CompletionSql.Summary(scope.Length, db is MySqlOfficeDbContext), scope, observedAt);
                await using var rows = await command.ExecuteReaderAsync(token); await rows.ReadAsync(token);
                double? Number(int column) => rows.IsDBNull(column) ? null : Convert.ToDouble(rows.GetValue(column));
                report = new(query, observedAt, Convert.ToInt64(rows.GetValue(0)), Convert.ToInt64(rows.GetValue(1)), Convert.ToInt64(rows.GetValue(2)), Convert.ToInt64(rows.GetValue(3)),
                    new(Convert.ToInt64(rows.GetValue(4)), Number(5), Number(6)), new(Convert.ToInt64(rows.GetValue(7)), Number(8), Number(9)), buildings);
                await rows.CloseAsync(); await native.CommitAsync(token);
            }
            var fresh = await ScopeAsync(actor, token); return report.Buildings.All(x => fresh.Any(y => x.Id == y.Id)) ? report : null;
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }
}

internal static class CompletionSql
{
    // History currently has timestamps, not a per-request sequence. Conflicting workflow events
    // at one timestamp are excluded and counted; GUID ordering cannot invent their actual order.
    public static string Summary(int count, bool mysql)
    {
        string Seconds(string start, string end) => mysql ? $"TIMESTAMPDIFF(MICROSECOND,{start},{end}) / 1000000.0" : $"ROUND((julianday({end}) - julianday({start})) * 86400.0,3)";
        return $$"""
            WITH scoped AS (
              SELECT w.id, w.created_at FROM work_orders w JOIN suites s ON s.id = w.suite_id
              WHERE s.building_id IN ({{string.Join(",", Enumerable.Range(0, count).Select(i => "@building" + i))}}) AND w.created_at <= @asOf
            ), events AS (
              SELECT h.work_order_id, h.action, h.occurred_at FROM work_order_history h JOIN scoped w ON w.id = h.work_order_id
              WHERE h.occurred_at <= @asOf AND h.action IN ('Reopen','Complete','Close','Cancel')
            ), ambiguous AS (
              SELECT DISTINCT work_order_id FROM events GROUP BY work_order_id, occurred_at HAVING COUNT(*) > 1
              UNION SELECT DISTINCT h.work_order_id FROM events h JOIN scoped w ON w.id = h.work_order_id WHERE h.action = 'Reopen' AND h.occurred_at = w.created_at
            ), starts AS (
              SELECT w.id, w.created_at AS started_at FROM scoped w WHERE NOT EXISTS (SELECT 1 FROM ambiguous a WHERE a.work_order_id = w.id)
              UNION ALL SELECT h.work_order_id, h.occurred_at FROM events h WHERE h.action = 'Reopen' AND NOT EXISTS (SELECT 1 FROM ambiguous a WHERE a.work_order_id = h.work_order_id)
            ), cycles AS (
              SELECT id, started_at, LEAD(started_at) OVER (PARTITION BY id ORDER BY started_at) AS ended_at FROM starts
            ), outcomes AS (
              SELECT c.*,
                (SELECT MIN(h.occurred_at) FROM events h WHERE h.work_order_id = c.id AND h.action = 'Complete' AND h.occurred_at >= c.started_at AND (c.ended_at IS NULL OR h.occurred_at < c.ended_at)) AS completed_at,
                (SELECT MIN(h.occurred_at) FROM events h WHERE h.work_order_id = c.id AND h.action = 'Close' AND h.occurred_at >= c.started_at AND (c.ended_at IS NULL OR h.occurred_at < c.ended_at)) AS closed_at,
                (SELECT MIN(h.occurred_at) FROM events h WHERE h.work_order_id = c.id AND h.action = 'Cancel' AND h.occurred_at >= c.started_at AND (c.ended_at IS NULL OR h.occurred_at < c.ended_at)) AS cancelled_at
              FROM cycles c
            ), durations AS (
              SELECT 'completion' AS kind, {{Seconds("started_at", "completed_at")}} AS seconds FROM outcomes WHERE completed_at >= started_at
              UNION ALL SELECT 'verification', {{Seconds("completed_at", "closed_at")}} FROM outcomes WHERE completed_at IS NOT NULL AND closed_at >= completed_at
            ), ranked AS (
              SELECT kind, seconds, ROW_NUMBER() OVER (PARTITION BY kind ORDER BY seconds) AS position, COUNT(*) OVER (PARTITION BY kind) AS samples FROM durations
            ), stats AS (
              SELECT kind, MAX(samples) AS samples,
                AVG(CASE WHEN position IN (FLOOR((samples + 1) / 2.0), FLOOR((samples + 2) / 2.0)) THEN seconds ELSE NULL END) AS median_seconds,
                MAX(CASE WHEN position = CEILING(samples * 0.95) THEN seconds ELSE NULL END) AS p95_seconds FROM ranked GROUP BY kind
            )
            SELECT COUNT(*), COALESCE(SUM(CASE WHEN completed_at IS NULL AND cancelled_at IS NULL THEN 1 ELSE 0 END),0),
              COALESCE(SUM(CASE WHEN cancelled_at IS NOT NULL THEN 1 ELSE 0 END),0), (SELECT COUNT(*) FROM ambiguous),
              COALESCE((SELECT samples FROM stats WHERE kind = 'completion'),0), (SELECT median_seconds FROM stats WHERE kind = 'completion'), (SELECT p95_seconds FROM stats WHERE kind = 'completion'),
              COALESCE((SELECT samples FROM stats WHERE kind = 'verification'),0), (SELECT median_seconds FROM stats WHERE kind = 'verification'), (SELECT p95_seconds FROM stats WHERE kind = 'verification') FROM outcomes
            """;
    }
}
