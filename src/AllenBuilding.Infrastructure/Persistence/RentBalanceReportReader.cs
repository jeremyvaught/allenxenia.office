using System.Data;
using System.Data.Common;
using AllenBuilding.Application.Access;
using AllenBuilding.Application.Reporting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Infrastructure.Persistence;

public sealed partial class ManagementReportReader
{
    public async Task<RentBalanceReport?> RentBalancesAsync(Actor actor, RentBalanceQuery query, DateTimeOffset observedAt, DateOnly localDate, CancellationToken cancellationToken)
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
            await using var native = connection is SqliteConnection sqlite ? sqlite.BeginTransaction(IsolationLevel.Serializable, deferred: true)
                : await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
            RentBalanceReport report;
            await using (var transaction = await db.Database.UseTransactionAsync(native, cancellationToken))
            {
                var buildings = await ScopeAsync(actor, cancellationToken);
                if (buildings.Count is 0 or > 100 || query.BuildingId is { } requested && !buildings.Any(x => x.Id == requested)) return null;
                var scope = buildings.Where(x => query.BuildingId == null || x.Id == query.BuildingId).Select(x => x.Id).ToArray();
                RentBalanceTotals totals;
                await using (var command = BalanceCommand(connection, native, RentBalanceSql.Totals(scope.Length), scope, observedAt, localDate))
                await using (var rows = await command.ExecuteReaderAsync(cancellationToken))
                {
                    await rows.ReadAsync(cancellationToken);
                    long Value(int i) => Convert.ToInt64(rows.GetValue(i));
                    totals = new(Value(0), Value(1), Value(2), Value(3), Value(4), Value(5), Value(6), Value(7), Value(8), Value(9), Value(10));
                }
                var items = new List<RentBalanceItem>();
                await using (var command = BalanceCommand(connection, native, RentBalanceSql.Items(scope.Length, query.Group), scope, observedAt, localDate))
                {
                    Add(command, "@limit", RentBalanceQuery.PageSize + 1, DbType.Int32); Add(command, "@offset", (query.Page - 1) * RentBalanceQuery.PageSize, DbType.Int32);
                    await using var rows = await command.ExecuteReaderAsync(cancellationToken);
                    while (await rows.ReadAsync(cancellationToken))
                        items.Add(new(Guid.Parse(rows.GetString(0)), Guid.Parse(rows.GetString(1)), rows.GetString(2), rows.GetString(3), rows.GetString(4),
                            DateOnly.ParseExact(rows.GetString(5), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), Convert.ToInt64(rows.GetValue(6)), Convert.ToInt64(rows.GetValue(7)) != 0));
                }
                report = new(query, observedAt, localDate, totals, items.Take(RentBalanceQuery.PageSize).ToArray(), items.Count > RentBalanceQuery.PageSize && query.Page < 1000, buildings);
                await native.CommitAsync(cancellationToken);
            }
            var fresh = await ScopeAsync(actor, cancellationToken);
            return report.Buildings.All(x => fresh.Any(y => x.Id == y.Id)) ? report : null;
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }
    internal static DbCommand BalanceCommand(DbConnection connection, DbTransaction? transaction, string sql, IReadOnlyList<Guid> scope, DateTimeOffset now, DateOnly localDate)
    {
        var command = Command(connection, transaction, sql, scope, now);
        Add(command, "@localDate", localDate.ToString("yyyy-MM-dd"), DbType.String);
        foreach (var days in new[] { 30, 60, 90 }) Add(command, "@days" + days, localDate.AddDays(-days).ToString("yyyy-MM-dd"), DbType.String);
        return command;
    }
}

