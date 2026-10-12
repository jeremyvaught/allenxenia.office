using AllenBuilding.Application.Access;
using AllenBuilding.Application.Maintenance;
using AllenBuilding.Domain.Maintenance;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AllenBuilding.IntegrationTests;

public static class MaintenanceContract
{
    public static async Task VerifyAsync(Func<OfficeDbContext> create)
    {
        var services = new ServiceCollection(); services.AddLogging();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<OfficeDbContext>(_ => create());
        services.AddIdentityCore<OfficeUser>().AddRoles<IdentityRole>().AddEntityFrameworkStores<OfficeDbContext>().AddDefaultTokenProviders();
        services.Replace(ServiceDescriptor.Scoped<IUserStore<OfficeUser>, ProtectedUserStore>());
        services.AddScoped<TestActor>(); services.AddScoped<ICurrentActor>(sp => sp.GetRequiredService<TestActor>());
        services.AddScoped<AccountInvitations>(); services.AddScoped<AccessRevocation>();
        services.AddScoped<IMaintenanceStore, MaintenanceStore>(); services.AddScoped<MaintenanceActions>();
        services.AddScoped<IContractorStore, ContractorStore>(); services.AddScoped<ContractorActions>();
        await using var provider = services.BuildServiceProvider();
        string tenantId, peerId, managerId, secondManagerId; Guid suiteId, membershipId, buildingId;
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<OfficeUser>>();
            async Task<string> User()
            {
                var user = new OfficeUser { UserName = $"workflow-{Guid.NewGuid():N}@example.test", EmailConfirmed = true };
                Assert.True((await users.CreateAsync(user, AccountTestHost.Password)).Succeeded); return user.Id;
            }
            tenantId = await User(); peerId = await User(); managerId = await User(); secondManagerId = await User();
            var building = new Building { Name = "Workflow test" }; buildingId = building.Id;
            var suite = new Suite { BuildingId = building.Id, Name = "Private suite" }; suiteId = suite.Id;
            var membership = Membership.Grant(suite.Id, tenantId, DateTimeOffset.UtcNow.AddDays(-1)); membershipId = membership.Id;
            var peerMembership = Membership.Grant(suite.Id, peerId, DateTimeOffset.UtcNow.AddDays(-1));
            db.AddRange(building, suite, membership, peerMembership,
                new ActiveGrant { UserId = tenantId, SuiteId = suite.Id, MembershipId = membership.Id },
                new ActiveGrant { UserId = peerId, SuiteId = suite.Id, MembershipId = peerMembership.Id },
                new BuildingManager { UserId = managerId, BuildingId = building.Id },
                new BuildingManager { UserId = secondManagerId, BuildingId = building.Id });
            await db.SaveChangesAsync();
        }
        var tenant = new Actor(tenantId, new HashSet<Guid>(), false, false);
        var peer = new Actor(peerId, new HashSet<Guid>(), false, false);
        var manager = new Actor(managerId, new HashSet<Guid> { buildingId }, true, true);
        var secondManager = new Actor(secondManagerId, new HashSet<Guid> { buildingId }, true, true);
        async Task<T> As<T>(Actor actor, Func<IServiceProvider, Task<T>> call)
        {
            using var scope = provider.CreateScope(); scope.ServiceProvider.GetRequiredService<TestActor>().Actor = actor;
            return await call(scope.ServiceProvider);
        }
        var submit = new SubmitRequest(Guid.NewGuid(), membershipId, "Private request", "A leaking sink", "Kitchen");
        var first = await As(tenant, sp => sp.GetRequiredService<MaintenanceActions>().SubmitAsync(submit));
        Assert.Equal(MaintenanceCode.Success, first.Code);
        var concurrent = submit with { OperationId = Guid.NewGuid(), Title = "Concurrent retry" };
        var concurrentResults = await Task.WhenAll(
            Task.Run(() => As(tenant, sp => sp.GetRequiredService<MaintenanceActions>().SubmitAsync(concurrent))),
            Task.Run(() => As(tenant, sp => sp.GetRequiredService<MaintenanceActions>().SubmitAsync(concurrent))));
        Assert.Equal(concurrentResults[0], concurrentResults[1]);
        var concurrentTriage = new ChangeRequest(Guid.NewGuid(), concurrentResults[0].WorkOrderId!.Value, concurrentResults[0].Version!.Value,
            MaintenanceAction.Triage, Priority.Normal, Trade.General);
        var competingManagers = await Task.WhenAll(
            Task.Run(() => As(manager, sp => sp.GetRequiredService<MaintenanceActions>().ChangeAsync(concurrentTriage, true))),
            Task.Run(() => As(secondManager, sp => sp.GetRequiredService<MaintenanceActions>().ChangeAsync(concurrentTriage with { OperationId = Guid.NewGuid() }, true))));
        Assert.Single(competingManagers, x => x.Code == MaintenanceCode.Success);
        Assert.Single(competingManagers, x => x.Code == MaintenanceCode.Conflict);
        var replay = await As(tenant, sp => sp.GetRequiredService<MaintenanceActions>().SubmitAsync(submit)); Assert.Equal(first, replay);
        var mismatch = await As(tenant, sp => sp.GetRequiredService<MaintenanceActions>().SubmitAsync(submit with { Title = "Changed" })); Assert.Equal(MaintenanceCode.Conflict, mismatch.Code);
        var id = first.WorkOrderId!.Value;
        Assert.Null(await As(peer, sp => sp.GetRequiredService<MaintenanceActions>().ReadAsync(id, false)));
        Assert.Null(await As(manager, sp => sp.GetRequiredService<MaintenanceActions>().ReadAsync(id, false)));
        Assert.NotNull(await As(manager, sp => sp.GetRequiredService<MaintenanceActions>().ReadAsync(id, true)));
        var triage = new ChangeRequest(Guid.NewGuid(), id, first.Version!.Value, MaintenanceAction.Triage, Priority.High, Trade.Plumbing);
        Assert.Equal(MaintenanceCode.Unavailable, (await As(tenant, sp => sp.GetRequiredService<MaintenanceActions>().ChangeAsync(triage, false))).Code);
        var triaged = await As(manager, sp => sp.GetRequiredService<MaintenanceActions>().ChangeAsync(triage, true)); Assert.Equal(MaintenanceCode.Success, triaged.Code);
        Assert.Equal(triaged, await As(manager, sp => sp.GetRequiredService<MaintenanceActions>().ChangeAsync(triage, true)));
        Assert.Equal(MaintenanceCode.Conflict, (await As(manager, sp => sp.GetRequiredService<MaintenanceActions>().ChangeAsync(triage with { OperationId = Guid.NewGuid() }, true))).Code);
        var contactCommand = new SaveContractor(Guid.NewGuid(), buildingId, null, "Reliable plumbing", Trade.Plumbing, "contractor@example.test", "555-0100");
        Assert.Equal(MaintenanceCode.Unavailable, await As(tenant, sp => sp.GetRequiredService<ContractorActions>().SaveAsync(contactCommand)));
        Assert.Equal(MaintenanceCode.Success, await As(manager, sp => sp.GetRequiredService<ContractorActions>().SaveAsync(contactCommand)));
        var contact = (await As(manager, sp => sp.GetRequiredService<ContractorActions>().ListAsync())).Single(x => x.Id == contactCommand.Id);
        var assignContact = new ChangeRequest(Guid.NewGuid(), id, triaged.Version!.Value, MaintenanceAction.Assign, ContractorId: contact.Id);
        var assignedContact = await As(manager, sp => sp.GetRequiredService<MaintenanceActions>().ChangeAsync(assignContact, true));
        Assert.Equal(MaintenanceCode.Success, assignedContact.Code);
        var deactivate = contactCommand with { Version = contact.Version, Active = false };
        Assert.Equal(MaintenanceCode.Success, await As(manager, sp => sp.GetRequiredService<ContractorActions>().SaveAsync(deactivate)));
        Assert.Equal(MaintenanceCode.Conflict, await As(manager, sp => sp.GetRequiredService<ContractorActions>().SaveAsync(deactivate with { Name = "Stale edit" })));
        Assert.Equal(assignedContact, await As(manager, sp => sp.GetRequiredService<MaintenanceActions>().ChangeAsync(assignContact, true)));
        Assert.Equal(MaintenanceCode.Unavailable, (await As(manager, sp => sp.GetRequiredService<MaintenanceActions>().ChangeAsync(assignContact with { OperationId = Guid.NewGuid(), Version = assignedContact.Version!.Value }, true))).Code);
        var afterDeactivation = (await As(manager, sp => sp.GetRequiredService<MaintenanceActions>().ReadAsync(id, true)))!;
        Assert.Equal(contact.Name, afterDeactivation.Assignment);
        Assert.DoesNotContain(afterDeactivation.Assignees!, x => x.Value == "contractor:" + contact.Id.ToString("D"));
        Assert.Contains(afterDeactivation.History, x => x.Note == "Assigned to Reliable plumbing (contractor).");
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            var foreignBuilding = new Building { Name = "Other building" };
            var foreignContact = Contractor.Create(Guid.NewGuid(), foreignBuilding.Id, "Other contact", Trade.General, "", "");
            db.AddRange(foreignBuilding, foreignContact); await db.SaveChangesAsync();
            Assert.Equal(MaintenanceCode.Unavailable, (await As(manager, sp => sp.GetRequiredService<MaintenanceActions>().ChangeAsync(assignContact with { OperationId = Guid.NewGuid(), Version = afterDeactivation.Version, ContractorId = foreignContact.Id }, true))).Code);
            Assert.Equal(MaintenanceCode.Unavailable, await As(manager, sp => sp.GetRequiredService<ContractorActions>().SaveAsync(contactCommand with { Id = foreignContact.Id, Version = foreignContact.Version })));
            db.Contractors.Remove(await db.Contractors.SingleAsync(x => x.Id == contact.Id));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        Assert.Equal(MaintenanceCode.ValidationFailed, (await As(manager, sp => sp.GetRequiredService<MaintenanceActions>().ChangeAsync(assignContact with { ManagerId = secondManagerId }, true))).Code);
        Assert.Equal(MaintenanceCode.Unavailable, (await As(manager, sp => sp.GetRequiredService<MaintenanceActions>().ChangeAsync(assignContact with { OperationId = Guid.NewGuid(), Version = afterDeactivation.Version, ManagerId = peerId, ContractorId = null }, true))).Code);
        var crossAssignments = await Task.WhenAll(
            Task.Run(() => As(manager, sp => sp.GetRequiredService<MaintenanceActions>().ChangeAsync(new(Guid.NewGuid(), id, afterDeactivation.Version, MaintenanceAction.Assign, ManagerId: secondManagerId), true))),
            Task.Run(async () =>
            {
                var other = (await As(secondManager, sp => sp.GetRequiredService<MaintenanceActions>().ReadAsync(concurrentResults[0].WorkOrderId!.Value, true)))!;
                return await As(secondManager, sp => sp.GetRequiredService<MaintenanceActions>().ChangeAsync(new(Guid.NewGuid(), other.Id, other.Version, MaintenanceAction.Assign, ManagerId: managerId), true));
            }));
        Assert.All(crossAssignments, result => Assert.Equal(MaintenanceCode.Success, result.Code));
        async Task<MaintenanceResult> Change(MaintenanceAction action, string? note = null, bool internalNote = false)
        {
            var details = (await As(manager, sp => sp.GetRequiredService<MaintenanceActions>().ReadAsync(id, true)))!;
            return await As(manager, sp => sp.GetRequiredService<MaintenanceActions>().ChangeAsync(new(Guid.NewGuid(), id, details.Version, action, Note: note, Internal: internalNote), true));
        }
        Assert.Equal(WorkOrderStatus.Assigned, (await Change(MaintenanceAction.AssignSelf)).Status);
        Assert.Equal(WorkOrderStatus.InProgress, (await Change(MaintenanceAction.Start)).Status);
        Assert.Equal(MaintenanceCode.Success, (await Change(MaintenanceAction.Comment, "Private management note", true)).Code);
        var privateView = (await As(tenant, sp => sp.GetRequiredService<MaintenanceActions>().ReadAsync(id, false)))!;
        Assert.DoesNotContain(privateView.History, x => x.Internal || x.Note == "Private management note");
        var internalAttempt = new ChangeRequest(Guid.NewGuid(), id, privateView.Version, MaintenanceAction.Comment, Note: "Disallowed", Internal: true);
        Assert.Equal(MaintenanceCode.Unavailable, (await As(tenant, sp => sp.GetRequiredService<MaintenanceActions>().ChangeAsync(internalAttempt, false))).Code);
        int receiptsBefore;
        using (var scope = provider.CreateScope()) receiptsBefore = await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().MaintenanceReceipts.CountAsync(x => x.WorkOrderId == id);
        Assert.Equal(MaintenanceCode.ValidationFailed, (await Change(MaintenanceAction.Complete)).Code);
        using (var scope = provider.CreateScope()) Assert.Equal(receiptsBefore, await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().MaintenanceReceipts.CountAsync(x => x.WorkOrderId == id));
        Assert.Equal(WorkOrderStatus.Completed, (await Change(MaintenanceAction.Complete, "Sink repaired")).Status);
        Assert.Equal(WorkOrderStatus.Closed, (await Change(MaintenanceAction.Close)).Status);
        Assert.Equal(WorkOrderStatus.Submitted, (await Change(MaintenanceAction.Reopen, "Leak returned")).Status);
        var onBehalf = new SubmitRequest(Guid.NewGuid(), membershipId, "Reported by phone", "Manager recorded the tenant report", "Entrance");
        Assert.Equal(MaintenanceCode.Unavailable, (await As(peer, sp => sp.GetRequiredService<MaintenanceActions>().SubmitOnBehalfAsync(onBehalf))).Code);
        Assert.Equal(MaintenanceCode.Unavailable, (await As(manager with { ChallengedMfa = false }, sp => sp.GetRequiredService<MaintenanceActions>().SubmitOnBehalfAsync(onBehalf))).Code);
        Assert.DoesNotContain(await As(peer, sp => sp.GetRequiredService<MaintenanceActions>().MembersAsync(true)), x => x.Id == membershipId);
        Assert.Contains(await As(manager, sp => sp.GetRequiredService<MaintenanceActions>().MembersAsync(true)), x => x.Id == membershipId);
        Assert.All(await As(tenant, sp => sp.GetRequiredService<MaintenanceActions>().MembersAsync(false)), x => Assert.Null(x.RequesterEmail));
        var behalfResults = await Task.WhenAll(
            Task.Run(() => As(manager, sp => sp.GetRequiredService<MaintenanceActions>().SubmitOnBehalfAsync(onBehalf))),
            Task.Run(() => As(manager, sp => sp.GetRequiredService<MaintenanceActions>().SubmitOnBehalfAsync(onBehalf))));
        Assert.Equal(behalfResults[0], behalfResults[1]); Assert.Equal(MaintenanceCode.Success, behalfResults[0].Code);
        Assert.Equal(MaintenanceCode.Conflict, (await As(manager, sp => sp.GetRequiredService<MaintenanceActions>().SubmitOnBehalfAsync(onBehalf with { Title = "Changed recorded request" }))).Code);
        Assert.NotNull(await As(tenant, sp => sp.GetRequiredService<MaintenanceActions>().ReadAsync(behalfResults[0].WorkOrderId!.Value, false)));
        Assert.Null(await As(peer, sp => sp.GetRequiredService<MaintenanceActions>().ReadAsync(behalfResults[0].WorkOrderId!.Value, false)));
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            var recorded = await db.WorkOrders.SingleAsync(x => x.Id == behalfResults[0].WorkOrderId);
            Assert.Equal(tenantId, recorded.RequesterUserId); Assert.Equal(membershipId, recorded.RequesterMembershipId);
            var entry = await db.WorkOrderHistory.SingleAsync(x => x.WorkOrderId == recorded.Id);
            Assert.Equal(managerId, entry.ActorId); Assert.Equal("SubmittedOnBehalf", entry.Action);
            var foreignBuilding = new Building { Name = "Behalf foreign building" }; var foreignSuite = new Suite { BuildingId = foreignBuilding.Id, Name = "Foreign suite" };
            var foreignMembership = Membership.Grant(foreignSuite.Id, tenantId, DateTimeOffset.UtcNow.AddDays(-1));
            db.AddRange(foreignBuilding, foreignSuite, foreignMembership, new ActiveGrant { UserId = tenantId, SuiteId = foreignSuite.Id, MembershipId = foreignMembership.Id }); await db.SaveChangesAsync();
            Assert.Equal(MaintenanceCode.Unavailable, (await As(manager, sp => sp.GetRequiredService<MaintenanceActions>().SubmitOnBehalfAsync(onBehalf with { OperationId = Guid.NewGuid(), MembershipId = foreignMembership.Id }))).Code);
            Assert.DoesNotContain(await As(manager, sp => sp.GetRequiredService<MaintenanceActions>().MembersAsync(true)), x => x.Id == foreignMembership.Id);
        }
        Guid version;
        using (var scope = provider.CreateScope()) version = (await scope.ServiceProvider.GetRequiredService<OfficeDbContext>().Memberships.SingleAsync(x => x.Id == membershipId)).Version;
        Assert.Equal(MaintenanceCode.Success, await As(manager, sp => sp.GetRequiredService<AccessRevocation>().RevokeAsync(membershipId, version, "Access ended")));
        Assert.Equal(behalfResults[0], await As(manager, sp => sp.GetRequiredService<MaintenanceActions>().SubmitOnBehalfAsync(onBehalf)));
        Assert.Equal(MaintenanceCode.Unavailable, (await As(manager, sp => sp.GetRequiredService<MaintenanceActions>().SubmitOnBehalfAsync(onBehalf with { OperationId = Guid.NewGuid() }))).Code);
        Assert.Null(await As(tenant, sp => sp.GetRequiredService<MaintenanceActions>().ReadAsync(behalfResults[0].WorkOrderId!.Value, false)));
        Assert.Null(await As(tenant, sp => sp.GetRequiredService<MaintenanceActions>().ReadAsync(id, false)));
        Assert.Equal(MaintenanceCode.Unavailable, (await As(tenant, sp => sp.GetRequiredService<MaintenanceActions>().SubmitAsync(submit))).Code);
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            var returned = Membership.Grant(suiteId, tenantId, DateTimeOffset.UtcNow.AddMinutes(-1));
            db.Memberships.Add(returned); db.ActiveGrants.Add(new ActiveGrant { UserId = tenantId, SuiteId = suiteId, MembershipId = returned.Id });
            await db.SaveChangesAsync();
        }
        Assert.Null(await As(tenant, sp => sp.GetRequiredService<MaintenanceActions>().ReadAsync(id, false)));
        Assert.NotNull(await As(manager, sp => sp.GetRequiredService<MaintenanceActions>().ReadAsync(id, true)));
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            Assert.Equal(membershipId, (await db.WorkOrders.SingleAsync(x => x.Id == id)).RequesterMembershipId);
            Assert.Equal(await db.MaintenanceReceipts.CountAsync(x => x.WorkOrderId == id), await db.WorkOrderHistory.CountAsync(x => x.WorkOrderId == id));
            Assert.True(await db.AccountMessages.AnyAsync(x => x.Kind == "MaintenanceUpdate" && x.UserId == tenantId));
        }
    }
}

public sealed class TestActor : ICurrentActor
{
    public Actor? Actor { get; set; }
    public Task<Actor?> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(Actor);
}
