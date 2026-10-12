using System.Text.Json;
using AllenBuilding.Application.Access;
using AllenBuilding.Application.Maintenance;
using AllenBuilding.Domain.Maintenance;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Notifications;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AllenBuilding.IntegrationTests;

public static class AccessContract
{
    public static async Task VerifyAsync(Func<OfficeDbContext> create)
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider()); services.AddScoped<OfficeDbContext>(_ => create());
        services.AddIdentityCore<OfficeUser>().AddRoles<IdentityRole>().AddEntityFrameworkStores<OfficeDbContext>().AddDefaultTokenProviders().AddTokenProvider<InvitationTokenProvider>("Invitation");
        services.Replace(ServiceDescriptor.Scoped<IUserStore<OfficeUser>, ProtectedUserStore>());
        services.AddScoped<TestActor>(); services.AddScoped<ICurrentActor>(sp => sp.GetRequiredService<TestActor>());
        services.AddScoped<AccountInvitations>(); services.AddScoped<AccountAccess>(); services.AddScoped<AccessRevocation>();
        services.AddScoped<IMaintenanceStore, MaintenanceStore>(); services.AddScoped<MaintenanceActions>();
        await using var provider = services.BuildServiceProvider();
        var building = new Building { Name = "Access contract building" }; var other = new Building { Name = "Access other building" };
        var suite = new Suite { BuildingId = building.Id, Name = "Grant suite" }; var foreign = new Suite { BuildingId = other.Id, Name = "Foreign suite" };
        string managerId, secondId, tenantId; string passwordHash, stamp;
        var email = $"access-existing-{Guid.NewGuid():N}@example.test";
        using (var scope = provider.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
            async Task<OfficeUser> User(string address)
            {
                var user = new OfficeUser { Email = address, UserName = address, EmailConfirmed = true };
                Assert.True((await users.CreateAsync(user, AccountTestHost.Password)).Succeeded); return user;
            }
            managerId = (await User($"access-manager-{Guid.NewGuid():N}@example.test")).Id;
            secondId = (await User($"access-second-{Guid.NewGuid():N}@example.test")).Id;
            var tenant = await User(email); tenantId = tenant.Id; passwordHash = tenant.PasswordHash!; stamp = tenant.SecurityStamp!;
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            db.AddRange(building, other, suite, foreign, new BuildingManager { BuildingId = building.Id, UserId = managerId }, new BuildingManager { BuildingId = building.Id, UserId = secondId });
            await db.SaveChangesAsync();
        }
        var manager = new Actor(managerId, new HashSet<Guid> { building.Id }, true, true);
        var second = new Actor(secondId, new HashSet<Guid> { building.Id }, true, true);
        var tenantActor = new Actor(tenantId, new HashSet<Guid>(), false, false);
        async Task<T> As<T>(Actor? actor, Func<IServiceProvider, Task<T>> call)
        {
            using var scope = provider.CreateScope(); scope.ServiceProvider.GetRequiredService<TestActor>().Actor = actor;
            return await call(scope.ServiceProvider);
        }
        var grant = new GrantExistingAccount(Guid.NewGuid(), suite.Id, email, "Approved suite access");
        Assert.Equal(AccessCode.Unavailable, (await As(tenantActor, sp => sp.GetRequiredService<AccountAccess>().GrantAsync(grant))).Code);
        Assert.Equal(AccessCode.Unavailable, (await As(manager with { RecentMfa = false }, sp => sp.GetRequiredService<AccountAccess>().GrantAsync(grant))).Code);
        Assert.Equal(AccessCode.Unavailable, (await As(manager, sp => sp.GetRequiredService<AccountAccess>().GrantAsync(grant with { SuiteId = foreign.Id }))).Code);
        var granted = await As(manager, sp => sp.GetRequiredService<AccountAccess>().GrantAsync(grant)); Assert.Equal(AccessCode.Success, granted.Code);
        Assert.Equal(granted, await As(manager, sp => sp.GetRequiredService<AccountAccess>().GrantAsync(grant with { Email = email.ToUpperInvariant() })));
        Assert.Equal(AccessCode.Conflict, (await As(manager, sp => sp.GetRequiredService<AccountAccess>().GrantAsync(grant with { Reason = "Changed payload" }))).Code);
        Assert.Equal(AccessCode.Conflict, (await As(second, sp => sp.GetRequiredService<AccountAccess>().GrantAsync(grant with { OperationId = Guid.NewGuid() }))).Code);
        Guid membershipVersion, orderId;
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            membershipVersion = (await db.Memberships.SingleAsync(x => x.Id == granted.MembershipId)).Version;
            var order = WorkOrder.Submit(suite.Id, granted.MembershipId!.Value, tenantId, "Old tenancy request", "Private description", "Kitchen", DateTimeOffset.UtcNow); orderId = order.Id;
            db.WorkOrders.Add(order); await db.SaveChangesAsync();
        }
        Assert.Equal(MaintenanceCode.Success, await As(manager, sp => sp.GetRequiredService<AccessRevocation>().RevokeAsync(granted.MembershipId!.Value, membershipVersion, "Access ended")));
        Assert.Equal(granted, await As(manager, sp => sp.GetRequiredService<AccountAccess>().GrantAsync(grant)));
        var returning = await As(manager, sp => sp.GetRequiredService<AccountAccess>().GrantAsync(grant with { OperationId = Guid.NewGuid(), Reason = "New tenancy approved" }));
        Assert.Equal(AccessCode.Success, returning.Code); Assert.NotEqual(granted.MembershipId, returning.MembershipId);
        Assert.Null(await As(tenantActor, sp => sp.GetRequiredService<MaintenanceActions>().ReadAsync(orderId, false)));
        using (var scope = provider.CreateScope())
        {
            var user = await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().Users.SingleAsync(x => x.Id == tenantId);
            Assert.Equal(passwordHash, user.PasswordHash); Assert.Equal(stamp, user.SecurityStamp);
        }
        async Task<(Guid Id, Guid Version, string Token, Guid MessageId)> Invitation(string address)
        {
            var id = await As(manager, sp => sp.GetRequiredService<AccountInvitations>().IssueAsync(suite.Id, address)); Assert.NotNull(id);
            return await ReadInvitation(id.Value);
        }
        async Task<(Guid Id, Guid Version, string Token, Guid MessageId)> ReadInvitation(Guid id)
        {
            using var scope = provider.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            var invitation = await db.Invitations.AsNoTracking().SingleAsync(x => x.Id == id);
            var message = (await db.AccountMessages.Where(x => x.UserId == invitation.UserId && x.Kind == "Invitation").ToListAsync()).Single(x =>
            {
                var json = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("AllenOffice.AccountMessage.v1").Unprotect(x.ProtectedPayload);
                return JsonDocument.Parse(json).RootElement.GetProperty("version").GetGuid() == invitation.TokenVersion;
            });
            var payload = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("AllenOffice.AccountMessage.v1").Unprotect(message.ProtectedPayload);
            return (invitation.Id, invitation.Version, JsonDocument.Parse(payload).RootElement.GetProperty("token").GetString()!, message.Id);
        }
        var first = await Invitation($"access-invited-{Guid.NewGuid():N}@example.test");
        Assert.Equal(AccessCode.Success, await As(manager, sp => sp.GetRequiredService<AccountInvitations>().ChangeAsync(first.Id, first.Version, false, "New link approved")));
        var reissued = await ReadInvitation(first.Id); Assert.NotEqual(first.Token, reissued.Token);
        Assert.False(await As(null, sp => sp.GetRequiredService<AccountInvitations>().AcceptAsync(first.Id, first.Token, AccountTestHost.Password)));
        Assert.Equal(AccessCode.Conflict, await As(manager, sp => sp.GetRequiredService<AccountInvitations>().ChangeAsync(first.Id, first.Version, true, "Stale manager form")));
        // A queued superseded token is suppressed even if a dispatcher has already claimed it.
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); var message = await db.AccountMessages.SingleAsync(x => x.Id == first.MessageId);
            message.State = MessageState.Sending; message.LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(2); await db.SaveChangesAsync();
            var queue = new NotificationQueue(db, scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>(), TimeProvider.System, new Uri("http://localhost:5081"));
            Assert.Null(await queue.ComposeAsync(new(message.Id, message.LeaseToken), default));
        }
        Assert.Equal(AccessCode.Success, await As(manager, sp => sp.GetRequiredService<AccountInvitations>().ChangeAsync(reissued.Id, reissued.Version, true, "Invitation withdrawn")));
        Assert.False(await As(null, sp => sp.GetRequiredService<AccountInvitations>().AcceptAsync(reissued.Id, reissued.Token, AccountTestHost.Password)));
        using (var scope = provider.CreateScope())
        {
            var version = (await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().Invitations.SingleAsync(x => x.Id == first.Id)).Version;
            Assert.Equal(AccessCode.Success, await As(manager, sp => sp.GetRequiredService<AccountInvitations>().ChangeAsync(first.Id, version, false, "Explicitly approve a fresh invitation")));
        }
        var approved = await ReadInvitation(first.Id);
        Assert.True(await As(null, sp => sp.GetRequiredService<AccountInvitations>().AcceptAsync(approved.Id, approved.Token, AccountTestHost.Password)));
        Assert.False(await As(null, sp => sp.GetRequiredService<AccountInvitations>().AcceptAsync(approved.Id, approved.Token, AccountTestHost.Password)));
        Assert.Equal(AccessCode.Conflict, await As(manager, sp => sp.GetRequiredService<AccountInvitations>().ChangeAsync(approved.Id, approved.Version, false, "Cannot replace an accepted account")));
        var racing = await Invitation($"access-race-{Guid.NewGuid():N}@example.test");
        var acceptance = Task.Run(() => As(null, sp => sp.GetRequiredService<AccountInvitations>().AcceptAsync(racing.Id, racing.Token, AccountTestHost.Password)));
        var revocation = Task.Run(() => As(second, sp => sp.GetRequiredService<AccountInvitations>().ChangeAsync(racing.Id, racing.Version, true, "Revoke during activation")));
        await Task.WhenAll(acceptance, revocation);
        Assert.True(acceptance.Result ? revocation.Result == AccessCode.Conflict : revocation.Result == AccessCode.Success);
    }
}
