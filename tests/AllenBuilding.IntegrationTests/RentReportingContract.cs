using System.Data.Common;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using AllenBuilding.Application.Access;
using AllenBuilding.Application.Reporting;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AllenBuilding.IntegrationTests;

public static class RentReportingContract
{
    public static async Task VerifyAsync(Func<OfficeDbContext> create)
    {
        // UTC has crossed midnight but the building's due-date boundary has not.
        var now = new DateTimeOffset(2026, 10, 13, 3, 30, 0, TimeSpan.Zero); var today = new DateOnly(2026, 10, 12);
        var manager = new OfficeUser { UserName = "rent-report-manager-" + Guid.NewGuid().ToString("N") };
        var payer = new OfficeUser { UserName = "rent-report-payer-" + Guid.NewGuid().ToString("N") };
        var own = new Building { Name = "Rent report building" }; var empty = new Building { Name = "Empty rent building" }; var foreign = new Building { Name = "Foreign financial building" };
        var suite = new Suite { BuildingId = own.Id, Name = "Rent report suite" }; var otherSuite = new Suite { BuildingId = foreign.Id, Name = "Foreign financial suite" };
        var member = Membership.Grant(suite.Id, payer.Id, now.AddYears(-2)); member.Revoke(manager.Id, "Ended tenancy still owes debt", now.AddDays(-1));
        var otherMember = Membership.Grant(otherSuite.Id, payer.Id, now.AddYears(-1));
        var account = RentAccount.Open(Guid.NewGuid(), member.Id); var otherAccount = RentAccount.Open(Guid.NewGuid(), otherMember.Id);
        var binding = BillingCustomerBinding.Create(account.Id, "acct_rent_report", false, "cus_rent_report", now.AddDays(-10));
        var rows = new List<object> { manager, payer, own, empty, foreign, suite, otherSuite, member, otherMember, account, otherAccount, binding,
            new BuildingManager { BuildingId = own.Id, UserId = manager.Id }, new BuildingManager { BuildingId = empty.Id, UserId = manager.Id } };
        Obligation Charge(long amount, string label, DateOnly due)
        { var o = Obligation.Issue(account.Id, new Money(amount), label, due, manager.Id, now.AddDays(-5)); rows.Add(o); return o; }
        var aged = new[] { 1, 30, 31, 60, 61, 90, 91, 900 }.Select(days => Charge(100, "Age boundary " + days, today.AddDays(-days))).ToArray();
        var dueToday = Charge(1000, "Due today", today); var future = Charge(6000, "Not yet overdue", today.AddDays(1));
        var small = Charge(100, "Small balance", today.AddDays(-2)); rows.Add(ObligationCredit.RecordLocal(Guid.NewGuid(), manager.Id, new(small.Id, account.Id, small.Version, 100), 75, ObligationCreditKind.Credit, "Small remainder", now));
        var partial = Charge(10000, "<script>private-rent()</script>", today.AddDays(-2));
        foreach (var amount in new[] { 1000, 2000 }) rows.Add(ObligationCredit.RecordLocal(Guid.NewGuid(), manager.Id, new(partial.Id, account.Id, partial.Version, 10000), amount, ObligationCreditKind.Credit, "Applied credit", now));
        (PaymentAttempt Attempt, SettlementPayment Capture) Pay(Obligation o, long amount)
        {
            var a = PaymentAttempt.Admit(Guid.NewGuid(), payer.Id, binding, [new(o.Id, account.Id, o.Version, amount)], now.AddDays(-1)); a.BeginDispatch(now.AddDays(-1));
            var fact = new CanonicalSettlement(binding.ProviderAccount, false, binding.CustomerId, "cs_" + a.Id.ToString("N"), "pi_" + a.Id.ToString("N"), amount, "USD");
            var capture = SettlementPayment.Record(a, fact, now); rows.Add(a); rows.Add(capture); return (a, capture);
        }
        var first = Pay(partial, 3000); var second = Pay(partial, 2000);
        foreach (var (capture, amount, disposition) in new[] { (first, 500L, RefundDisposition.CreditDebt), (second, 750L, RefundDisposition.RetainDebt) })
        {
            var movement = CashMovement.Record(capture.Capture, capture.Attempt, new(binding.ProviderAccount, false, account.Id, capture.Attempt.Id, binding.CustomerId,
                capture.Attempt.PaymentId!, "re_" + capture.Attempt.Id.ToString("N"), "txn_" + capture.Attempt.Id.ToString("N"), CashMovementKind.Refund, -amount, "USD", now), now);
            var allocation = RefundAllocation.Record(movement, capture.Capture.Allocations.Single(), partial.Version, amount, capture.Capture.AmountMinorUnits);
            var review = CashMovementReview.Open(movement); var op = Guid.NewGuid(); review.ApplyRefund(movement, op, manager.Id, disposition, "Reviewed refund", now);
            rows.AddRange([movement, allocation, review]);
            if (disposition == RefundDisposition.CreditDebt) rows.Add(ObligationCredit.RecordRefundCredit(op, manager.Id, allocation, 7000, "Paired credit", now));
        }
        var dispute = CashMovement.Record(second.Capture, second.Attempt, new(binding.ProviderAccount, false, account.Id, second.Attempt.Id, binding.CustomerId, second.Attempt.PaymentId!,
            "dp_report", "txn_dispute_report", CashMovementKind.DisputeWithdrawal, -400, "USD", now), now);
        rows.AddRange([dispute, CashMovementReview.Open(dispute), FinancialException.Open(second.Attempt, FinancialFailure.ExternalCashMovement, "Paid", now)]);
        var held = Charge(80, "Guarded charge", today.AddDays(-1)); var admitted = PaymentAttempt.Admit(Guid.NewGuid(), payer.Id, binding, [new(held.Id, account.Id, held.Version, 80)], now);
        rows.AddRange([admitted, new CollectionGuard { ObligationId = held.Id, AccountId = account.Id, AttemptId = admitted.Id }]);
        var paid = Charge(500, "Paid charge", today.AddDays(-2)); Pay(paid, 500);
        var fillers = Enumerable.Range(0, 500).Select(i => Charge(100, "Tie charge " + i, today.AddDays(-5))).ToArray();
        rows.Add(Obligation.Issue(account.Id, new Money(999999), "Future issue excluded", today.AddYears(-1), manager.Id, now.AddHours(1)));
        for (var i = 0; i < 1500; i++) rows.Add(Obligation.Issue(otherAccount.Id, new Money(100000), "Foreign financial private " + i, today.AddYears(-1), manager.Id, now.AddDays(-1)));
        await using (var db = create()) { db.AddRange(rows); await db.SaveChangesAsync(); }
        var actor = new Actor(manager.Id, new HashSet<Guid> { own.Id, empty.Id }, true, false);
        async Task<RentBalanceReport?> Read(RentBalanceQuery query, Actor? selected = null)
        { await using var db = create(); return await new ManagementReportQueries(new Current(selected ?? actor), new ManagementReportReader(db), new Clock(now)).RentBalancesAsync(query); }
        var report = (await Read(new()))!;
        Assert.Equal(today, report.LocalDate); Assert.Equal(now, report.ObservedAt); Assert.Equal(513, report.Totals.Count);
        Assert.Equal(60655, report.Totals.ConfirmedMinor); Assert.Equal(53655, report.Totals.OverdueMinor); Assert.Equal(2830, report.Totals.HeldMinor);
        Assert.Equal(53055, report.Totals.From1To30DaysMinor); Assert.Equal(200, report.Totals.From31To60DaysMinor); Assert.Equal(200, report.Totals.From61To90DaysMinor); Assert.Equal(200, report.Totals.Over90DaysMinor);
        Assert.Equal(1, report.Totals.OpenExceptions); Assert.Equal(1250, report.Totals.UnresolvedRefundMinor); Assert.Equal(400, report.Totals.UnresolvedDisputeWithdrawalMinor);
        var all = new List<RentBalanceItem>();
        for (var page = 1; page <= 11; page++) { var r = (await Read(new(Page: page)))!; all.AddRange(r.Items); Assert.Equal(report.Totals, r.Totals); Assert.Equal(page < 11, r.HasNext); }
        Assert.Equal(513, all.Select(x => x.ObligationId).Distinct().Count()); Assert.Equal(2750, all.Single(x => x.ObligationId == partial.Id).ConfirmedMinor);
        Assert.True(all.Single(x => x.ObligationId == partial.Id).Held); Assert.Contains(all, x => x.ObligationId == small.Id && x.ConfirmedMinor == 25);
        Assert.DoesNotContain(all, x => x.ObligationId == paid.Id); Assert.DoesNotContain(all, x => x.Description.Contains("Foreign", StringComparison.Ordinal));
        Assert.Equal(all.OrderBy(x => x.DueOn).ThenBy(x => x.ObligationId).Select(x => x.ObligationId), all.Select(x => x.ObligationId));
        var heldReport = (await Read(new(Group: RentBalanceGroup.Held)))!; Assert.Equal(2, heldReport.Items.Count); Assert.All(heldReport.Items, x => Assert.True(x.Held));
        var overdue = (await Read(new(Group: RentBalanceGroup.Overdue)))!; Assert.All(overdue.Items, x => Assert.True(x.DueOn < today));
        var emptyReport = (await Read(new(BuildingId: empty.Id)))!; Assert.Equal(0, emptyReport.Totals.Count); Assert.Equal(0, emptyReport.Totals.OpenExceptions); Assert.Empty(emptyReport.Items);
        Assert.Null(await Read(new(BuildingId: foreign.Id))); Assert.Null(await Read(new(), actor with { ChallengedMfa = false }));
        Assert.Null(await Read(new(), actor with { UserId = payer.Id }));
        foreach (var invalid in new[] { new RentBalanceQuery(BuildingId: Guid.Empty), new(Group: (RentBalanceGroup)999), new(Page: 0), new(Page: 1001) }) Assert.Null(await Read(invalid));
        // A concurrent debt credit must not mix into only one half of the read snapshot.
        string connection; bool mysql;
        await using (var db = create()) { connection = db.Database.GetConnectionString()!; mysql = db is MySqlOfficeDbContext; }
        OfficeDbContext Intercept(ReportInterference hook)
        {
            return mysql ? new MySqlOfficeDbContext(new DbContextOptionsBuilder<MySqlOfficeDbContext>().UseMySQL(connection).AddInterceptors(hook).Options)
                : new SqliteOfficeDbContext(new DbContextOptionsBuilder<SqliteOfficeDbContext>().UseSqlite(connection).AddInterceptors(hook).Options);
        }
        var creditDuringRead = new ReportInterference(async () =>
        {
            await using var db = create(); db.ObligationCredits.Add(ObligationCredit.RecordLocal(Guid.NewGuid(), manager.Id,
                new(aged[0].Id, account.Id, aged[0].Version, 100), 100, ObligationCreditKind.WriteOff, "Concurrent report test", now)); await db.SaveChangesAsync();
        });
        await using (var db = Intercept(creditDuringRead))
        {
            var snapshot = (await new ManagementReportQueries(new Current(actor), new ManagementReportReader(db), new Clock(now)).RentBalancesAsync(new()))!;
            Assert.True(creditDuringRead.Ran); Assert.Equal(report.Totals, snapshot.Totals);
            var heldSnapshot = (await new ManagementReportQueries(new Current(actor), new ManagementReportReader(db), new Clock(now)).RentBalancesAsync(new(Group: RentBalanceGroup.Held)))!;
            Assert.Equal(report.Totals.ConfirmedMinor - 100, heldSnapshot.Totals.ConfirmedMinor);
        }
        await Evidence(create, Read, own.Id, now, today);
        // Removed grants discard a response even when the original actor adapter is stale.
        var revokeDuringRead = new ReportInterference(async () =>
        {
            await using var db = create(); db.BuildingManagers.Remove(await db.BuildingManagers.SingleAsync(x => x.UserId == manager.Id && x.BuildingId == empty.Id)); await db.SaveChangesAsync();
        });
        await using (var db = Intercept(revokeDuringRead))
        {
            Assert.Null(await new ManagementReportQueries(new Current(actor), new ManagementReportReader(db), new Clock(now)).RentBalancesAsync(new()));
            Assert.True(revokeDuringRead.Ran);
        }
        await using (var db = create()) { db.BuildingManagers.Remove(await db.BuildingManagers.SingleAsync(x => x.UserId == manager.Id && x.BuildingId == own.Id)); await db.SaveChangesAsync(); }
        Assert.Null(await Read(new(BuildingId: own.Id)));
    }
    private static async Task Evidence(Func<OfficeDbContext> create, Func<RentBalanceQuery, Actor?, Task<RentBalanceReport?>> read, Guid building, DateTimeOffset now, DateOnly today)
    {
        await using var db = create(); await db.Database.OpenConnectionAsync(); var connection = db.Database.GetDbConnection(); var mysql = db is MySqlOfficeDbContext;
        await using (var stats = connection.CreateCommand()) { stats.CommandText = mysql ? "ANALYZE TABLE obligations, payment_allocations, refund_allocations, obligation_credits, rent_accounts, memberships" : "ANALYZE"; await stats.ExecuteNonQueryAsync(); }
        var samples = new List<double>(); for (var i = 0; i < 21; i++) { var timer = Stopwatch.StartNew(); Assert.NotNull(await read(new(BuildingId: building), null)); if (i > 0) samples.Add(timer.Elapsed.TotalMilliseconds); }
        samples.Sort();
        var evidence = new List<object>();
        foreach (var sql in new[] { RentBalanceSql.Totals(1), RentBalanceSql.Items(1, RentBalanceGroup.Held) })
        {
            var plan = new List<string>(); await using var command = ManagementReportReader.BalanceCommand(connection, null, (mysql ? "EXPLAIN ANALYZE " : "EXPLAIN QUERY PLAN ") + sql, [building], now, today);
            if (sql.Contains("rent-balances-items", StringComparison.Ordinal)) { ManagementReportReader.Add(command, "@limit", 51, System.Data.DbType.Int32); ManagementReportReader.Add(command, "@offset", 0, System.Data.DbType.Int32); }
            await using var results = await command.ExecuteReaderAsync(); while (await results.ReadAsync()) plan.Add(string.Join(" | ", Enumerable.Range(0, results.FieldCount).Select(i => Convert.ToString(results.GetValue(i)))));
            Assert.NotEmpty(plan); evidence.Add(new { Sql = sql, Plan = plan });
        }
        var directory = new DirectoryInfo(AppContext.BaseDirectory); while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AllenXeniaOffice.slnx"))) directory = directory.Parent; Assert.NotNull(directory);
        var output = Path.Combine(directory.FullName, "artifacts", "reports"); Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, mysql ? "rent-balances-mysql.json" : "rent-balances-sqlite.json"), JsonSerializer.Serialize(new
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Provider = db.Database.ProviderName,
            Server = connection.ServerVersion,
            OS = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Runtime = RuntimeInformation.FrameworkDescription,
            LogicalProcessors = Environment.ProcessorCount,
            Population = new { ScopedObligations = 515, FutureIssued = 1, ForeignObligations = 1500, TotalDatabaseObligations = await db.Obligations.CountAsync(), ConcurrentReportClients = 1 },
            Measurement = "20 sequential warmed full report reads; nearest-rank percentiles; existing indexes; other tooling load uncontrolled; no before/after or production capacity claim",
            P50Milliseconds = samples[9],
            P95Milliseconds = samples[18],
            SamplesMilliseconds = samples,
            Queries = evidence
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private sealed class ReportInterference(Func<Task> change) : DbCommandInterceptor
    {
        public bool Ran { get; private set; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!Ran && command.Transaction is not null && command.CommandText.Contains("building_managers", StringComparison.Ordinal))
            { Ran = true; await change(); }
            return result;
        }
    }
    private sealed class Current(Actor actor) : ICurrentActor { public Task<Actor?> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult<Actor?>(actor); }
    private sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
}
