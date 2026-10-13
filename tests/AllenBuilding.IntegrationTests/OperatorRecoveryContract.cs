using AllenBuilding.Application.Access;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Notifications;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AllenBuilding.IntegrationTests;

public static class OperatorRecoveryContract
{
    public static async Task VerifyAsync(Func<OfficeDbContext> create)
    {
        ServiceProvider Services(Func<OfficeDbContext> factory)
        {
            var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton(TimeProvider.System);
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider()); services.AddScoped<OfficeDbContext>(_ => factory());
            services.AddIdentityCore<OfficeUser>().AddRoles<IdentityRole>().AddEntityFrameworkStores<OfficeDbContext>().AddDefaultTokenProviders();
            services.Replace(ServiceDescriptor.Scoped<IUserStore<OfficeUser>, ProtectedUserStore>());
            services.AddScoped<ICurrentActor, TestActor>(); services.AddScoped<AccountInvitations>(); return services.BuildServiceProvider();
        }
        await using var provider = Services(create); var building = new Building { Name = "Operator recovery contract" };
        string id; string rollbackId; string tenantId; string unconfirmedId;
        async Task<string> Seed(bool manager = true, bool confirmed = true)
        {
            using var scope = provider.CreateScope(); var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
            var email = "operator-recovery-" + Guid.NewGuid().ToString("N") + "@example.test";
            var user = new OfficeUser { UserName = email, Email = email, EmailConfirmed = confirmed };
            Assert.True((await users.CreateAsync(user, AccountTestHost.Password)).Succeeded);
            Assert.True((await users.ResetAuthenticatorKeyAsync(user)).Succeeded); Assert.True((await users.SetTwoFactorEnabledAsync(user, true)).Succeeded);
            Assert.NotNull(await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 2));
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            if (manager) db.BuildingManagers.Add(new BuildingManager { BuildingId = building.Id, UserId = user.Id });
            await db.SaveChangesAsync(); return user.Id;
        }
        await using (var db = create()) { db.Buildings.Add(building); await db.SaveChangesAsync(); }
        id = await Seed(); rollbackId = await Seed(); tenantId = await Seed(manager: false); unconfirmedId = await Seed(confirmed: false);
        async Task<(string Stamp, string Password, string Seed, int Codes)> Snapshot(string target)
        {
            using var scope = provider.CreateScope(); var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>(); var user = (await users.FindByIdAsync(target))!;
            return (user.SecurityStamp!, user.PasswordHash!, (await users.GetAuthenticatorKeyAsync(user))!, await users.CountRecoveryCodesAsync(user));
        }
        var original = await Snapshot(id); var rollback = await Snapshot(rollbackId);
        async Task<AccessCode> Recover(ServiceProvider source, RecoverManagerFactor command, OperatorRecoveryPolicy? policy = null)
        {
            using var scope = source.CreateScope(); var services = scope.ServiceProvider;
            return await new OperatorMfaRecovery(services.GetRequiredService<OfficeDbContext>(), services.GetRequiredService<UserManager<OfficeUser>>(),
                services.GetRequiredService<AccountInvitations>(), policy ?? new(true, Environment.UserName)).ExecuteAsync(command);
        }
        RecoverManagerFactor Command(string target, string stamp) => new(target, stamp, "owner-verification-record-42", "Lost device independently verified against established owner contact", new string('A', 32));
        var reviewed = Command(id, original.Stamp);
        Assert.Equal(AccessCode.Unavailable, await Recover(provider, reviewed, new(false, Environment.UserName)));
        Assert.Equal(AccessCode.Unavailable, await Recover(provider, reviewed, new(true, "different-operating-system-identity")));
        Assert.Equal(AccessCode.ValidationFailed, await Recover(provider, reviewed with { VerificationReference = "" }));
        Assert.Equal(AccessCode.ValidationFailed, await Recover(provider, reviewed with { Reason = "Control\ntext" }));
        Assert.Equal(AccessCode.Conflict, await Recover(provider, reviewed with { ReviewedSecurityStamp = "stale-stamp" }));
        Assert.Equal(AccessCode.Unavailable, await Recover(provider, Command(tenantId, (await Snapshot(tenantId)).Stamp)));
        Assert.Equal(AccessCode.Unavailable, await Recover(provider, Command(unconfirmedId, (await Snapshot(unconfirmedId)).Stamp)));
        Assert.Equal(original, await Snapshot(id));

        var results = await Task.WhenAll(Recover(provider, reviewed), Recover(provider, reviewed));
        Assert.Equal(1, results.Count(x => x == AccessCode.Success)); Assert.Equal(1, results.Count(x => x == AccessCode.Conflict));
        var after = await Snapshot(id); Assert.NotEqual(original.Stamp, after.Stamp); Assert.NotEqual(original.Seed, after.Seed); Assert.Equal(original.Password, after.Password); Assert.Equal(0, after.Codes);
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); var user = await db.Users.SingleAsync(x => x.Id == id);
            Assert.False(user.TwoFactorEnabled); Assert.True(user.MustReplaceRecoveryCodes); Assert.True(user.EmailConfirmed);
            Assert.Single(await db.BuildingManagers.Where(x => x.UserId == id).ToArrayAsync());
            var audit = Assert.Single(await db.AccountAudits.Where(x => x.SubjectId == id && x.Action == "OperatorMfaRecovered").ToArrayAsync());
            Assert.Equal("deployment:" + Environment.UserName, audit.ActorId); Assert.Contains(reviewed.VerificationReference, audit.Reason);
            var notice = Assert.Single(await db.AccountMessages.Where(x => x.UserId == id && x.Kind == "SecurityNotice").ToArrayAsync());
            Assert.Equal(MessageState.Queued, notice.State); Assert.Equal(building.Id, notice.BuildingId); Assert.DoesNotContain(reviewed.VerificationReference, notice.ProtectedPayload);
            notice.State = MessageState.Sending; notice.LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(2); await db.SaveChangesAsync();
            var queue = new NotificationQueue(db, scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>(), TimeProvider.System, new Uri("http://localhost:5081"));
            var email = await queue.ComposeAsync(new(notice.Id, notice.LeaseToken), default); Assert.NotNull(email); Assert.DoesNotContain(original.Seed, email.Text);
            notice.State = MessageState.Suppressed; notice.LeaseUntil = null; await db.SaveChangesAsync(); // Keep this fixture's private protector out of later queue-contract runs.
        }
        Assert.Equal(AccessCode.Conflict, await Recover(provider, reviewed));

        async Task<bool> Authorize(string code, TimeProvider? clock = null)
        {
            using var scope = provider.CreateScope(); var services = scope.ServiceProvider;
            var users = services.GetRequiredService<UserManager<OfficeUser>>(); var user = (await users.FindByIdAsync(id))!;
            return await new OperatorEnrollment(services.GetRequiredService<OfficeDbContext>(), users, clock ?? TimeProvider.System)
                .AuthorizeAsync(id, user.SecurityStamp!, code, default);
        }
        await using (var db = create())
        {
            var credential = await db.UserTokens.SingleAsync(x => x.UserId == id && x.LoginProvider == OperatorEnrollment.Provider && x.Name == OperatorEnrollment.CodeToken);
            Assert.DoesNotContain(reviewed.EnrollmentCode, credential.Value);
        }
        Assert.False(await Authorize(new string('B', 32)));
        Assert.False(await Authorize(reviewed.EnrollmentCode, new TestClock { Offset = TimeSpan.FromMinutes(16) }));
        var admissions = await Task.WhenAll(Authorize(reviewed.EnrollmentCode), Authorize(reviewed.EnrollmentCode));
        Assert.Equal(1, admissions.Count(x => x)); Assert.False(await Authorize(reviewed.EnrollmentCode));
        using (var scope = provider.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>(); var user = (await users.FindByIdAsync(id))!;
            Assert.False(user.TwoFactorEnabled); Assert.Equal("true", await users.GetAuthenticationTokenAsync(user, OperatorEnrollment.Provider, OperatorEnrollment.RequiredToken));
        }

        OfficeDbContext Failing()
        {
            using var originalDb = create(); var connection = originalDb.Database.GetConnectionString()!; var interceptor = new FailRecoveryAudit();
            return originalDb is MySqlOfficeDbContext ? new MySqlOfficeDbContext(new DbContextOptionsBuilder<MySqlOfficeDbContext>().UseMySQL(connection).AddInterceptors(interceptor).Options)
                : new SqliteOfficeDbContext(new DbContextOptionsBuilder<SqliteOfficeDbContext>().UseSqlite(connection).AddInterceptors(interceptor).Options);
        }
        await using (var failing = Services(Failing)) await Assert.ThrowsAsync<DbUpdateException>(() => Recover(failing, Command(rollbackId, rollback.Stamp)));
        Assert.Equal(rollback, await Snapshot(rollbackId));
        await using (var db = create())
        {
            Assert.True((await db.Users.SingleAsync(x => x.Id == rollbackId)).TwoFactorEnabled);
            Assert.False(await db.AccountAudits.AnyAsync(x => x.SubjectId == rollbackId && x.Action == "OperatorMfaRecovered"));
            Assert.False(await db.AccountMessages.AnyAsync(x => x.UserId == rollbackId && x.Kind == "SecurityNotice"));
            Assert.False(await db.UserTokens.AnyAsync(x => x.UserId == rollbackId && x.LoginProvider == OperatorEnrollment.Provider));
        }
    }
    private sealed class FailRecoveryAudit : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is OfficeDbContext db && db.ChangeTracker.Entries<AccountAudit>().Any(x => x.State == EntityState.Added && x.Entity.Action == "OperatorMfaRecovered"))
                throw new DbUpdateException("Controlled recovery-audit failure");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