internal static class RentBalanceSql
{
    // Independently aggregate each child relation before joining. No active-grant filter: ended tenancies still owe debt.
    private static string Population(int count) => $$"""
        WITH scoped_accounts AS (
          SELECT a.id, b.name AS building, s.name AS suite FROM rent_accounts a
          JOIN memberships m ON m.id = a.membership_id JOIN suites s ON s.id = m.suite_id JOIN buildings b ON b.id = s.building_id
          WHERE s.building_id IN ({{string.Join(",", Enumerable.Range(0, count).Select(i => "@building" + i))}})
        ), scoped_obligations AS (
          SELECT o.*, a.building, a.suite FROM obligations o JOIN scoped_accounts a ON a.id = o.account_id WHERE o.issued_at <= @asOf
        ), credits AS (
          SELECT c.obligation_id, SUM(c.amount_minor_units) AS amount FROM obligation_credits c
          JOIN scoped_obligations o ON o.id = c.obligation_id GROUP BY c.obligation_id
        ), paid AS (
          SELECT p.obligation_id, SUM(p.amount_minor_units) AS amount FROM payment_allocations p
          JOIN scoped_obligations o ON o.id = p.obligation_id GROUP BY p.obligation_id
        ), returned AS (
          SELECT r.obligation_id, SUM(r.signed_minor_units) AS amount FROM refund_allocations r
          JOIN scoped_obligations o ON o.id = r.obligation_id GROUP BY r.obligation_id
        ), balances AS (
          SELECT o.*, o.amount_minor_units - COALESCE(c.amount,0) - COALESCE(p.amount,0) - COALESCE(r.amount,0) AS balance,
            CASE WHEN EXISTS (SELECT 1 FROM collection_guards g WHERE g.obligation_id = o.id)
              OR EXISTS (SELECT 1 FROM tenancy_closures c WHERE c.account_id = o.account_id AND c.state <> 'Verified')
              OR EXISTS (SELECT 1 FROM financial_exceptions e WHERE e.account_id = o.account_id AND e.state <> 'Resolved')
              OR EXISTS (SELECT 1 FROM cash_movements m WHERE m.account_id = o.account_id AND NOT EXISTS
                         (SELECT 1 FROM cash_movement_reviews v WHERE v.movement_id = m.id AND v.state = 'Classified'
                          AND v.verified_at IS NOT NULL AND v.agreement_hash IS NOT NULL AND LENGTH(v.agreement_hash) = 64))
              OR EXISTS (SELECT 1 FROM cash_source_work w WHERE w.account_id = o.account_id AND w.state <> 'Completed')
              OR EXISTS (SELECT 1 FROM collection_work w JOIN payment_attempts a ON a.id = w.attempt_id
                         WHERE a.account_id = o.account_id AND w.read_only_reconciliation = 1 AND w.state <> 'Completed')
              OR EXISTS (SELECT 1 FROM payment_attempt_items i JOIN payment_attempts a ON a.id = i.attempt_id
                         WHERE i.obligation_id = o.id AND a.state NOT IN ('Settled', 'Uncollected')
                          AND (a.state = 'NeedsReview' OR NOT EXISTS (SELECT 1 FROM collection_guards g WHERE g.obligation_id = o.id)))
              THEN 1 ELSE 0 END AS held
          FROM scoped_obligations o LEFT JOIN credits c ON c.obligation_id = o.id
          LEFT JOIN paid p ON p.obligation_id = o.id LEFT JOIN returned r ON r.obligation_id = o.id
        )
        """;
    internal static string Totals(int count) => Population(count) + """
        /* rent-balances-totals */
        SELECT COALESCE(SUM(CASE WHEN balance > 0 THEN 1 ELSE 0 END),0),
          COALESCE(SUM(CASE WHEN balance > 0 THEN balance ELSE 0 END),0),
          COALESCE(SUM(CASE WHEN balance > 0 AND due_on < @localDate THEN balance ELSE 0 END),0),
          COALESCE(SUM(CASE WHEN balance > 0 AND held = 1 THEN balance ELSE 0 END),0),
          COALESCE(SUM(CASE WHEN balance > 0 AND due_on < @localDate AND due_on >= @days30 THEN balance ELSE 0 END),0),
          COALESCE(SUM(CASE WHEN balance > 0 AND due_on < @days30 AND due_on >= @days60 THEN balance ELSE 0 END),0),
          COALESCE(SUM(CASE WHEN balance > 0 AND due_on < @days60 AND due_on >= @days90 THEN balance ELSE 0 END),0),
          COALESCE(SUM(CASE WHEN balance > 0 AND due_on < @days90 THEN balance ELSE 0 END),0),
          (SELECT COUNT(*) FROM financial_exceptions e JOIN scoped_accounts a ON a.id = e.account_id WHERE e.state <> 'Resolved'),
          (SELECT COALESCE(SUM(-m.signed_minor_units),0) FROM cash_movements m JOIN cash_movement_reviews v ON v.movement_id = m.id
             JOIN scoped_accounts a ON a.id = m.account_id WHERE m.kind = 'Refund' AND v.state <> 'Classified'),
          (SELECT COALESCE(SUM(-m.signed_minor_units),0) FROM cash_movements m JOIN cash_movement_reviews v ON v.movement_id = m.id
             JOIN scoped_accounts a ON a.id = m.account_id WHERE m.kind = 'DisputeWithdrawal' AND v.state <> 'Classified')
        FROM balances
        """;
    internal static string Items(int count, RentBalanceGroup group) => Population(count) + $$"""
        /* rent-balances-items */
        SELECT id, account_id, building, suite, description, CAST(due_on AS CHAR), balance, held
        FROM balances WHERE balance > 0 {{group switch { RentBalanceGroup.Overdue => "AND due_on < @localDate", RentBalanceGroup.Held => "AND held = 1", _ => "" }}}
        ORDER BY due_on, id LIMIT @limit OFFSET @offset
        """;
}
