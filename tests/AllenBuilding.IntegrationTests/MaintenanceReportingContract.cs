using System.Data.Common;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using AllenBuilding.Application.Access;
using AllenBuilding.Application.Reporting;
using AllenBuilding.Domain.Maintenance;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AllenBuilding.IntegrationTests;

public static class MaintenanceReportingContract
{
    public static async Task VerifyAsync(Func<OfficeDbContext> create)
    {
        var now = new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        var manager = new OfficeUser { UserName = $"report-{Guid.NewGuid():N}" };
        var tenant = new OfficeUser { UserName = $"report-tenant-{Guid.NewGuid():N}" };
        var building = new Building { Name = "Report building" }; var empty = new Building { Name = "Empty report building" }; var foreign = new Building { Name = "Foreign private building" };
        var suite = new Suite { BuildingId = building.Id, Name = "Report suite" }; var foreignSuite = new Suite { BuildingId = foreign.Id, Name = "Private suite" };
        var membership = Membership.Grant(suite.Id, tenant.Id, now.AddYears(-2)); var foreignMembership = Membership.Grant(foreignSuite.Id, tenant.Id, now.AddYears(-2));
        WorkOrder Order(string title, DateTimeOffset created) => WorkOrder.Submit(suite.Id, membership.Id, tenant.Id, title, "Private description", "Office", created);
        var boundary = new[] { Order("New report", now), Order("Just under seven", now.AddDays(-7).AddSeconds(1)), Order("Exactly seven", now.AddDays(-7)), Order("Just under thirty", now.AddDays(-30).AddSeconds(1)), Order("Exactly thirty", now.AddDays(-30)) };
        var reopened = Order("Reopened original age", now.AddDays(-180)); reopened.Cancel("Originally withdrawn", now.AddDays(-179)); reopened.Reopen("Still needed", now.AddHours(-1));
        var completed = Order("Awaiting verification", now.AddDays(-40)); completed.Triage(Priority.High, Trade.General, now.AddDays(-39)); completed.Assign(manager.Id, null, now.AddDays(-38)); completed.Start(now.AddDays(-37)); completed.Complete("Ready", now.AddDays(-36));
        var closed = Order("Verified closed", now.AddDays(-60)); closed.Triage(Priority.High, Trade.General, now.AddDays(-59)); closed.Assign(manager.Id, null, now.AddDays(-58)); closed.Start(now.AddDays(-57)); closed.Complete("Ready", now.AddDays(-56)); closed.Close(now.AddDays(-55));
        var cancelled = Order("Cancelled", now.AddDays(-90)); cancelled.Cancel("Withdrawn", now.AddDays(-89));
        var future = Order("Future record excluded", now.AddHours(1));
        var filler = Enumerable.Range(0, 1000).Select(i => Order("Tied report " + i, now.AddDays(-2))).ToList();
        var own = boundary.Concat(new[] { reopened, completed, closed, cancelled, future }).Concat(filler).ToList();
        var outside = Enumerable.Range(0, 3000).Select(i => WorkOrder.Submit(foreignSuite.Id, foreignMembership.Id, tenant.Id, "Foreign private title " + i, "Private", "Office", now.AddDays(-100))).ToList();
        bool mysql; string connection;
        await using (var db = create())
        {
            mysql = db is MySqlOfficeDbContext; connection = db.Database.GetConnectionString()!;
            if (!mysql) await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL");
            db.AddRange(manager, tenant, building, empty, foreign, suite, foreignSuite, membership, foreignMembership,
                new BuildingManager { BuildingId = building.Id, UserId = manager.Id }, new BuildingManager { BuildingId = empty.Id, UserId = manager.Id });
            db.WorkOrders.AddRange(own.Concat(outside));
            foreach (var i in Enumerable.Range(0, 3)) db.WorkOrderHistory.Add(new() { WorkOrderId = reopened.Id, ActorId = manager.Id, Action = "Comment", Status = WorkOrderStatus.Submitted, Note = "Internal note", Visibility = HistoryVisibility.Internal, OccurredAt = now.AddMinutes(-i) });
            await db.SaveChangesAsync();
        }
        OfficeDbContext New(DbCommandInterceptor? interceptor = null)
        {
            if (mysql)
            {
                var options = new DbContextOptionsBuilder<MySqlOfficeDbContext>().UseMySQL(connection);
                if (interceptor is not null) options.AddInterceptors(interceptor);
                if (interceptor is ScopeInterference { ProveReadOnly: true } verification) options.AddInterceptors(verification.Proof);
                return new MySqlOfficeDbContext(options.Options);
            }
            var sqlite = new DbContextOptionsBuilder<SqliteOfficeDbContext>().UseSqlite(connection);
            if (interceptor is not null) sqlite.AddInterceptors(interceptor);
            return new SqliteOfficeDbContext(sqlite.Options);
        }
        var actor = new Actor(manager.Id, new HashSet<Guid> { building.Id, empty.Id }, true, false);
        async Task<MaintenanceAgingReport?> Read(MaintenanceAgingQuery query, Actor? selectedActor = null, DbCommandInterceptor? interceptor = null)
        {
            await using var db = New(interceptor);
            return await new ManagementReportQueries(new ReportActor(selectedActor ?? actor), new ManagementReportReader(db), new ReportClock(now)).MaintenanceAgingAsync(query);
        }
        var report = (await Read(new()))!;
        Assert.Equal(new MaintenanceAgingTotals(1002, 2, 2, 1), report.Totals); Assert.Equal(now, report.ObservedAt);
        Assert.Equal(50, report.Items.Count); Assert.True(report.HasNext);
        Assert.Equal(reopened.Id, report.Items[0].Id); Assert.Equal(new[] { "Empty report building", "Report building" }, report.Buildings.Select(x => x.Name));
        var all = new List<Guid>();
        for (var page = 1; page <= 21; page++) { var current = (await Read(new(Page: page)))!; all.AddRange(current.Items.Select(x => x.Id)); Assert.Equal(report.Totals, current.Totals); }
        Assert.Equal(boundary.Concat(new[] { reopened }).Concat(filler).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Select(x => x.Id), all);
        Assert.Equal(new[] { boundary[2].Id, boundary[3].Id }.OrderBy(id => own.Single(x => x.Id == id).CreatedAt), (await Read(new(Bucket: MaintenanceAgeBucket.From7To30Days)))!.Items.Select(x => x.Id));
        Assert.Equal(new[] { reopened.Id, boundary[4].Id }, (await Read(new(Bucket: MaintenanceAgeBucket.AtLeast30Days)))!.Items.Select(x => x.Id));
        Assert.Equal(new[] { completed.Id }, (await Read(new(Bucket: MaintenanceAgeBucket.AwaitingVerification)))!.Items.Select(x => x.Id));
        var emptyReport = (await Read(new(BuildingId: empty.Id)))!; Assert.Equal(0, emptyReport.Totals.Active); Assert.Empty(emptyReport.Items);
        Assert.Null(await Read(new(BuildingId: foreign.Id))); Assert.Null(await Read(new(), actor with { ChallengedMfa = false }));
        Assert.Null(await Read(new(), new Actor(tenant.Id, new HashSet<Guid>(), false, false)));
        foreach (var invalid in new[] { new MaintenanceAgingQuery(BuildingId: Guid.Empty), new(Bucket: (MaintenanceAgeBucket)999), new(Page: 0), new(Page: 1001) }) Assert.Null(await Read(invalid));
        // Change a record after the first consistent read; totals and items must still use that snapshot.
        var interference = new ScopeInterference(async () =>
        {
            await using var db = New(); var order = await db.WorkOrders.SingleAsync(x => x.Id == boundary[4].Id);
            order.Cancel("Changed concurrently", now); await db.SaveChangesAsync();
        }, mysql);
        var snapshot = (await Read(new(Bucket: MaintenanceAgeBucket.AtLeast30Days), interceptor: interference))!;
        Assert.True(interference.Ran); if (mysql) Assert.True(interference.ReadOnlyEnforced);
        Assert.Equal(report.Totals, snapshot.Totals); Assert.Contains(snapshot.Items, x => x.Id == boundary[4].Id);
        Assert.Equal(1, (await Read(new()))!.Totals.AtLeast30Days);
        await RecordEvidence(New, actor, building.Id, now, own.Count, outside.Count, Read);
        // Revocation after snapshot acquisition discards the protected response despite a stale actor adapter.
        var revoke = new ScopeInterference(async () =>
        {
            await using var db = New(); db.BuildingManagers.Remove(await db.BuildingManagers.SingleAsync(x => x.UserId == manager.Id && x.BuildingId == building.Id)); await db.SaveChangesAsync();
        }, false);
        Assert.Null(await Read(new(), interceptor: revoke)); Assert.True(revoke.Ran);
        Assert.Null(await Read(new(BuildingId: building.Id)));
    }

