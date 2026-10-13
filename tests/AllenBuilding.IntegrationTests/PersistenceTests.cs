using AllenBuilding.Domain.Maintenance;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
            // Exercise upgrades with a real pre-delivery row, not only an empty database.
            await db.GetService<IMigrator>().MigrateAsync("MaintenanceWorkflow");
            var legacyUser = new OfficeUser { UserName = $"upgrade-{Guid.NewGuid():N}@example.test" };
            db.Users.Add(legacyUser); await db.SaveChangesAsync();
            var messageId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO account_messages (id, user_id, kind, protected_payload, created_at) VALUES ({messageId.ToString("D")}, {legacyUser.Id}, {"SecurityNotice"}, {"synthetic-legacy-payload"}, {now.UtcDateTime})");
            await db.Database.MigrateAsync();
            var upgraded = await db.AccountMessages.AsNoTracking().SingleAsync(x => x.Id == messageId);
            Assert.Equal(MessageState.Queued, upgraded.State); Assert.Equal(Guid.Empty, upgraded.LeaseToken);
            Assert.Equal(upgraded.CreatedAt, upgraded.NextAttemptAt);
            Assert.Null(upgraded.OriginTraceId); Assert.Null(upgraded.OriginSpanId); Assert.Null(upgraded.OriginOperationId);
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
        await VerifyProtectedIdentity(create);
        await MaintenanceContract.VerifyAsync(create);
        await NotificationContract.VerifyAsync(create);
        await KnowledgeContract.VerifyAsync(create);
        await BillingContract.VerifyAsync(create);
        await AccessContract.VerifyAsync(create);
        await MaintenanceQueueContract.VerifyAsync(create);
        await MaintenanceReportingContract.VerifyAsync(create);
        await TelemetryPersistenceContract.VerifyAsync(create);
    }

    private static async Task VerifyProtectedIdentity(Func<OfficeDbContext> create)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddScoped<OfficeDbContext>(_ => create());
        services.AddIdentityCore<OfficeUser>().AddRoles<IdentityRole>().AddEntityFrameworkStores<OfficeDbContext>().AddDefaultTokenProviders();
        services.Replace(ServiceDescriptor.Scoped<IUserStore<OfficeUser>, ProtectedUserStore>());
        await using var provider = services.BuildServiceProvider();
        string id; string code;
        using (var scope = provider.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
            var user = new OfficeUser { UserName = $"identity-{Guid.NewGuid():N}@example.test" };
            Assert.True((await users.CreateAsync(user, AccountTestHost.Password)).Succeeded); id = user.Id;
            Assert.True((await users.ResetAuthenticatorKeyAsync(user)).Succeeded);
            var key = await users.GetAuthenticatorKeyAsync(user);
            code = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 2))!.First();
            var values = await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().UserTokens.Where(x => x.UserId == id).Select(x => x.Value).ToListAsync();
            Assert.DoesNotContain(key, values); Assert.All(values, value => Assert.DoesNotContain(code, value));
        }
        using var first = provider.CreateScope(); using var second = provider.CreateScope();
        var a = first.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
        var b = second.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
        var original = (await a.FindByIdAsync(id))!; var stale = (await b.FindByIdAsync(id))!;
        Assert.True((await a.RedeemTwoFactorRecoveryCodeAsync(original, code)).Succeeded);
        Assert.False((await b.RedeemTwoFactorRecoveryCodeAsync(stale, code)).Succeeded);
        using var final = provider.CreateScope();
        var manager = final.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
        var saved = (await manager.FindByIdAsync(id))!;
        Assert.True(saved.MustReplaceRecoveryCodes); Assert.Equal(1, await manager.CountRecoveryCodesAsync(saved));
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
