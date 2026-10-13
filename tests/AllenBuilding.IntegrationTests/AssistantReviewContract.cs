using AllenBuilding.Application.Access;
using AllenBuilding.Application.Assistant;
using AllenBuilding.Application.Knowledge;
using AllenBuilding.Application.Maintenance;
using AllenBuilding.Domain.Knowledge;
using AllenBuilding.Domain.Maintenance;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace AllenBuilding.IntegrationTests;

public static class AssistantReviewContract
{
    public static async Task VerifyAsync(Func<OfficeDbContext> create)
    {
        var clock = new TestClock(); var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider()); services.AddScoped<OfficeDbContext>(_ => create());
        services.AddIdentityCore<OfficeUser>().AddEntityFrameworkStores<OfficeDbContext>().AddDefaultTokenProviders();
        services.AddScoped<TestActor>(); services.AddScoped<ICurrentActor>(sp => sp.GetRequiredService<TestActor>());
        services.AddScoped<AccountInvitations>(); services.AddScoped<AccessRevocation>(); services.AddScoped<IKnowledgeStore, KnowledgeStore>(); services.AddScoped<KnowledgeActions>();
        services.AddScoped<AssistantTenantAccess>(); services.AddScoped<IAssistantTenantAccess>(sp => sp.GetRequiredService<AssistantTenantAccess>());
        services.AddScoped<AssistantReviewStore>(); services.AddScoped<IAssistantReviewStore>(sp => sp.GetRequiredService<AssistantReviewStore>());
        services.AddScoped<IMaintenanceStore, MaintenanceStore>(); services.AddScoped<MaintenanceActions>(); services.AddScoped<AssistantMaintenance>();
        await using var provider = services.BuildServiceProvider();
        var user = new OfficeUser { UserName = "review-owner-" + Guid.NewGuid(), EmailConfirmed = true, SecurityStamp = Guid.NewGuid().ToString() };
        var other = new OfficeUser { UserName = "review-other-" + Guid.NewGuid(), EmailConfirmed = true, SecurityStamp = Guid.NewGuid().ToString() };
        var manager = new OfficeUser { UserName = "review-manager-" + Guid.NewGuid(), EmailConfirmed = true, SecurityStamp = Guid.NewGuid().ToString() };
        var building = new Building { Name = "Review contract" }; var suite = new Suite { BuildingId = building.Id, Name = "Review office" };
        var secondSuite = new Suite { BuildingId = building.Id, Name = "Second review office" };
        var member = Membership.Grant(suite.Id, user.Id, clock.GetUtcNow().AddDays(-1)); var second = Membership.Grant(secondSuite.Id, user.Id, clock.GetUtcNow().AddDays(-1));
        var sourceId = Guid.NewGuid(); var revision = HelpRevision.Create(sourceId, "Review source", "Private guidance", ArticleAudience.ActiveTenant, manager.Id, new(2027, 1, 1), clock.GetUtcNow());
        var article = HelpArticle.Create(sourceId, building.Id, revision.Id); article.Publish(revision.Id);
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>(); db.AddRange(user, other, manager, building, suite, secondSuite, member, second, article, revision,
                new ActiveGrant { UserId = user.Id, SuiteId = suite.Id, MembershipId = member.Id }, new ActiveGrant { UserId = user.Id, SuiteId = secondSuite.Id, MembershipId = second.Id },
                new BuildingManager { UserId = manager.Id, BuildingId = building.Id }); await db.SaveChangesAsync();
        }
        var actor = new Actor(user.Id, new HashSet<Guid>(), false, false); var managerActor = new Actor(manager.Id, new HashSet<Guid> { building.Id }, true, true);
        async Task<T> As<T>(Actor selected, Func<IServiceProvider, Task<T>> action)
        { using var scope = provider.CreateScope(); scope.ServiceProvider.GetRequiredService<TestActor>().Actor = selected; return await action(scope.ServiceProvider); }
        var access = (await As(actor, sp => sp.GetRequiredService<IAssistantTenantAccess>().CaptureAsync(member.Id, default)))!;
        var draft = new AssistantDraft(AssistantReviewKind.Request, "Review sink leak", "Water below the sink", "Kitchen");
        async Task<AssistantReviewView> Prepare(IReadOnlyList<AssistantSource>? sources = null)
            => (await As(actor, sp => sp.GetRequiredService<AssistantMaintenance>().PrepareAsync(Guid.NewGuid(), access, draft, null, sources ?? [], default))).View!;
        Task<MaintenanceResult> Confirm(AssistantReviewView view, Actor? selected = null) => As(selected ?? actor, sp => sp.GetRequiredService<AssistantMaintenance>().ConfirmAsync(new(view.Review.Id, view.Review.ConversationId, view.Review.Version), view.Review.OperationId, default));
        var pending = await Prepare(); Assert.NotNull(pending);
        await using (var db = create()) { Assert.Empty(await db.WorkOrders.Where(x => x.RequesterMembershipId == member.Id).ToListAsync()); Assert.Empty(await db.MaintenanceReceipts.Where(x => x.ActorId == user.Id).ToListAsync()); }
        Assert.Null(await As(managerActor, sp => sp.GetRequiredService<AssistantMaintenance>().ReadAsync(pending.Review.Id, pending.Review.ConversationId, default)));
        Assert.Equal(MaintenanceCode.Unavailable, (await Confirm(pending, new Actor(other.Id, new HashSet<Guid>(), false, false))).Code);
        Assert.Null(await As(actor, sp => sp.GetRequiredService<AssistantMaintenance>().ReadAsync(pending.Review.Id, Guid.NewGuid(), default)));
        Assert.Equal(MaintenanceCode.Conflict, (await As(actor, sp => sp.GetRequiredService<AssistantMaintenance>().ConfirmAsync(new(pending.Review.Id, pending.Review.ConversationId, Guid.NewGuid()), pending.Review.OperationId, default))).Code);
        Assert.Equal(MaintenanceCode.Unavailable, (await As(actor, sp => sp.GetRequiredService<MaintenanceActions>().SubmitAsync(AssistantMaintenance.Request(pending.Review)))).Code);
        // Independent double-confirm requests serialize and return one original committed receipt.
        var results = await Task.WhenAll(Confirm(pending), Confirm(pending)); Assert.All(results, x => Assert.Equal(MaintenanceCode.Success, x.Code)); Assert.Equal(results[0], results[1]);
        var result = results[0]; Assert.Equal(result, await Confirm(pending));
        await using (var db = create())
        {
            Assert.Single(await db.WorkOrders.Where(x => x.RequesterMembershipId == member.Id).ToListAsync());
            Assert.Single(await db.WorkOrderHistory.Where(x => x.WorkOrderId == result.WorkOrderId).ToListAsync());
            Assert.Single(await db.MaintenanceReceipts.Where(x => x.ActorId == user.Id).ToListAsync());
            Assert.Equal(2, await db.AccountMessages.CountAsync(x => x.BuildingId == building.Id));
            var saved = await db.AssistantMaintenanceReviews.SingleAsync(x => x.Id == pending.Review.Id); Assert.Equal(AssistantReviewState.Completed, saved.State); Assert.Equal(result.WorkOrderId, saved.ResultId);
        }
        var edit = await Prepare(); var old = new AssistantReviewClaim(edit.Review.Id, edit.Review.ConversationId, edit.Review.Version);
        Assert.Equal(MaintenanceCode.Success, await As(actor, sp => sp.GetRequiredService<AssistantMaintenance>().EditAsync(old, draft with { Title = "Edited request" }, false, default)));
        Assert.Equal(MaintenanceCode.Conflict, (await Confirm(edit)).Code);
        var edited = (await As(actor, sp => sp.GetRequiredService<AssistantMaintenance>().ReadAsync(old.Id, old.ConversationId, default)))!;
        Assert.NotEqual(edit.Review.OperationId, edited.Review.OperationId);
        Assert.Equal(MaintenanceCode.Success, await As(actor, sp => sp.GetRequiredService<AssistantMaintenance>().EditAsync(new(edited.Review.Id, edited.Review.ConversationId, edited.Review.Version), draft, true, default)));
        Assert.Equal(MaintenanceCode.Conflict, (await Confirm(edited)).Code);
        var expired = await Prepare(); clock.Offset = TimeSpan.FromMinutes(11); Assert.Equal(MaintenanceCode.Conflict, (await Confirm(expired)).Code); clock.Offset = TimeSpan.Zero;
        var withSource = await Prepare([new(article.Id, revision.Id, "", "")]);
        await using (var db = create()) { (await db.HelpArticles.SingleAsync(x => x.Id == article.Id)).Withdraw(); await db.SaveChangesAsync(); }
        Assert.Equal(MaintenanceCode.Unavailable, (await Confirm(withSource)).Code);
        // Comments use the same tenant-visible comment path and exact reviewed request version.
        var details = (await As(actor, sp => sp.GetRequiredService<MaintenanceActions>().ReadAsync(result.WorkOrderId!.Value, false)))!;
        Assert.NotNull(await As(actor, sp => sp.GetRequiredService<MaintenanceActions>().ReadForMembershipAsync(details.Id, member.Id)));
        Assert.Null(await As(actor, sp => sp.GetRequiredService<MaintenanceActions>().ReadForMembershipAsync(details.Id, second.Id)));
        var comment = (await As(actor, sp => sp.GetRequiredService<AssistantMaintenance>().PrepareAsync(Guid.NewGuid(), access, new(AssistantReviewKind.Comment, Comment: "Please call before arriving"), details, [], default))).View!;
        Assert.Equal(MaintenanceCode.Success, (await Confirm(comment)).Code); Assert.Equal(await Confirm(comment), await Confirm(comment));
        var stale = (await As(actor, sp => sp.GetRequiredService<AssistantMaintenance>().PrepareAsync(Guid.NewGuid(), access, new(AssistantReviewKind.Comment, Comment: "Old target"), details, [], default)));
        Assert.Equal(MaintenanceCode.Conflict, stale.Code);
        var freshDetails = (await As(actor, sp => sp.GetRequiredService<MaintenanceActions>().ReadAsync(result.WorkOrderId!.Value, false)))!;
        var staleReview = (await As(actor, sp => sp.GetRequiredService<AssistantMaintenance>().PrepareAsync(Guid.NewGuid(), access, new(AssistantReviewKind.Comment, Comment: "Reviewed target"), freshDetails, [], default))).View!;
        await As(actor, sp => sp.GetRequiredService<MaintenanceActions>().ChangeAsync(new(Guid.NewGuid(), freshDetails.Id, freshDetails.Version, MaintenanceAction.Comment, Note: "Another browser changed this request"), false));
        Assert.Equal(MaintenanceCode.Conflict, (await Confirm(staleReview)).Code);
        // Failure during the final save rolls back review completion, request, history, receipt and outbox.
        var rollback = await Prepare(); string connection; bool mysql; await using (var db = create()) { connection = db.Database.GetConnectionString()!; mysql = db is MySqlOfficeDbContext; }
        await using (var db = mysql ? (OfficeDbContext)new MySqlOfficeDbContext(new DbContextOptionsBuilder<MySqlOfficeDbContext>().UseMySQL(connection).AddInterceptors(new FailCompletion()).Options)
            : new SqliteOfficeDbContext(new DbContextOptionsBuilder<SqliteOfficeDbContext>().UseSqlite(connection).AddInterceptors(new FailCompletion()).Options))
        {
            var current = new TestActor { Actor = actor }; var audit = new AccountInvitations(db, null!, current, new EphemeralDataProtectionProvider(), clock);
            var knowledge = new KnowledgeActions(current, new KnowledgeStore(db, current, audit, clock)); var gate = new AssistantTenantAccess(db, current, knowledge, clock);
            var store = new AssistantReviewStore(db, current, gate, clock); var maintenance = new MaintenanceActions(current, new MaintenanceStore(db, current, audit, clock, store), clock);
            await Assert.ThrowsAsync<InvalidOperationException>(() => new AssistantMaintenance(store, maintenance).ConfirmAsync(new(rollback.Review.Id, rollback.Review.ConversationId, rollback.Review.Version), rollback.Review.OperationId, default));
        }
        await using (var db = create()) { Assert.Equal(AssistantReviewState.Ready, (await db.AssistantMaintenanceReviews.SingleAsync(x => x.Id == rollback.Review.Id)).State); Assert.False(await db.MaintenanceReceipts.AnyAsync(x => x.OperationId == rollback.Review.OperationId)); Assert.Equal(1, await db.WorkOrders.CountAsync(x => x.RequesterMembershipId == member.Id)); }
        Assert.Equal(MaintenanceCode.Success, (await Confirm(rollback)).Code);
        var revoked = await Prepare(); Assert.Equal(MaintenanceCode.Success, await As(managerActor, sp => sp.GetRequiredService<AccessRevocation>().RevokeAsync(member.Id, member.Version, "Synthetic tenancy ended")));
        Assert.Equal(MaintenanceCode.Unavailable, (await Confirm(revoked)).Code); Assert.Equal(MaintenanceCode.Unavailable, (await Confirm(pending)).Code);
        await using (var db = create()) { Assert.Equal(AssistantReviewState.Revoked, (await db.AssistantMaintenanceReviews.SingleAsync(x => x.Id == revoked.Review.Id)).State); Assert.True(await db.ActiveGrants.AnyAsync(x => x.MembershipId == second.Id)); }
    }
    private sealed class FailCompletion : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { if (eventData.Context!.ChangeTracker.Entries<AssistantMaintenanceReview>().Any(x => x.Entity.State == AssistantReviewState.Completed)) throw new InvalidOperationException("Synthetic completion save failure"); return ValueTask.FromResult(result); }
    }
}
