using AllenBuilding.Application.Access;
using AllenBuilding.Application.Reporting;
using AllenBuilding.Domain.Maintenance;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.IntegrationTests;

public static class CompletionReportingContract
{
    public static async Task VerifyAsync(Func<OfficeDbContext> create)
    {
        var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero); var start = now.AddDays(-10);
        var manager = new OfficeUser { UserName = "completion-" + Guid.NewGuid().ToString("N"), EmailConfirmed = true };
        var building = new Building { Name = "Measured workflow" }; var foreign = new Building { Name = "Unmanaged workflow" };
        var suite = new Suite { BuildingId = building.Id, Name = "Report original suite" }; var other = new Suite { BuildingId = foreign.Id, Name = "Foreign suite" };
        var member = Membership.Grant(suite.Id, manager.Id, start.AddDays(-1)); var otherMember = Membership.Grant(other.Id, manager.Id, start.AddDays(-1));
        await using (var db = create())
        {
            db.AddRange(manager, building, foreign, suite, other, member, otherMember, new BuildingManager { BuildingId = building.Id, UserId = manager.Id });
            for (var i = 0; i < 8; i++)
            {
                var order = WorkOrder.Submit(i == 7 ? other.Id : suite.Id, i == 7 ? otherMember.Id : member.Id, manager.Id, "Cycle " + i, "Original repair", "Office", start);
                db.Add(order);
                void Event(string action, double hours) => db.WorkOrderHistory.Add(new() { WorkOrderId = order.Id, ActorId = manager.Id, Action = action, Status = action == "Complete" ? WorkOrderStatus.Completed : WorkOrderStatus.Submitted, OccurredAt = start.AddHours(hours) });
                switch (i)
                {
                    case 0: Event("Complete", 1); Event("Close", 1.5); Event("Comment", 1.75); break;
                    case 1: Event("Complete", 3); Event("Close", 4); break;
                    case 2: Event("Complete", 8); break;
                    case 3: Event("Complete", 2); Event("Close", 4); Event("Reopen", 24); Event("Complete", 36); break;
                    case 4: break; // Missing completion is not a zero duration.
                    case 5: Event("Cancel", 5); break;
                    case 6: Event("Complete", 2); Event("Reopen", 2); break; // No fabricated UUID order.
                    case 7: Event("Complete", 200); Event("Close", 201); break;
                }
            }
            await db.SaveChangesAsync();
        }
        var actor = new Actor(manager.Id, new HashSet<Guid> { building.Id }, true, true);
        async Task<CompletionReport?> Read(Actor selected, Guid? target = null, DateTimeOffset? at = null) { await using var db = create(); return await new ManagementReportReader(db).ReadAsync(selected, new(target), at ?? now, default); }
        var report = (await Read(actor))!; Assert.NotNull(report); Assert.Equal(7, report.Cycles); Assert.Equal(1, report.Unresolved); Assert.Equal(1, report.Cancelled); Assert.Equal(1, report.AmbiguousRequests);
        Assert.Equal(5, report.Completion.Samples); Assert.Equal(3 * 3600, report.Completion.MedianSeconds); Assert.Equal(12 * 3600, report.Completion.P95Seconds);
        Assert.Equal(3, report.Verification.Samples); Assert.Equal(3600, report.Verification.MedianSeconds); Assert.Equal(2 * 3600, report.Verification.P95Seconds);
        Assert.Single(report.Buildings); Assert.DoesNotContain(report.Buildings, x => x.Id == foreign.Id);
        Assert.Null(await Read(actor, foreign.Id)); Assert.Null(await Read(actor with { ChallengedMfa = false })); Assert.Null(await Read(actor with { UserId = "wrong-reader" }));
        var early = (await Read(actor, building.Id, start.AddHours(1)))!; Assert.Equal(1, early.Completion.Samples); Assert.Equal(0, early.Verification.Samples); Assert.Null(early.Verification.MedianSeconds); Assert.Null(early.Verification.P95Seconds);
        var even = (await Read(actor, building.Id, start.AddHours(2)))!; Assert.Equal(2, even.Completion.Samples); Assert.Equal(1.5 * 3600, even.Completion.MedianSeconds);
        await using (var db = create()) { db.BuildingManagers.Remove(await db.BuildingManagers.SingleAsync(x => x.UserId == manager.Id && x.BuildingId == building.Id)); await db.SaveChangesAsync(); }
        Assert.Null(await Read(actor));
    }
}
