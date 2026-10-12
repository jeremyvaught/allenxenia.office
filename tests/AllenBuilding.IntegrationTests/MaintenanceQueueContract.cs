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

namespace AllenBuilding.IntegrationTests;

public static class MaintenanceQueueContract
{
    public static async Task VerifyAsync(Func<OfficeDbContext> create)
    {
        var now = new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<TimeProvider>(new QueueClock(now));
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider()); services.AddScoped<OfficeDbContext>(_ => create());
        services.AddIdentityCore<OfficeUser>().AddEntityFrameworkStores<OfficeDbContext>().AddDefaultTokenProviders();
        services.AddScoped<TestActor>(); services.AddScoped<ICurrentActor>(sp => sp.GetRequiredService<TestActor>());
        services.AddScoped<AccountInvitations>(); services.AddScoped<IMaintenanceStore, MaintenanceStore>(); services.AddScoped<MaintenanceActions>();
        await using var provider = services.BuildServiceProvider();
        OfficeUser User(string name) => new() { UserName = $"queue-{name}-{Guid.NewGuid():N}", DisplayName = name };
        var manager = User("Queue manager"); var departed = User("Former manager"); var tenant = User("Queue tenant");
        var building = new Building { Name = "Queue building" }; var foreign = new Building { Name = "Foreign queue building" };
        var suite = new Suite { BuildingId = building.Id, Name = "Queue suite" }; var foreignSuite = new Suite { BuildingId = foreign.Id, Name = "Foreign suite" };
        var membership = Membership.Grant(suite.Id, tenant.Id, now.AddYears(-1)); var foreignMembership = Membership.Grant(foreignSuite.Id, tenant.Id, now.AddYears(-1));
        var contact = Contractor.Create(Guid.NewGuid(), building.Id, "Historic contractor", Trade.General, "", "");
        var foreignContact = Contractor.Create(Guid.NewGuid(), foreign.Id, "Foreign private contractor", Trade.General, "", "");
        WorkOrder Order(string title, int age) => WorkOrder.Submit(suite.Id, membership.Id, tenant.Id, title, "Private details", "Office", now.AddDays(-age));
        var closed = Order("Closed old work", 120); closed.Triage(Priority.High, Trade.General, now.AddDays(-119)); closed.Assign(null, contact.Id, now.AddDays(-118)); closed.Start(now.AddDays(-117)); closed.Complete("Resolved", now.AddDays(-116)); closed.Close(now.AddDays(-115));
        var cancelled = Order("Cancelled work", 90); cancelled.Cancel("No longer needed", now.AddDays(-89));
        var completed = Order("Needs verification", 40); completed.Triage(Priority.Urgent, Trade.General, now.AddDays(-39)); completed.Assign(manager.Id, null, now.AddDays(-38)); completed.Start(now.AddDays(-37)); completed.Complete("Ready for review", now.AddDays(-30));
        var mine = Order("Assigned high priority", 20); mine.Triage(Priority.High, Trade.General, now.AddDays(-19)); mine.Assign(manager.Id, null, now.AddDays(-18));
        var contracted = Order("Historic assignment", 10); contracted.Triage(Priority.Normal, Trade.General, now.AddDays(-9)); contracted.Assign(null, contact.Id, now.AddDays(-8));
        var unassigned = Order("Awaiting assignment", 60); unassigned.Triage(Priority.Low, Trade.General, now.AddDays(-59));
        var recent = Order("Recent report", 1);
        var reopened = Order("Reopened old report", 50); reopened.Cancel("Initially withdrawn", now.AddDays(-49)); reopened.Reopen("Needs work after all", now.AddHours(-6));
        var previousManager = Order("Previous manager assignment", 3); previousManager.Triage(Priority.Normal, Trade.General, now.AddDays(-2)); previousManager.Assign(departed.Id, null, now.AddDays(-1));
        var filler = Enumerable.Range(0, 60).Select(index => Order($"Equal-time report {index}", 5)).ToList();
        var foreignOrder = WorkOrder.Submit(foreignSuite.Id, foreignMembership.Id, tenant.Id, "Foreign private request", "Private", "Office", now.AddDays(-100));
        foreignOrder.Triage(Priority.Urgent, Trade.General, now.AddDays(-99)); foreignOrder.Assign(null, foreignContact.Id, now.AddDays(-98));
        contact.Update(contact.Name, contact.Trade, "", "", false);
        var orders = new[] { closed, cancelled, completed, mine, contracted, unassigned, recent, reopened, previousManager }.Concat(filler).ToList();
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            db.AddRange(manager, departed, tenant, building, foreign, suite, foreignSuite, membership, foreignMembership, contact, foreignContact,
                new BuildingManager { UserId = manager.Id, BuildingId = building.Id }, new ActiveGrant { UserId = tenant.Id, SuiteId = suite.Id, MembershipId = membership.Id },
                new ActiveGrant { UserId = tenant.Id, SuiteId = foreignSuite.Id, MembershipId = foreignMembership.Id }, foreignOrder);
            db.WorkOrders.AddRange(orders); await db.SaveChangesAsync();
        }
        var actor = new Actor(manager.Id, new HashSet<Guid> { building.Id }, true, true);
        async Task<MaintenanceQueuePage?> Read(MaintenanceQueueQuery query, Actor? asActor = null)
        {
            using var scope = provider.CreateScope(); scope.ServiceProvider.GetRequiredService<TestActor>().Actor = asActor ?? actor;
            return await scope.ServiceProvider.GetRequiredService<MaintenanceActions>().QueueAsync(query);
        }
        var first = (await Read(new()))!; var second = (await Read(new(Page: 2)))!;
        Assert.Equal(50, first.Items.Count); Assert.True(first.HasNext); Assert.False(second.HasNext);
        var expected = orders.Where(x => x.Status is not (WorkOrderStatus.Closed or WorkOrderStatus.Cancelled)).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Select(x => x.Id);
        Assert.Equal(expected, first.Items.Concat(second.Items).Select(x => x.Id));
        Assert.Contains(first.Items, x => x.Id == completed.Id); Assert.DoesNotContain(first.Items, x => x.Id == closed.Id || x.Id == cancelled.Id || x.Id == foreignOrder.Id);
        Assert.Equal(new[] { mine.Id }, (await Read(new(Priority: Priority.High, MinAgeDays: 15, Assignee: "mine")))!.Items.Select(x => x.Id));
        Assert.Equal(new[] { unassigned.Id, reopened.Id }, (await Read(new(MinAgeDays: 45, Assignee: "unassigned")))!.Items.Select(x => x.Id));
        Assert.Equal(new[] { closed.Id }, (await Read(new(Status: WorkOrderStatus.Closed)))!.Items.Select(x => x.Id));
        var all = (await Read(new(IncludeFinished: true)))!; Assert.Contains(all.Items, x => x.Id == closed.Id); Assert.Contains(all.Items, x => x.Id == cancelled.Id);
        Assert.Equal(new[] { contracted.Id }, (await Read(new(Assignee: "contractor:" + contact.Id)))!.Items.Select(x => x.Id));
        Assert.Equal(new[] { previousManager.Id }, (await Read(new(Assignee: "manager:" + departed.Id)))!.Items.Select(x => x.Id));
        Assert.Equal(reopened.Id, (await Read(new(Sort: QueueSort.RecentlyUpdated)))!.Items[0].Id);
        Assert.Equal(recent.Id, (await Read(new(Sort: QueueSort.Newest)))!.Items[0].Id);
        Assert.Contains(first.Assignees, x => x.Value == "contractor:" + contact.Id && x.Label.Contains("inactive", StringComparison.Ordinal));
        Assert.DoesNotContain(first.Assignees, x => x.Value == "contractor:" + foreignContact.Id || x.Label.Contains("Foreign private", StringComparison.Ordinal));
        Assert.Null(await Read(new(), actor with { ChallengedMfa = false }));
        Assert.Null(await Read(new(), new Actor(tenant.Id, new HashSet<Guid>(), false, false)));
        foreach (var invalid in new[] { new MaintenanceQueueQuery(Status: (WorkOrderStatus)999), new(Priority: (Priority)999), new(MinAgeDays: -1), new(Assignee: "all"), new(Assignee: "contractor:" + Guid.Empty), new(Sort: (QueueSort)999), new(Page: 0), new(Page: 1001) }) Assert.Null(await Read(invalid));
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); db.BuildingManagers.Remove(await db.BuildingManagers.SingleAsync(x => x.UserId == manager.Id && x.BuildingId == building.Id)); await db.SaveChangesAsync();
        }
        var ended = (await Read(new()))!; Assert.Empty(ended.Items); Assert.Empty(ended.Assignees);
    }
    private sealed class QueueClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
}
