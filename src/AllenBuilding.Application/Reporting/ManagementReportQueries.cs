using AllenBuilding.Application.Access;
using AllenBuilding.Domain.Maintenance;

namespace AllenBuilding.Application.Reporting;

public enum MaintenanceAgeBucket { Active, Under7Days, From7To30Days, AtLeast30Days, AwaitingVerification }
public sealed record MaintenanceAgingQuery(Guid? BuildingId = null, MaintenanceAgeBucket Bucket = MaintenanceAgeBucket.Active, int Page = 1)
{
    public const int PageSize = 50;
    public bool IsValid => BuildingId != Guid.Empty && Enum.IsDefined(Bucket) && Page is >= 1 and <= 1000;
}
public sealed record ReportBuilding(Guid Id, string Name);
public sealed record MaintenanceAgingTotals(long Under7Days, long From7To30Days, long AtLeast30Days, long AwaitingVerification)
{
    public long Active => Under7Days + From7To30Days + AtLeast30Days;
    public long For(MaintenanceAgeBucket bucket) => bucket switch
    {
        MaintenanceAgeBucket.Under7Days => Under7Days,
        MaintenanceAgeBucket.From7To30Days => From7To30Days,
        MaintenanceAgeBucket.AtLeast30Days => AtLeast30Days,
        MaintenanceAgeBucket.AwaitingVerification => AwaitingVerification,
        _ => Active
    };
}
public sealed record MaintenanceAgingItem(Guid Id, string Building, string Suite, string Title, WorkOrderStatus Status, DateTimeOffset CreatedAt);
public sealed record MaintenanceAgingReport(MaintenanceAgingQuery Query, DateTimeOffset ObservedAt, MaintenanceAgingTotals Totals,
    IReadOnlyList<MaintenanceAgingItem> Items, bool HasNext, IReadOnlyList<ReportBuilding> Buildings);
public interface IManagementReportReader
{
    Task<MaintenanceAgingReport?> MaintenanceAgingAsync(Actor actor, MaintenanceAgingQuery query, DateTimeOffset observedAt, CancellationToken cancellationToken);
}
public sealed class ManagementReportQueries(ICurrentActor current, IManagementReportReader reader, TimeProvider time)
{
    public async Task<MaintenanceAgingReport?> MaintenanceAgingAsync(MaintenanceAgingQuery query, CancellationToken cancellationToken = default)
    {
        if (!query.IsValid) return null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var actor = await current.GetAsync(linked.Token);
            if (actor is not { ChallengedMfa: true } || actor.ManagedBuildings.Count == 0) return null;
            var report = await reader.MaintenanceAgingAsync(actor, query, time.GetUtcNow(), linked.Token);
            // Release protected data only while the current ticket and every reported building remain authorized.
            var fresh = await current.GetAsync(linked.Token);
            return report is not null && fresh is { ChallengedMfa: true } && fresh.UserId == actor.UserId &&
                report.Buildings.All(x => fresh.ManagedBuildings.Contains(x.Id)) ? report : null;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("The management report exceeded its deadline."); }
    }
}
