using AllenBuilding.Domain.Maintenance;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.IntegrationTests;

public sealed class PersistenceTests
{
    [Fact]
    public async Task Sqlite_migrations_constraints_and_concurrency_preserve_private_ownership()
    {
        var path = Path.Combine(Path.GetTempPath(), $"office-check-{Guid.NewGuid():N}.db");
        OfficeDbContext Create() => new SqliteOfficeDbContext(new DbContextOptionsBuilder<SqliteOfficeDbContext>()
            .UseSqlite($"Data Source={path};Foreign Keys=True").Options);
        try { await VerifyDatabase(Create); }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }

    [MySqlFact]
    public async Task MySql_migrations_constraints_and_concurrency_match_local_contract()
    {
        var connection = Environment.GetEnvironmentVariable("OFFICE_TEST_MYSQL")!;
        var parsed = new MySql.Data.MySqlClient.MySqlConnectionStringBuilder(connection);
        Assert.Equal("office_check", parsed.Database);
        Assert.Contains(parsed.Server, new[] { "127.0.0.1", "localhost" });
        OfficeDbContext Create() => new MySqlOfficeDbContext(new DbContextOptionsBuilder<MySqlOfficeDbContext>()
            .UseMySQL(connection).Options);
        await VerifyDatabase(Create);
    }

    private static async Task VerifyDatabase(Func<OfficeDbContext> create)
    {
        var now = new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        var user = new OfficeUser { Id = Guid.NewGuid().ToString("N"), UserName = Guid.NewGuid().ToString("N") };
        var building = new Building { Name = "Synthetic test building" };
        var suite = new Suite { BuildingId = building.Id, Name = "Test suite" };
        var membership = Membership.Grant(suite.Id, user.Id, now.AddDays(-1));
        var request = WorkOrder.Submit(suite.Id, membership.Id, user.Id, "Leaking sink", "Water under basin", "Kitchen", now);
        await using (var db = create())
        {
            await db.Database.MigrateAsync();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            db.AddRange(user, building, suite, membership, request,
                new ActiveGrant { UserId = user.Id, SuiteId = suite.Id, MembershipId = membership.Id });
            await db.SaveChangesAsync();
        }
        await using (var db = create())
        {
            var active = await db.Memberships.Where(x => x.UserId == user.Id && x.RevokedAt == null && x.StartsAt <= now)
                .SingleAsync();
            Assert.Equal(membership.Id, active.Id);
            Assert.Equal(now, (await db.WorkOrders.SingleAsync(x => x.Id == request.Id)).CreatedAt);
            Assert.Equal(user.Id, (await db.WorkOrders.SingleAsync(x => x.Id == request.Id)).RequesterUserId);
        }
        await using (var first = create())
        await using (var stale = create())
        {
            var a = await first.WorkOrders.SingleAsync(x => x.Id == request.Id);
            var b = await stale.WorkOrders.SingleAsync(x => x.Id == request.Id);
            a.Triage(Priority.High, Trade.Plumbing, now); b.Triage(Priority.Low, Trade.General, now);
            await first.SaveChangesAsync();
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync());
        }
        await using (var db = create())
        {
            var duplicate = Membership.Grant(suite.Id, user.Id, now);
            db.Memberships.Add(duplicate);
            db.ActiveGrants.Add(new ActiveGrant { UserId = user.Id, SuiteId = suite.Id, MembershipId = duplicate.Id });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        await using (var db = create())
        {
            Assert.Equal(1, await db.Memberships.CountAsync(x => x.UserId == user.Id));
            db.WorkOrders.Add(WorkOrder.Submit(suite.Id, Guid.NewGuid(), user.Id, "Invalid original grant", "Description", "Kitchen", now));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        await using (var db = create())
        {
            var wrongRequester = new OfficeUser { Id = Guid.NewGuid().ToString("N"), UserName = Guid.NewGuid().ToString("N") };
            db.Users.Add(wrongRequester);
            await db.SaveChangesAsync();
            db.WorkOrders.Add(WorkOrder.Submit(suite.Id, membership.Id, wrongRequester.Id, "Mismatched owner", "Description", "Kitchen", now));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        await using (var db = create())
        {
            var grant = await db.Memberships.SingleAsync(x => x.Id == membership.Id);
            grant.Revoke("operator", "Membership ended", now);
            db.ActiveGrants.Remove(await db.ActiveGrants.SingleAsync(x => x.MembershipId == membership.Id));
            await db.SaveChangesAsync();
        }
        await using (var db = create())
        {
            Assert.Empty(await db.ActiveGrants.Where(x => x.UserId == user.Id).ToListAsync());
            Assert.False((await db.Memberships.SingleAsync(x => x.Id == membership.Id)).IsActive(now));
            Assert.Equal(WorkOrderStatus.Triaged, (await db.WorkOrders.SingleAsync(x => x.Id == request.Id)).Status);
        }
    }
}

public sealed class MySqlFactAttribute : FactAttribute
{
    public MySqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OFFICE_TEST_MYSQL")))
            Skip = "Requires the isolated MySQL check: ./scripts/verify-mysql.sh";
    }
}