    private static async Task RecordEvidence(Func<DbCommandInterceptor?, OfficeDbContext> create, Actor actor, Guid building, DateTimeOffset now, int ownCount, int foreignCount,
        Func<MaintenanceAgingQuery, Actor?, DbCommandInterceptor?, Task<MaintenanceAgingReport?>> read)
    {
        var evidence = new List<object>();
        await using var db = create(null); await db.Database.OpenConnectionAsync();
        var mysql = db is MySqlOfficeDbContext; var connection = db.Database.GetDbConnection();
        var databaseRows = await db.WorkOrders.CountAsync();
        // Refresh statistics before measurement, only in this disposable synthetic database.
        await using (var stats = connection.CreateCommand()) { stats.CommandText = mysql ? "ANALYZE TABLE work_orders, suites, building_managers" : "ANALYZE"; await stats.ExecuteNonQueryAsync(); }
        var samples = new List<double>();
        MaintenanceAgingReport? latest = null;
        for (var i = 0; i < 21; i++)
        {
            var timer = Stopwatch.StartNew(); latest = await read(new(BuildingId: building), null, null); Assert.NotNull(latest);
            if (i > 0) samples.Add(timer.Elapsed.TotalMilliseconds);
        }
        samples.Sort();
        foreach (var sql in new[] { MaintenanceAgingSql.Totals(1), MaintenanceAgingSql.Items(1, MaintenanceAgeBucket.AtLeast30Days) })
        {
            var plan = new List<string>();
            await using var command = ManagementReportReader.Command(connection, null, (mysql ? "EXPLAIN ANALYZE " : "EXPLAIN QUERY PLAN ") + sql, new[] { building }, now);
            if (sql.Contains("items", StringComparison.Ordinal)) { ManagementReportReader.Add(command, "@limit", 51, System.Data.DbType.Int32); ManagementReportReader.Add(command, "@offset", 0, System.Data.DbType.Int32); }
            await using var rows = await command.ExecuteReaderAsync();
            while (await rows.ReadAsync()) plan.Add(string.Join(" | ", Enumerable.Range(0, rows.FieldCount).Select(i => Convert.ToString(rows.GetValue(i)))));
            Assert.NotEmpty(plan); evidence.Add(new { Sql = sql, Plan = plan });
        }
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AllenXeniaOffice.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var output = Path.Combine(directory.FullName, "artifacts", "reports"); Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, mysql ? "maintenance-aging-mysql.json" : "maintenance-aging-sqlite.json"), JsonSerializer.Serialize(new
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Provider = db.Database.ProviderName,
            Server = connection.ServerVersion,
            OS = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Runtime = RuntimeInformation.FrameworkDescription,
            LogicalProcessors = Environment.ProcessorCount,
            Population = new { ScopedRequests = ownCount, ForeignFixtureRequests = foreignCount, TotalDatabaseRequests = databaseRows, UnscopedDatabaseRequests = databaseRows - ownCount, ScopedBuildings = actor.ManagedBuildings.Count, ConcurrentReportClients = 1 },
            Distribution = latest!.Totals,
            Measurement = "20 sequential warmed full report reads; nearest-rank percentiles; existing indexes; other test/tooling load uncontrolled; no before/after optimization or production capacity claim",
            P50Milliseconds = samples[9],
            P95Milliseconds = samples[18],
            SamplesMilliseconds = samples,
            Queries = evidence
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private sealed class ReportClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class ReportActor(Actor actor) : ICurrentActor { public Task<Actor?> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult<Actor?>(actor); }
    private sealed class ReadOnlyProof : DbTransactionInterceptor
    {
        public bool Checked { get; private set; }
        public override async ValueTask<DbTransaction> TransactionUsedAsync(DbConnection connection, TransactionEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
        {
            await using var write = connection.CreateCommand(); write.Transaction = result; write.CommandText = "UPDATE work_orders SET title = title WHERE 1 = 0";
            var rejected = await Assert.ThrowsAsync<MySql.Data.MySqlClient.MySqlException>(() => write.ExecuteNonQueryAsync(cancellationToken));
            Assert.Equal(1792, rejected.Number); Checked = true; return result;
        }
    }
    private sealed class ScopeInterference(Func<Task> change, bool proveReadOnly) : DbCommandInterceptor
    {
        public bool Ran { get; private set; }
        public bool ProveReadOnly => proveReadOnly;
        public ReadOnlyProof Proof { get; } = new();
        public bool ReadOnlyEnforced => Proof.Checked;
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!Ran && command.Transaction is not null && command.CommandText.Contains("building_managers", StringComparison.Ordinal))
            {
                Ran = true;
                await change();
            }
            return result;
        }
    }
}
