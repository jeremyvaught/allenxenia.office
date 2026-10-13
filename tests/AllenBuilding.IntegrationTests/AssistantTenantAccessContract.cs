using AllenBuilding.Application.Access;
using AllenBuilding.Application.Assistant;
using AllenBuilding.Application.Billing;
using AllenBuilding.Application.Knowledge;
using AllenBuilding.Application.Maintenance;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Domain.Knowledge;
using AllenBuilding.Domain.Maintenance;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AllenBuilding.IntegrationTests;

public static class AssistantTenantAccessContract
{
    public static async Task VerifyAsync(Func<OfficeDbContext> create)
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton(TimeProvider.System); services.AddScoped<OfficeDbContext>(_ => create());
        services.AddIdentityCore<OfficeUser>().AddEntityFrameworkStores<OfficeDbContext>().AddDefaultTokenProviders();
        services.AddScoped<TestActor>(); services.AddScoped<ICurrentActor>(sp => sp.GetRequiredService<TestActor>());
        services.AddScoped<AccountInvitations>(); services.AddScoped<IKnowledgeStore, KnowledgeStore>(); services.AddScoped<KnowledgeActions>();
        services.AddScoped<IAssistantTenantAccess, AssistantTenantAccess>();
        services.AddScoped<IMaintenanceStore, MaintenanceStore>(); services.AddScoped<MaintenanceActions>();
        services.AddScoped<IBillingStore, BillingStore>(); services.AddScoped<BillingActions>();
        await using var provider = services.BuildServiceProvider();
        var user = new OfficeUser { UserName = "assistant-contract-" + Guid.NewGuid(), EmailConfirmed = true, SecurityStamp = Guid.NewGuid().ToString() };
        var manager = new OfficeUser { UserName = "assistant-manager-" + Guid.NewGuid(), EmailConfirmed = true, SecurityStamp = Guid.NewGuid().ToString() };
        var firstBuilding = new Building { Name = "Assistant first building" }; var secondBuilding = new Building { Name = "Assistant second building" };
        var firstSuite = new Suite { BuildingId = firstBuilding.Id, Name = "Assistant A" }; var secondSuite = new Suite { BuildingId = secondBuilding.Id, Name = "Assistant B" };
        var first = Membership.Grant(firstSuite.Id, user.Id, DateTimeOffset.UtcNow.AddDays(-1)); var second = Membership.Grant(secondSuite.Id, user.Id, DateTimeOffset.UtcNow.AddDays(-1));
        var articles = new List<HelpArticle>(); var revisions = new List<HelpRevision>();
        var firstRequest = WorkOrder.Submit(firstSuite.Id, first.Id, user.Id, "Original selected request", "Synthetic leak", "Kitchen", DateTimeOffset.UtcNow.AddDays(-2));
        var secondRequest = WorkOrder.Submit(secondSuite.Id, second.Id, user.Id, "Other original request", "Synthetic leak", "Hall", DateTimeOffset.UtcNow);
        var firstAccount = RentAccount.Open(Guid.NewGuid(), first.Id); var secondAccount = RentAccount.Open(Guid.NewGuid(), second.Id);
        var firstCharge = Obligation.Issue(firstAccount.Id, new Money(100000), "First contract charge", new(2026, 11, 1), manager.Id, DateTimeOffset.UtcNow);
        var secondCharge = Obligation.Issue(secondAccount.Id, new Money(50000), "Second contract charge", new(2026, 11, 1), manager.Id, DateTimeOffset.UtcNow);
        foreach (var building in new[] { firstBuilding, secondBuilding })
        {
            var id = Guid.NewGuid(); var revision = HelpRevision.Create(id, "Tenant entry", "Authorized private source", ArticleAudience.ActiveTenant, manager.Id, new(2027, 1, 1), DateTimeOffset.UtcNow);
            var article = HelpArticle.Create(id, building.Id, revision.Id); article.Publish(revision.Id); articles.Add(article); revisions.Add(revision);
        }
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); db.AddRange(user, manager, firstBuilding, secondBuilding, firstSuite, secondSuite, first, second,
                new ActiveGrant { UserId = user.Id, SuiteId = firstSuite.Id, MembershipId = first.Id }, new ActiveGrant { UserId = user.Id, SuiteId = secondSuite.Id, MembershipId = second.Id },
                new BuildingManager { BuildingId = firstBuilding.Id, UserId = manager.Id }, firstRequest, secondRequest, firstAccount, secondAccount, firstCharge, secondCharge); db.AddRange(articles); db.AddRange(revisions); await db.SaveChangesAsync();
        }
        var tenant = new Actor(user.Id, new HashSet<Guid>(), false, false);
        async Task<T> As<T>(Actor actor, Func<IServiceProvider, Task<T>> action)
        {
            using var scope = provider.CreateScope(); scope.ServiceProvider.GetRequiredService<TestActor>().Actor = actor; return await action(scope.ServiceProvider);
        }
        var original = (await As(tenant, sp => sp.GetRequiredService<IAssistantTenantAccess>().CaptureAsync(first.Id, default)))!;
        Assert.NotNull(original);
        Assert.Null(await As(new Actor(manager.Id, new HashSet<Guid> { firstBuilding.Id }, true, true), sp => sp.GetRequiredService<IAssistantTenantAccess>().CaptureAsync(first.Id, default)));
        var found = await As(tenant, sp => sp.GetRequiredService<KnowledgeActions>().FindForMembershipAsync(first.Id));
        Assert.Contains(found, x => x.ArticleId == articles[0].Id); Assert.DoesNotContain(found, x => x.ArticleId == articles[1].Id);
        Assert.Equal(firstRequest.Id, Assert.Single(await As(tenant, sp => sp.GetRequiredService<MaintenanceActions>().ListForMembershipAsync(first.Id))).Id);
        var selectedBilling = Assert.Single(await As(tenant, sp => sp.GetRequiredService<BillingActions>().ReadForMembershipAsync(first.Id)));
        Assert.Equal(firstAccount.Id, selectedBilling.Id); Assert.Equal(firstCharge.Id, Assert.Single(selectedBilling.Obligations).Id);
        var management = new Actor(manager.Id, new HashSet<Guid> { firstBuilding.Id }, true, true);
        Assert.Empty(await As(management, sp => sp.GetRequiredService<MaintenanceActions>().ListForMembershipAsync(first.Id)));
        Assert.Empty(await As(management, sp => sp.GetRequiredService<BillingActions>().ReadForMembershipAsync(first.Id)));
        var ownSource = new AssistantSource(articles[0].Id, revisions[0].Id, revisions[0].Title, revisions[0].Body);
        var wrongSource = new AssistantSource(articles[1].Id, revisions[1].Id, revisions[1].Title, revisions[1].Body);
        var emitted = 0;
        Task Emit(CancellationToken _) { emitted++; return Task.CompletedTask; }
        Assert.True(await As(tenant, sp => sp.GetRequiredService<IAssistantTenantAccess>().ReleaseAsync(original, [ownSource], Emit, default))); Assert.Equal(1, emitted);
        Assert.False(await As(tenant, sp => sp.GetRequiredService<IAssistantTenantAccess>().ReleaseAsync(original, [wrongSource], Emit, default))); Assert.Equal(1, emitted);
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); (await db.HelpArticles.SingleAsync(x => x.Id == articles[0].Id)).Withdraw(); await db.SaveChangesAsync();
        }
        Assert.False(await As(tenant, sp => sp.GetRequiredService<IAssistantTenantAccess>().ReleaseAsync(original, [ownSource], Emit, default))); Assert.Equal(1, emitted);
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); (await db.Memberships.SingleAsync(x => x.Id == first.Id)).Revoke(manager.Id, "Original access ended", DateTimeOffset.UtcNow);
            db.ActiveGrants.Remove(await db.ActiveGrants.SingleAsync(x => x.MembershipId == first.Id)); await db.SaveChangesAsync();
        }
        Assert.False(await As(tenant, sp => sp.GetRequiredService<IAssistantTenantAccess>().ReleaseAsync(original, [], Emit, default))); Assert.Equal(1, emitted);
        Assert.Empty(await As(tenant, sp => sp.GetRequiredService<MaintenanceActions>().ListForMembershipAsync(first.Id)));
        Assert.Empty(await As(tenant, sp => sp.GetRequiredService<BillingActions>().ReadForMembershipAsync(first.Id)));
        Assert.Equal(secondRequest.Id, Assert.Single(await As(tenant, sp => sp.GetRequiredService<MaintenanceActions>().ListForMembershipAsync(second.Id))).Id);
        Assert.Equal(secondAccount.Id, Assert.Single(await As(tenant, sp => sp.GetRequiredService<BillingActions>().ReadForMembershipAsync(second.Id))).Id);
        var remaining = (await As(tenant, sp => sp.GetRequiredService<IAssistantTenantAccess>().CaptureAsync(second.Id, default)))!; Assert.NotNull(remaining);
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); (await db.Users.SingleAsync(x => x.Id == user.Id)).SecurityStamp = Guid.NewGuid().ToString(); await db.SaveChangesAsync();
        }
        Assert.False(await As(tenant, sp => sp.GetRequiredService<IAssistantTenantAccess>().ReleaseAsync(remaining, [], Emit, default))); Assert.Equal(1, emitted);
    }
}
