using AllenBuilding.Application.Access;
using AllenBuilding.Application.Knowledge;
using AllenBuilding.Domain.Knowledge;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AllenBuilding.IntegrationTests;

public static class KnowledgeContract
{
    public static async Task VerifyAsync(Func<OfficeDbContext> create)
    {
        var services = new ServiceCollection(); services.AddLogging();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton(TimeProvider.System); services.AddScoped<OfficeDbContext>(_ => create());
        services.AddIdentityCore<OfficeUser>().AddEntityFrameworkStores<OfficeDbContext>().AddDefaultTokenProviders();
        services.AddScoped<TestActor>(); services.AddScoped<ICurrentActor>(sp => sp.GetRequiredService<TestActor>());
        services.AddScoped<AccountInvitations>(); services.AddScoped<IKnowledgeStore, KnowledgeStore>(); services.AddScoped<KnowledgeActions>();
        await using var provider = services.BuildServiceProvider();
        var managerUser = new OfficeUser { UserName = $"help-manager-{Guid.NewGuid():N}" };
        var tenantUser = new OfficeUser { UserName = $"help-tenant-{Guid.NewGuid():N}" };
        var outsiderUser = new OfficeUser { UserName = $"help-outsider-{Guid.NewGuid():N}" };
        var building = new Building { Name = "Help building" }; var other = new Building { Name = "Other help building" };
        var suite = new Suite { BuildingId = building.Id, Name = "Help suite" }; var otherSuite = new Suite { BuildingId = other.Id, Name = "Other suite" };
        var membership = Membership.Grant(suite.Id, tenantUser.Id, DateTimeOffset.UtcNow.AddDays(-1));
        var otherMembership = Membership.Grant(otherSuite.Id, outsiderUser.Id, DateTimeOffset.UtcNow.AddDays(-1));
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            db.AddRange(managerUser, tenantUser, outsiderUser, building, other, suite, otherSuite, membership, otherMembership,
                new BuildingManager { BuildingId = building.Id, UserId = managerUser.Id },
                new ActiveGrant { UserId = tenantUser.Id, SuiteId = suite.Id, MembershipId = membership.Id },
                new ActiveGrant { UserId = outsiderUser.Id, SuiteId = otherSuite.Id, MembershipId = otherMembership.Id });
            await db.SaveChangesAsync();
        }
        var manager = new Actor(managerUser.Id, new HashSet<Guid> { building.Id }, true, true);
        var tenant = new Actor(tenantUser.Id, new HashSet<Guid>(), false, false);
        var outsider = new Actor(outsiderUser.Id, new HashSet<Guid>(), false, false);
        async Task<T> As<T>(Actor? actor, Func<KnowledgeActions, Task<T>> call)
        {
            using var scope = provider.CreateScope(); scope.ServiceProvider.GetRequiredService<TestActor>().Actor = actor;
            return await call(scope.ServiceProvider.GetRequiredService<KnowledgeActions>());
        }
        var publicDraft = new DraftArticle(Guid.NewGuid(), building.Id, null, "Public guidance", "Approved public directions", ArticleAudience.Public, new DateOnly(2027, 1, 1));
        var privateDraft = publicDraft with { Id = Guid.NewGuid(), Title = "Tenant entry guidance", Body = "Private-needle directions", Audience = ArticleAudience.ActiveTenant };
        Assert.Equal(ContentCode.Unavailable, await As(tenant, a => a.SaveAsync(privateDraft)));
        Assert.Equal(ContentCode.Success, await As(manager, a => a.SaveAsync(publicDraft)));
        Assert.Equal(ContentCode.Success, await As(manager, a => a.SaveAsync(privateDraft)));
        Assert.Empty(await As(null, a => a.FindAsync(KnowledgeScope.Public, id: publicDraft.Id)));
        Assert.Empty(await As(tenant, a => a.FindAsync(KnowledgeScope.Tenant, id: privateDraft.Id)));
        async Task<ArticleEditor> Edit(Guid id) => (await As(manager, a => a.ManageAsync(id))).Single();
        async Task<ContentCode> Publish(Guid id)
        {
            var editor = await Edit(id);
            return await As(manager, a => a.PublishAsync(new(id, editor.Version, editor.Draft.Id)));
        }
        Assert.Equal(ContentCode.Success, await Publish(publicDraft.Id));
        Assert.Equal(ContentCode.Success, await Publish(privateDraft.Id));
        Assert.Single(await As(null, a => a.FindAsync(KnowledgeScope.Public, "public guidance")));
        Assert.Empty(await As(null, a => a.FindAsync(KnowledgeScope.Public, "Private-needle")));
        Assert.Single(await As(tenant, a => a.FindAsync(KnowledgeScope.Tenant, "private-NEEDLE")));
        Assert.Empty(await As(outsider, a => a.FindAsync(KnowledgeScope.Tenant, "private-needle")));
        var approved = await Edit(publicDraft.Id);
        Assert.Equal(ContentCode.Success, await As(manager, a => a.SaveAsync(publicDraft with { Version = approved.Version, Body = "Unapproved private changes", Audience = ArticleAudience.ActiveTenant })));
        Assert.Equal("Approved public directions", (await As(null, a => a.FindAsync(KnowledgeScope.Public, id: publicDraft.Id))).Single().Body);
        Assert.Empty(await As(null, a => a.FindAsync(KnowledgeScope.Public, "Unapproved")));
        var edited = await Edit(publicDraft.Id);
        Assert.Equal(2, edited.History.Count); Assert.All(edited.History, x => Assert.Equal(managerUser.Id, x.Revision.EditorId));
        Assert.Equal(ContentCode.Conflict, await As(manager, a => a.PublishAsync(new(approved.Id, approved.Version, approved.Draft.Id))));
        Assert.Equal(ContentCode.Conflict, await As(manager, a => a.PublishAsync(new(edited.Id, edited.Version, approved.Draft.Id))));
        Assert.Equal(ContentCode.Conflict, await As(manager, a => a.SaveAsync(publicDraft with { Version = approved.Version })));
        Assert.Equal(ContentCode.Success, await Publish(publicDraft.Id));
        Assert.Empty(await As(null, a => a.FindAsync(KnowledgeScope.Public, id: publicDraft.Id)));
        Assert.Equal("Unapproved private changes", (await As(tenant, a => a.FindAsync(KnowledgeScope.Tenant, id: publicDraft.Id))).Single().Body);
        var current = await Edit(publicDraft.Id);
        Assert.Equal(ContentCode.Success, await As(manager, a => a.PublishAsync(new(current.Id, current.Version, current.Draft.Id, Withdraw: true))));
        Assert.Empty(await As(tenant, a => a.FindAsync(KnowledgeScope.Tenant, id: publicDraft.Id)));
        Assert.Equal(ContentCode.Unavailable, await As(manager, a => a.SaveAsync(privateDraft with { Id = Guid.NewGuid(), BuildingId = other.Id })));
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
            var grant = await db.Memberships.SingleAsync(x => x.Id == membership.Id); grant.Revoke(managerUser.Id, "Access ended", DateTimeOffset.UtcNow);
            db.ActiveGrants.Remove(await db.ActiveGrants.SingleAsync(x => x.MembershipId == membership.Id)); await db.SaveChangesAsync();
            Assert.True(await db.AccountAudits.AnyAsync(x => x.Action == "HelpPublished" && x.SubjectId == publicDraft.Id.ToString("D") && x.Reason != null));
            Assert.Equal(2, await db.HelpRevisions.CountAsync(x => x.ArticleId == publicDraft.Id));
            db.BuildingManagers.Remove(await db.BuildingManagers.SingleAsync(x => x.BuildingId == building.Id && x.UserId == managerUser.Id)); await db.SaveChangesAsync();
        }
        Assert.Empty(await As(tenant, a => a.FindAsync(KnowledgeScope.Tenant, id: privateDraft.Id)));
        Assert.Empty(await As(manager, a => a.ManageAsync(privateDraft.Id)));
        Assert.Equal(ContentCode.Unavailable, await As(manager, a => a.SaveAsync(publicDraft with { Id = Guid.NewGuid() })));
    }
}
